use resvg::{tiny_skia, usvg};
use tiny_skia::Transform;

// Serialises a usvg tree into the binary draw-command stream consumed by the C# side
// (SvgCommandListBuilder). The tree is walked the same way resvg's own renderer does
// (resvg/src/render.rs, clip.rs, path.rs): transforms are composed top-down, never taken
// from abs_transform().
//
// Stream layout (little-endian):
//   header : "FSV1", f32 width, f32 height, u32 totalPoints, u32 totalOps
//   ops    : u8 opcode + payload, terminated by END
//     1 PUSH_LAYER : f32 opacity, u32 nClip (0 = no clip), nClip x ClipEntry
//                    ClipEntry = mat, u8 fillRule, PathData
//     2 POP_LAYER
//     3 FILL       : mat, u8 fillRule, u8 aliased, Paint, PathData
//     4 STROKE     : mat, u8 aliased, Paint, f32 width, u8 cap, u8 join, f32 miterLimit,
//                    u32 nDash, f32 dash[nDash], f32 dashOffset, PathData
//     0 END
//   mat      : 6 x f32 in tiny-skia order sx ky kx sy tx ty
//   Paint    : u8 kind; 0 solid  f32 r,g,b,a (straight, 0..1)
//                       1 linear f32 x1,y1,x2,y2, mat, u8 spread, f32 opacityMul, u32 nStops, stops
//                       2 radial f32 cx,cy,r,fx,fy, mat, u8 spread, f32 opacityMul, u32 nStops, stops
//              stop = f32 offset, f32 r,g,b,a
//   PathData : u32 nVerbs, u8 verbs[] (0 Move,1 Line,2 Quad,3 Cubic,4 Close), u32 nPoints, f32 x,y pairs

const MAGIC: &[u8; 4] = b"FSV1";

const OP_END: u8 = 0;
const OP_PUSH_LAYER: u8 = 1;
const OP_POP_LAYER: u8 = 2;
const OP_FILL: u8 = 3;
const OP_STROKE: u8 = 4;

// Reason codes reported to C# when the file has to fall back to the raster.
pub const REASON_FILTER: i32 = 1;
pub const REASON_MASK: i32 = 2;
pub const REASON_BLEND_MODE: i32 = 3;
pub const REASON_PATTERN: i32 = 4;
pub const REASON_IMAGE: i32 = 5;
pub const REASON_CLIP_ON_CLIP_CHILD: i32 = 6;
pub const REASON_RADIAL_FOCAL_RADIUS: i32 = 7;
pub const REASON_TOO_COMPLEX: i32 = 8;
pub const REASON_OTHER: i32 = 9;

const MAX_POINTS: u32 = 200_000;
const MAX_OPS: u32 = 20_000;

type Unsupported = i32;

struct Writer {
    buf: Vec<u8>,
    total_points: u32,
    total_ops: u32,
}

/// Builds the command stream for `tree`. `Err(reason)` means the tree uses a feature the
/// command stream cannot express (see the REASON_* codes) and the raster must be used instead.
pub fn build_commands(tree: &usvg::Tree) -> Result<Vec<u8>, Unsupported> {
    let mut w = Writer {
        buf: Vec::new(),
        total_points: 0,
        total_ops: 0,
    };

    // resvg::render iterates the root's children and ignores the root group's own transform.
    w.walk(tree.root(), Transform::identity())?;
    w.buf.push(OP_END);

    let mut out = Vec::with_capacity(w.buf.len() + 20);
    out.extend_from_slice(MAGIC);
    put_f32(&mut out, tree.size().width());
    put_f32(&mut out, tree.size().height());
    put_u32(&mut out, w.total_points);
    put_u32(&mut out, w.total_ops);
    out.extend_from_slice(&w.buf);
    Ok(out)
}

fn put_u8(buf: &mut Vec<u8>, v: u8) {
    buf.push(v);
}

fn put_u32(buf: &mut Vec<u8>, v: u32) {
    buf.extend_from_slice(&v.to_le_bytes());
}

fn put_f32(buf: &mut Vec<u8>, v: f32) {
    buf.extend_from_slice(&v.to_le_bytes());
}

fn put_mat(buf: &mut Vec<u8>, ts: Transform) {
    // tiny-skia order: sx ky kx sy tx ty
    for v in [ts.sx, ts.ky, ts.kx, ts.sy, ts.tx, ts.ty] {
        put_f32(buf, v);
    }
}

fn put_fill_rule(buf: &mut Vec<u8>, rule: usvg::FillRule) {
    put_u8(
        buf,
        match rule {
            usvg::FillRule::NonZero => 0,
            usvg::FillRule::EvenOdd => 1,
        },
    );
}

fn put_color(buf: &mut Vec<u8>, c: usvg::Color, a: f32) {
    put_f32(buf, c.red as f32 / 255.0);
    put_f32(buf, c.green as f32 / 255.0);
    put_f32(buf, c.blue as f32 / 255.0);
    put_f32(buf, a);
}

/// Mirrors the early-outs of resvg's `fill_path`: a path whose bounds have zero width or
/// height (horizontal / vertical line) cannot be filled.
fn is_fillable(path: &usvg::Path) -> bool {
    let b = path.data().bounds();
    b.width() != 0.0 && b.height() != 0.0
}

/// A group with nothing but a transform: no layer, no effects (what `should_isolate` rejects).
fn is_plain_group(g: &usvg::Group) -> bool {
    g.opacity().get() == 1.0
        && g.clip_path().is_none()
        && g.mask().is_none()
        && g.filters().is_empty()
        && g.blend_mode() == usvg::BlendMode::Normal
}

/// If the subtree of `group` is exactly one path with only a fill or only a stroke (possibly
/// through plain transform-only groups), returns that path and its composed transform.
/// Such a group's opacity can be folded into the paint instead of using a layer.
fn foldable_path(group: &usvg::Group, ts: Transform) -> Option<(&usvg::Path, Transform)> {
    match group.children() {
        [usvg::Node::Path(p)] if p.fill().is_some() != p.stroke().is_some() => Some((p, ts)),
        [usvg::Node::Group(g)] if is_plain_group(g) => {
            foldable_path(g, ts.pre_concat(g.transform()))
        }
        _ => None,
    }
}

struct ClipEntry<'a> {
    ts: Transform,
    rule: usvg::FillRule,
    data: &'a tiny_skia::Path,
}

/// Collects the fill geometry of a clip path's content, mirroring `draw_children` in resvg's
/// clip.rs: stroke is ignored, every path uses its own fill rule, groups compose transforms
/// and text goes through its flattened group.
fn collect_clip<'a>(
    parent: &'a usvg::Group,
    ts: Transform,
    out: &mut Vec<ClipEntry<'a>>,
) -> Result<(), Unsupported> {
    for child in parent.children() {
        match child {
            usvg::Node::Path(path) => {
                if !path.is_visible() {
                    continue;
                }
                let Some(fill) = path.fill() else { continue };
                if !is_fillable(path) {
                    continue;
                }
                out.push(ClipEntry {
                    ts,
                    rule: fill.rule(),
                    data: path.data(),
                });
            }
            usvg::Node::Text(text) => collect_clip(text.flattened(), ts, out)?,
            usvg::Node::Group(group) => {
                if group.clip_path().is_some() {
                    return Err(REASON_CLIP_ON_CLIP_CHILD);
                }
                collect_clip(group, ts.pre_concat(group.transform()), out)?;
            }
            _ => {}
        }
    }
    Ok(())
}

impl Writer {
    fn count_op(&mut self) -> Result<(), Unsupported> {
        self.total_ops += 1;
        if self.total_ops > MAX_OPS {
            return Err(REASON_TOO_COMPLEX);
        }
        Ok(())
    }

    fn put_path_data(&mut self, buf_target: &mut Vec<u8>, data: &tiny_skia::Path) -> Result<(), Unsupported> {
        let verbs = data.verbs();
        let points = data.points();

        let expected: usize = verbs
            .iter()
            .map(|v| match v {
                usvg::tiny_skia_path::PathVerb::Move | usvg::tiny_skia_path::PathVerb::Line => 1,
                usvg::tiny_skia_path::PathVerb::Quad => 2,
                usvg::tiny_skia_path::PathVerb::Cubic => 3,
                usvg::tiny_skia_path::PathVerb::Close => 0,
            })
            .sum();
        if expected != points.len() {
            return Err(REASON_OTHER);
        }

        self.total_points = self.total_points.saturating_add(points.len() as u32);
        if self.total_points > MAX_POINTS {
            return Err(REASON_TOO_COMPLEX);
        }

        put_u32(buf_target, verbs.len() as u32);
        for v in verbs {
            put_u8(
                buf_target,
                match v {
                    usvg::tiny_skia_path::PathVerb::Move => 0,
                    usvg::tiny_skia_path::PathVerb::Line => 1,
                    usvg::tiny_skia_path::PathVerb::Quad => 2,
                    usvg::tiny_skia_path::PathVerb::Cubic => 3,
                    usvg::tiny_skia_path::PathVerb::Close => 4,
                },
            );
        }
        put_u32(buf_target, points.len() as u32);
        for p in points {
            put_f32(buf_target, p.x);
            put_f32(buf_target, p.y);
        }
        Ok(())
    }

    fn walk(&mut self, group: &usvg::Group, ts: Transform) -> Result<(), Unsupported> {
        for node in group.children() {
            match node {
                usvg::Node::Group(g) => self.group(g, ts)?,
                usvg::Node::Path(p) => self.path(p, ts, 1.0)?,
                usvg::Node::Text(t) => self.group(t.flattened(), ts)?,
                usvg::Node::Image(_) => return Err(REASON_IMAGE),
            }
        }
        Ok(())
    }

    fn group(&mut self, g: &usvg::Group, ts: Transform) -> Result<(), Unsupported> {
        if !g.filters().is_empty() {
            return Err(REASON_FILTER);
        }
        if g.mask().is_some() {
            return Err(REASON_MASK);
        }
        if g.blend_mode() != usvg::BlendMode::Normal {
            return Err(REASON_BLEND_MODE);
        }

        // Transform of the group's content (resvg: `transform.pre_concat(group.transform())`).
        let ts = ts.pre_concat(g.transform());
        let opacity = g.opacity().get();

        // Without a clip and without opacity the group needs no layer (`isolate` is ignored).
        if g.clip_path().is_none() && opacity >= 1.0 {
            return self.walk(g, ts);
        }

        if g.clip_path().is_none() {
            if let Some((path, path_ts)) = foldable_path(g, ts) {
                return self.path(path, path_ts, opacity);
            }
        }

        // One layer per clip in the chain; nested layers intersect. Every clip in the chain is
        // positioned with this group's transform (clip.rs passes the outer `transform` down
        // to the nested clip, not the clip's own transform).
        let mut chain: Vec<Vec<ClipEntry>> = Vec::new();
        let mut clip = g.clip_path();
        while let Some(c) = clip {
            let mut entries = Vec::new();
            collect_clip(c.root(), ts.pre_concat(c.transform()), &mut entries)?;
            if entries.is_empty() {
                // Empty clip region: nothing of the group is visible.
                return Ok(());
            }
            chain.push(entries);
            clip = c.clip_path();
        }

        if chain.is_empty() {
            self.push_layer(opacity, &[])?;
            self.walk(g, ts)?;
            return self.pop_layer();
        }

        for (i, entries) in chain.iter().enumerate() {
            self.push_layer(if i == 0 { opacity } else { 1.0 }, entries)?;
        }
        self.walk(g, ts)?;
        for _ in &chain {
            self.pop_layer()?;
        }
        Ok(())
    }

    fn push_layer(&mut self, opacity: f32, clip: &[ClipEntry]) -> Result<(), Unsupported> {
        self.count_op()?;
        let mut op = Vec::new();
        put_u8(&mut op, OP_PUSH_LAYER);
        put_f32(&mut op, opacity);
        put_u32(&mut op, clip.len() as u32);
        for e in clip {
            put_mat(&mut op, e.ts);
            put_fill_rule(&mut op, e.rule);
            self.put_path_data(&mut op, e.data)?;
        }
        self.buf.extend_from_slice(&op);
        Ok(())
    }

    fn pop_layer(&mut self) -> Result<(), Unsupported> {
        self.count_op()?;
        self.buf.push(OP_POP_LAYER);
        Ok(())
    }

    /// `opacity_mul` is the group opacity folded into the paint (1.0 when there is none).
    fn path(&mut self, path: &usvg::Path, ts: Transform, opacity_mul: f32) -> Result<(), Unsupported> {
        if !path.is_visible() {
            return Ok(());
        }

        if path.paint_order() == usvg::PaintOrder::FillAndStroke {
            self.fill(path, ts, opacity_mul)?;
            self.stroke(path, ts, opacity_mul)
        } else {
            self.stroke(path, ts, opacity_mul)?;
            self.fill(path, ts, opacity_mul)
        }
    }

    fn fill(&mut self, path: &usvg::Path, ts: Transform, opacity_mul: f32) -> Result<(), Unsupported> {
        let Some(fill) = path.fill() else { return Ok(()) };
        if !is_fillable(path) {
            return Ok(());
        }
        let Some(paint) = paint_bytes(fill.paint(), fill.opacity().get() * opacity_mul)? else {
            return Ok(());
        };

        self.count_op()?;
        let mut op = Vec::new();
        put_u8(&mut op, OP_FILL);
        put_mat(&mut op, ts);
        put_fill_rule(&mut op, fill.rule());
        put_u8(&mut op, aliased(path));
        op.extend_from_slice(&paint);
        self.put_path_data(&mut op, path.data())?;
        self.buf.extend_from_slice(&op);
        Ok(())
    }

    fn stroke(&mut self, path: &usvg::Path, ts: Transform, opacity_mul: f32) -> Result<(), Unsupported> {
        let Some(stroke) = path.stroke() else { return Ok(()) };
        let Some(paint) = paint_bytes(stroke.paint(), stroke.opacity().get() * opacity_mul)? else {
            return Ok(());
        };

        self.count_op()?;
        let mut op = Vec::new();
        put_u8(&mut op, OP_STROKE);
        put_mat(&mut op, ts);
        put_u8(&mut op, aliased(path));
        op.extend_from_slice(&paint);
        put_f32(&mut op, stroke.width().get());
        put_u8(
            &mut op,
            match stroke.linecap() {
                usvg::LineCap::Butt => 0,
                usvg::LineCap::Round => 1,
                usvg::LineCap::Square => 2,
            },
        );
        put_u8(
            &mut op,
            match stroke.linejoin() {
                usvg::LineJoin::Miter => 0,
                usvg::LineJoin::MiterClip => 1,
                usvg::LineJoin::Round => 2,
                usvg::LineJoin::Bevel => 3,
            },
        );
        put_f32(&mut op, stroke.miterlimit().get());
        let dash = stroke.dasharray().unwrap_or(&[]);
        put_u32(&mut op, dash.len() as u32);
        for d in dash {
            put_f32(&mut op, *d);
        }
        put_f32(&mut op, stroke.dashoffset());
        self.put_path_data(&mut op, path.data())?;
        self.buf.extend_from_slice(&op);
        Ok(())
    }
}

fn aliased(path: &usvg::Path) -> u8 {
    // Same switch resvg uses for `paint.anti_alias` (optimizeSpeed and crispEdges both disable AA).
    (!path.rendering_mode().use_shape_antialiasing()) as u8
}

/// Serialises a paint. `opacity` is the paint opacity (already multiplied by any folded group
/// opacity). Returns `Ok(None)` when resvg would draw nothing (gradient without stops).
fn paint_bytes(paint: &usvg::Paint, opacity: f32) -> Result<Option<Vec<u8>>, Unsupported> {
    let mut b = Vec::new();
    match paint {
        usvg::Paint::Color(c) => {
            put_u8(&mut b, 0);
            put_color(&mut b, *c, opacity);
        }
        usvg::Paint::LinearGradient(lg) => {
            if let Some(solid) = single_stop_color(lg.stops(), opacity) {
                return Ok(solid);
            }
            put_u8(&mut b, 1);
            put_f32(&mut b, lg.x1());
            put_f32(&mut b, lg.y1());
            put_f32(&mut b, lg.x2());
            put_f32(&mut b, lg.y2());
            put_gradient_tail(&mut b, lg.transform(), lg.spread_method(), lg.stops(), opacity);
        }
        usvg::Paint::RadialGradient(rg) => {
            if rg.fr().get() > 0.0 {
                return Err(REASON_RADIAL_FOCAL_RADIUS);
            }
            if let Some(solid) = single_stop_color(rg.stops(), opacity) {
                return Ok(solid);
            }
            put_u8(&mut b, 2);
            put_f32(&mut b, rg.cx());
            put_f32(&mut b, rg.cy());
            put_f32(&mut b, rg.r().get());
            put_f32(&mut b, rg.fx());
            put_f32(&mut b, rg.fy());
            put_gradient_tail(&mut b, rg.transform(), rg.spread_method(), rg.stops(), opacity);
        }
        usvg::Paint::Pattern(_) => return Err(REASON_PATTERN),
    }
    Ok(Some(b))
}

/// Gradients with fewer than two stops are not gradients: none draws nothing, one is a solid
/// color. Returns `Some(result)` when it applies, `None` for a regular gradient.
fn single_stop_color(stops: &[usvg::Stop], opacity: f32) -> Option<Option<Vec<u8>>> {
    match stops {
        [] => Some(None),
        [s] => {
            let mut b = Vec::new();
            put_u8(&mut b, 0);
            put_color(&mut b, s.color(), s.opacity().get() * opacity);
            Some(Some(b))
        }
        _ => None,
    }
}

fn put_gradient_tail(
    b: &mut Vec<u8>,
    ts: Transform,
    spread: usvg::SpreadMethod,
    stops: &[usvg::Stop],
    opacity: f32,
) {
    put_mat(b, ts);
    put_u8(
        b,
        match spread {
            usvg::SpreadMethod::Pad => 0,
            usvg::SpreadMethod::Reflect => 1,
            usvg::SpreadMethod::Repeat => 2,
        },
    );
    put_f32(b, opacity);
    put_u32(b, stops.len() as u32);
    for s in stops {
        put_f32(b, s.offset().get());
        put_color(b, s.color(), s.opacity().get());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ---- test-side reader of the binary format -------------------------------------------

    struct Reader<'a> {
        d: &'a [u8],
        p: usize,
    }

    impl<'a> Reader<'a> {
        fn u8(&mut self) -> u8 {
            let v = self.d[self.p];
            self.p += 1;
            v
        }
        fn u32(&mut self) -> u32 {
            let v = u32::from_le_bytes(self.d[self.p..self.p + 4].try_into().unwrap());
            self.p += 4;
            v
        }
        fn f32(&mut self) -> f32 {
            let v = f32::from_le_bytes(self.d[self.p..self.p + 4].try_into().unwrap());
            self.p += 4;
            v
        }
        fn mat(&mut self) -> [f32; 6] {
            [self.f32(), self.f32(), self.f32(), self.f32(), self.f32(), self.f32()]
        }
    }

    #[derive(Debug, PartialEq)]
    struct PathData {
        verbs: Vec<u8>,
        points: Vec<(f32, f32)>,
    }

    #[derive(Debug, PartialEq)]
    enum P {
        Solid([f32; 4]),
        Linear { line: [f32; 4], mat: [f32; 6], spread: u8, opacity: f32, stops: Vec<(f32, [f32; 4])> },
        Radial { circle: [f32; 5], mat: [f32; 6], spread: u8, opacity: f32, stops: Vec<(f32, [f32; 4])> },
    }

    #[derive(Debug, PartialEq)]
    struct Clip {
        mat: [f32; 6],
        rule: u8,
        path: PathData,
    }

    #[derive(Debug, PartialEq)]
    enum Op {
        Push { opacity: f32, clip: Vec<Clip> },
        Pop,
        Fill { mat: [f32; 6], rule: u8, aliased: u8, paint: P, path: PathData },
        Stroke {
            mat: [f32; 6],
            aliased: u8,
            paint: P,
            width: f32,
            cap: u8,
            join: u8,
            miter: f32,
            dash: Vec<f32>,
            dash_offset: f32,
            path: PathData,
        },
    }

    struct Parsed {
        width: f32,
        height: f32,
        points: u32,
        ops_count: u32,
        ops: Vec<Op>,
    }

    fn read_path(r: &mut Reader) -> PathData {
        let nv = r.u32() as usize;
        let verbs = (0..nv).map(|_| r.u8()).collect();
        let np = r.u32() as usize;
        let points = (0..np).map(|_| (r.f32(), r.f32())).collect();
        PathData { verbs, points }
    }

    fn read_stops(r: &mut Reader) -> Vec<(f32, [f32; 4])> {
        let n = r.u32() as usize;
        (0..n).map(|_| (r.f32(), [r.f32(), r.f32(), r.f32(), r.f32()])).collect()
    }

    fn read_paint(r: &mut Reader) -> P {
        match r.u8() {
            0 => P::Solid([r.f32(), r.f32(), r.f32(), r.f32()]),
            1 => {
                let line = [r.f32(), r.f32(), r.f32(), r.f32()];
                let mat = r.mat();
                let spread = r.u8();
                let opacity = r.f32();
                P::Linear { line, mat, spread, opacity, stops: read_stops(r) }
            }
            2 => {
                let circle = [r.f32(), r.f32(), r.f32(), r.f32(), r.f32()];
                let mat = r.mat();
                let spread = r.u8();
                let opacity = r.f32();
                P::Radial { circle, mat, spread, opacity, stops: read_stops(r) }
            }
            k => panic!("bad paint kind {k}"),
        }
    }

    fn parse(d: &[u8]) -> Parsed {
        assert_eq!(&d[0..4], b"FSV1");
        let mut r = Reader { d, p: 4 };
        let width = r.f32();
        let height = r.f32();
        let points = r.u32();
        let ops_count = r.u32();
        let mut ops = Vec::new();
        loop {
            match r.u8() {
                0 => break,
                1 => {
                    let opacity = r.f32();
                    let n = r.u32();
                    let clip = (0..n)
                        .map(|_| Clip { mat: r.mat(), rule: r.u8(), path: read_path(&mut r) })
                        .collect();
                    ops.push(Op::Push { opacity, clip });
                }
                2 => ops.push(Op::Pop),
                3 => {
                    let mat = r.mat();
                    let rule = r.u8();
                    let aliased = r.u8();
                    let paint = read_paint(&mut r);
                    let path = read_path(&mut r);
                    ops.push(Op::Fill { mat, rule, aliased, paint, path });
                }
                4 => {
                    let mat = r.mat();
                    let aliased = r.u8();
                    let paint = read_paint(&mut r);
                    let width = r.f32();
                    let cap = r.u8();
                    let join = r.u8();
                    let miter = r.f32();
                    let nd = r.u32() as usize;
                    let dash = (0..nd).map(|_| r.f32()).collect();
                    let dash_offset = r.f32();
                    let path = read_path(&mut r);
                    ops.push(Op::Stroke { mat, aliased, paint, width, cap, join, miter, dash, dash_offset, path });
                }
                o => panic!("bad opcode {o}"),
            }
        }
        assert_eq!(r.p, d.len(), "trailing bytes after END");
        Parsed { width, height, points, ops_count, ops }
    }

    fn build_with(svg: &str, options: &usvg::Options) -> Result<Parsed, i32> {
        let tree = usvg::Tree::from_str(svg, options).unwrap();
        build_commands(&tree).map(|b| parse(&b))
    }

    fn build(svg: &str) -> Result<Parsed, i32> {
        build_with(svg, &usvg::Options::default())
    }

    fn ok(svg: &str) -> Parsed {
        build(svg).unwrap_or_else(|r| panic!("unsupported, reason {r}"))
    }

    fn assert_mat(actual: [f32; 6], expected: [f32; 6]) {
        for (a, e) in actual.iter().zip(expected.iter()) {
            assert!((a - e).abs() < 1e-4, "matrix {actual:?} != {expected:?}");
        }
    }

    fn assert_close(a: f32, e: f32) {
        assert!((a - e).abs() < 1e-4, "{a} != {e}");
    }

    const IDENTITY: [f32; 6] = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];

    // ---- tests ---------------------------------------------------------------------------

    #[test]
    fn rect_fill() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <rect x="10" y="20" width="30" height="40" fill="#ff8000"/></svg>"##);
        assert_eq!((p.width, p.height), (100.0, 50.0));
        assert_eq!(p.ops.len(), 1);
        assert_eq!(p.ops_count, 1);
        assert_eq!(p.points, 4);
        let Op::Fill { mat, rule, aliased, paint, path } = &p.ops[0] else { panic!("{:?}", p.ops[0]) };
        assert_mat(*mat, IDENTITY);
        assert_eq!((*rule, *aliased), (0, 0));
        let P::Solid(c) = paint else { panic!() };
        assert_close(c[0], 1.0);
        assert_close(c[1], 128.0 / 255.0);
        assert_close(c[2], 0.0);
        assert_close(c[3], 1.0);
        assert_eq!(path.verbs, vec![0, 1, 1, 1, 4]);
        assert_eq!(path.points, vec![(10.0, 20.0), (40.0, 20.0), (40.0, 60.0), (10.0, 60.0)]);
    }

    #[test]
    fn path_with_cubic_and_close() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <path d="M 10 10 C 20 0 40 0 50 10 L 50 50 Z" fill="black" fill-rule="evenodd"/></svg>"##);
        let Op::Fill { rule, path, .. } = &p.ops[0] else { panic!() };
        assert_eq!(*rule, 1);
        assert_eq!(path.verbs, vec![0, 3, 1, 4]);
        assert_eq!(
            path.points,
            vec![(10.0, 10.0), (20.0, 0.0), (40.0, 0.0), (50.0, 10.0), (50.0, 50.0)]
        );
        assert_eq!(p.points, 5);
    }

    #[test]
    fn linear_and_radial_gradient() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <defs>
              <linearGradient id="l" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0" spreadMethod="reflect">
                <stop offset="0" stop-color="#ff0000"/>
                <stop offset="1" stop-color="#0000ff" stop-opacity="0.5"/>
              </linearGradient>
              <radialGradient id="r" gradientUnits="userSpaceOnUse" cx="50" cy="50" r="40" fx="45" fy="55" spreadMethod="repeat">
                <stop offset="0" stop-color="#ffffff"/>
                <stop offset="1" stop-color="#000000"/>
              </radialGradient>
            </defs>
            <rect width="100" height="50" fill="url(#l)" fill-opacity="0.8"/>
            <rect y="50" width="100" height="50" fill="url(#r)"/></svg>"##);
        assert_eq!(p.ops.len(), 2);
        let Op::Fill { paint, .. } = &p.ops[0] else { panic!() };
        let P::Linear { line, mat, spread, opacity, stops } = paint else { panic!("{paint:?}") };
        assert_eq!(*line, [0.0, 0.0, 100.0, 0.0]);
        assert_mat(*mat, IDENTITY);
        assert_eq!(*spread, 1);
        assert_close(*opacity, 0.8);
        assert_eq!(stops.len(), 2);
        assert_eq!(stops[0], (0.0, [1.0, 0.0, 0.0, 1.0]));
        assert_eq!(stops[1], (1.0, [0.0, 0.0, 1.0, 0.5]));

        let Op::Fill { paint, .. } = &p.ops[1] else { panic!() };
        let P::Radial { circle, spread, opacity, stops, .. } = paint else { panic!("{paint:?}") };
        assert_eq!(*circle, [50.0, 50.0, 40.0, 45.0, 55.0]);
        assert_eq!(*spread, 2);
        assert_close(*opacity, 1.0);
        assert_eq!(stops[0], (0.0, [1.0, 1.0, 1.0, 1.0]));
        assert_eq!(stops[1], (1.0, [0.0, 0.0, 0.0, 1.0]));
    }

    #[test]
    fn stroke_with_dash() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <path d="M 10 10 L 90 10" fill="none" stroke="#00ff00" stroke-width="4"
                  stroke-linecap="round" stroke-linejoin="bevel" stroke-miterlimit="7"
                  stroke-dasharray="5 3" stroke-dashoffset="2" shape-rendering="crispEdges"/></svg>"##);
        assert_eq!(p.ops.len(), 1);
        let Op::Stroke { mat, aliased, paint, width, cap, join, miter, dash, dash_offset, path } = &p.ops[0]
        else { panic!("{:?}", p.ops[0]) };
        assert_mat(*mat, IDENTITY);
        assert_eq!(*aliased, 1);
        assert_eq!(*paint, P::Solid([0.0, 1.0, 0.0, 1.0]));
        assert_close(*width, 4.0);
        assert_eq!((*cap, *join), (1, 3));
        assert_close(*miter, 7.0);
        assert_eq!(*dash, vec![5.0, 3.0]);
        assert_close(*dash_offset, 2.0);
        assert_eq!(path.verbs, vec![0, 1]);
        assert_eq!(path.points, vec![(10.0, 10.0), (90.0, 10.0)]);
    }

    #[test]
    fn group_opacity_folded_single_fill() {
        // One path with only a fill: opacity goes into the paint alpha, no layer.
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <g opacity="0.5"><rect width="10" height="10" fill="#ff0000" fill-opacity="0.5"/></g></svg>"##);
        assert_eq!(p.ops.len(), 1);
        let Op::Fill { paint, .. } = &p.ops[0] else { panic!("{:?}", p.ops[0]) };
        let P::Solid(c) = paint else { panic!() };
        assert_close(c[3], 0.25);
    }

    #[test]
    fn group_opacity_folded_into_gradient_opacity_mul() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <defs><linearGradient id="l"><stop offset="0" stop-color="#000"/><stop offset="1" stop-color="#fff"/></linearGradient></defs>
            <g opacity="0.5"><rect width="10" height="10" fill="url(#l)"/></g></svg>"##);
        assert_eq!(p.ops.len(), 1);
        let Op::Fill { paint: P::Linear { opacity, .. }, .. } = &p.ops[0] else { panic!("{:?}", p.ops[0]) };
        assert_close(*opacity, 0.5);
    }

    #[test]
    fn group_opacity_layer_when_fill_and_stroke() {
        // Fill + stroke overlap, so folding would change the result: needs a real layer.
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <g opacity="0.5" transform="translate(5 5)">
              <rect width="10" height="10" fill="#ff0000" stroke="#0000ff" stroke-width="2"/>
            </g></svg>"##);
        assert_eq!(p.ops.len(), 4);
        let Op::Push { opacity, clip } = &p.ops[0] else { panic!("{:?}", p.ops[0]) };
        assert_close(*opacity, 0.5);
        assert!(clip.is_empty());
        let Op::Fill { mat, paint, .. } = &p.ops[1] else { panic!() };
        assert_mat(*mat, [1.0, 0.0, 0.0, 1.0, 5.0, 5.0]);
        // Paint alpha is untouched: the layer carries the opacity.
        assert_eq!(*paint, P::Solid([1.0, 0.0, 0.0, 1.0]));
        assert!(matches!(p.ops[2], Op::Stroke { .. }));
        assert_eq!(p.ops[3], Op::Pop);
        assert_eq!(p.ops_count, 4);
    }

    #[test]
    fn group_opacity_layer_with_two_children() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <g opacity="0.3"><rect width="10" height="10"/><rect x="20" width="10" height="10"/></g></svg>"##);
        assert_eq!(p.ops.len(), 4);
        assert!(matches!(p.ops[0], Op::Push { .. }));
        assert_eq!(p.ops[3], Op::Pop);
    }

    #[test]
    fn clip_path_on_transformed_group() {
        // group transform T = translate(10 20) scale(2); clipPath transform C = translate(3 4);
        // clip child rect has its own transform G = translate(1 1).
        // Clip entry must be T * C * G, content must be drawn with T only.
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
            <defs><clipPath id="c" transform="translate(3 4)">
              <rect transform="translate(1 1)" width="5" height="6"/>
            </clipPath></defs>
            <g transform="translate(10 20) scale(2)" clip-path="url(#c)">
              <rect width="50" height="50" fill="#123456"/>
            </g></svg>"##);
        assert_eq!(p.ops.len(), 3);
        let Op::Push { opacity, clip } = &p.ops[0] else { panic!("{:?}", p.ops[0]) };
        assert_close(*opacity, 1.0);
        assert_eq!(clip.len(), 1);
        // sx ky kx sy tx ty: scale 2, translate = (10,20) + 2*((3,4)+(1,1)) = (18, 30)
        assert_mat(clip[0].mat, [2.0, 0.0, 0.0, 2.0, 18.0, 30.0]);
        assert_eq!(clip[0].rule, 0);
        assert_eq!(clip[0].path.points[0], (0.0, 0.0));
        let Op::Fill { mat, .. } = &p.ops[1] else { panic!() };
        assert_mat(*mat, [2.0, 0.0, 0.0, 2.0, 10.0, 20.0]);
        assert_eq!(p.ops[2], Op::Pop);
    }

    #[test]
    fn clip_uses_fill_geometry_and_own_rule() {
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <defs><clipPath id="c">
              <rect width="10" height="10" clip-rule="evenodd" stroke="red" stroke-width="5"/>
              <circle cx="50" cy="50" r="5"/>
            </clipPath></defs>
            <rect width="100" height="100" fill="blue" clip-path="url(#c)"/></svg>"##);
        let Op::Push { clip, .. } = &p.ops[0] else { panic!("{:?}", p.ops[0]) };
        assert_eq!(clip.len(), 2);
        assert_eq!(clip[0].rule, 1);
        assert_eq!(clip[1].rule, 0);
        assert_eq!(clip[0].path.points.len(), 4);
    }

    #[test]
    fn nested_clip_uses_outer_group_transform() {
        // The nested clip (c2) is positioned by the group transform T, not by c1's transform.
        let p = ok(r##"<svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
            <defs>
              <clipPath id="c2"><rect width="8" height="8"/></clipPath>
              <clipPath id="c1" clip-path="url(#c2)" transform="translate(100 100)"><rect width="9" height="9"/></clipPath>
            </defs>
            <g transform="translate(10 20)" clip-path="url(#c1)" opacity="0.5">
              <rect width="50" height="50" fill="#000000" stroke="#ffffff"/>
            </g></svg>"##);
        // push(c1, opacity), push(c2, 1.0), fill, stroke, pop, pop
        let pushes: Vec<&Op> = p.ops.iter().filter(|o| matches!(o, Op::Push { .. })).collect();
        assert_eq!(pushes.len(), 2);
        let Op::Push { opacity: o1, clip: c1 } = pushes[0] else { panic!() };
        let Op::Push { opacity: o2, clip: c2 } = pushes[1] else { panic!() };
        assert_close(*o1, 0.5);
        assert_close(*o2, 1.0);
        assert_mat(c1[0].mat, [1.0, 0.0, 0.0, 1.0, 110.0, 120.0]);
        assert_mat(c2[0].mat, [1.0, 0.0, 0.0, 1.0, 10.0, 20.0]);
        assert_eq!(p.ops.iter().filter(|o| matches!(o, Op::Pop)).count(), 2);
        assert_eq!(p.ops_count as usize, p.ops.len());
    }

    #[test]
    fn text_is_flattened_into_fills() {
        let mut options = usvg::Options::default();
        options.fontdb = crate::FONT_DB.clone();
        options.font_family = "Arial".to_string();
        if options.fontdb.is_empty() {
            return; // no fonts on this machine, nothing to flatten
        }
        let p = build_with(
            r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
                <text x="5" y="40" font-size="40" font-family="Arial" fill="#abcdef">Ag</text></svg>"##,
            &options,
        )
        .unwrap_or_else(|r| panic!("unsupported, reason {r}"));
        assert!(!p.ops.is_empty());
        let mut curves = false;
        for op in &p.ops {
            let Op::Fill { paint, path, .. } = op else { panic!("{op:?}") };
            let P::Solid(c) = paint else { panic!() };
            assert_close(c[0], 0xab as f32 / 255.0);
            curves |= path.verbs.iter().any(|v| *v == 2 || *v == 3);
        }
        assert!(curves, "glyph outlines should contain curves");
    }

    #[test]
    fn unsupported_filter() {
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <filter id="f"><feGaussianBlur stdDeviation="2"/></filter>
            <rect width="10" height="10" filter="url(#f)"/></svg>"##);
        assert_eq!(r.err(), Some(REASON_FILTER));
    }

    #[test]
    fn unsupported_mask() {
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <mask id="m"><rect width="5" height="5" fill="white"/></mask>
            <rect width="10" height="10" mask="url(#m)"/></svg>"##);
        assert_eq!(r.err(), Some(REASON_MASK));
    }

    #[test]
    fn unsupported_blend_mode() {
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <rect width="10" height="10" style="mix-blend-mode:multiply"/></svg>"##);
        assert_eq!(r.err(), Some(REASON_BLEND_MODE));
    }

    #[test]
    fn unsupported_pattern() {
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <pattern id="p" width="4" height="4" patternUnits="userSpaceOnUse"><rect width="2" height="2"/></pattern>
            <rect width="10" height="10" fill="url(#p)"/></svg>"##);
        assert_eq!(r.err(), Some(REASON_PATTERN));
    }

    #[test]
    fn unsupported_image() {
        // 1x1 transparent PNG.
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="100" height="100">
            <image width="10" height="10" xlink:href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="/></svg>"##);
        assert_eq!(r.err(), Some(REASON_IMAGE));
    }

    #[test]
    fn unsupported_clip_on_clip_child() {
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <defs>
              <clipPath id="inner"><rect width="3" height="3"/></clipPath>
              <clipPath id="outer"><rect clip-path="url(#inner)" width="9" height="9"/></clipPath>
            </defs>
            <rect width="50" height="50" clip-path="url(#outer)"/></svg>"##);
        assert_eq!(r.err(), Some(REASON_CLIP_ON_CLIP_CHILD));
    }

    #[test]
    fn unsupported_radial_focal_radius() {
        let r = build(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <defs><radialGradient id="g" cx="50" cy="50" r="40" fr="10" gradientUnits="userSpaceOnUse">
              <stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></radialGradient></defs>
            <rect width="100" height="100" fill="url(#g)"/></svg>"##);
        assert_eq!(r.err(), Some(REASON_RADIAL_FOCAL_RADIUS));
    }

    #[test]
    fn too_complex_caps() {
        let mut svg = String::from(r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">"##);
        for i in 0..(MAX_OPS + 1) {
            svg.push_str(&format!(r##"<rect x="{}" width="1" height="1"/>"##, i % 90));
        }
        svg.push_str("</svg>");
        assert_eq!(build(&svg).err(), Some(REASON_TOO_COMPLEX));
    }
}
