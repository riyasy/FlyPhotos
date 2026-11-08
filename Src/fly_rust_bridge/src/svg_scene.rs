// Serialises a parsed usvg tree into a compact little-endian byte stream that C# (SvgScene*.cs)
// replays into Win2D/Direct2D, so SVGs are drawn on the GPU at any zoom.
// usvg has already resolved CSS, <use>, markers, units and text (text.flattened() is plain paths),
// so the stream only carries groups, paths, raster images, clip paths, masks and filters.
// Anything the C# side can't draw yet returns Err(reason) and the caller falls back to resvg.

use resvg::usvg::{self, filter, Node};

// ── Wire tags ────────────────────────────────────────────────────────────────
// Every value and the field order below must match the enums in SvgScene.Model.cs.

/// Node tags; END closes a child list.
mod tag {
    pub const END: u8 = 0;
    pub const GROUP: u8 = 1;
    pub const PATH: u8 = 2;
    pub const IMAGE: u8 = 3;
}

/// Bits of a group's flags byte.
mod group_flag {
    /// A clip path block follows.
    pub const CLIP: u8 = 1;
    /// A mask block follows.
    pub const MASK: u8 = 2;
    /// isolation:isolate — a backdrop boundary for mix-blend-mode even with nothing else set.
    pub const ISOLATE: u8 = 4;
    /// The clip path is one axis-aligned rectangle: its x, y, width, height (group user space) follow
    /// instead of a clip path block.
    pub const CLIP_RECT: u8 = 8;
}

/// Bits of a path's flags byte.
mod path_flag {
    /// A fill paint follows the segments.
    pub const FILL: u8 = 1;
    /// A stroke paint and stroke parameters follow (after the fill, if any).
    pub const STROKE: u8 = 2;
    /// `paint-order: stroke`: draw the stroke before the fill.
    pub const STROKE_FIRST: u8 = 4;
    /// Anti-alias the shape (off for `shape-rendering: crispEdges / optimizeSpeed`).
    pub const ANTIALIAS: u8 = 8;
    /// The fill rule is evenodd (otherwise nonzero).
    pub const EVEN_ODD: u8 = 16;
}

/// Path segment tags; each is followed by its points as f32 x, y pairs (none for CLOSE).
mod verb {
    pub const MOVE: u8 = 0;
    pub const LINE: u8 = 1;
    pub const QUAD: u8 = 2;
    pub const CUBIC: u8 = 3;
    pub const CLOSE: u8 = 4;
}

/// Paint tags: solid colour, linear gradient, radial gradient, pattern.
mod paint_tag {
    pub const SOLID: u8 = 0;
    pub const LINEAR: u8 = 1;
    pub const RADIAL: u8 = 2;
    pub const PATTERN: u8 = 3;
}

/// stroke-linecap values.
mod cap { pub const BUTT: u8 = 0; pub const ROUND: u8 = 1; pub const SQUARE: u8 = 2; }
/// stroke-linejoin values (miter-clip goes over as MITER).
mod join { pub const MITER: u8 = 0; pub const ROUND: u8 = 1; pub const BEVEL: u8 = 2; }
/// Gradient spreadMethod values.
mod spread { pub const PAD: u8 = 0; pub const REFLECT: u8 = 1; pub const REPEAT: u8 = 2; }

/// Filter primitive tags.
mod fe {
    pub const BLEND: u8 = 1;
    pub const COLOR_MATRIX: u8 = 2;
    pub const COMPONENT_TRANSFER: u8 = 3;
    pub const COMPOSITE: u8 = 4;
    pub const DROP_SHADOW: u8 = 5;
    pub const FLOOD: u8 = 6;
    pub const GAUSSIAN_BLUR: u8 = 7;
    pub const MERGE: u8 = 8;
    pub const MORPHOLOGY: u8 = 9;
    pub const OFFSET: u8 = 10;
    pub const TURBULENCE: u8 = 11;
    pub const DIFFUSE_LIGHTING: u8 = 12;
    pub const SPECULAR_LIGHTING: u8 = 13;
}

/// Filter primitive input tags; REFERENCE is followed by the result name.
mod input_tag { pub const SOURCE_GRAPHIC: u8 = 0; pub const SOURCE_ALPHA: u8 = 1; pub const REFERENCE: u8 = 2; }

/// feComponentTransfer function types.
mod transfer_tag {
    pub const IDENTITY: u8 = 0;
    pub const TABLE: u8 = 1;
    pub const DISCRETE: u8 = 2;
    pub const LINEAR: u8 = 3;
    pub const GAMMA: u8 = 4;
}

/// feComposite operators; ARITHMETIC is followed by k1..k4.
mod composite_op {
    pub const OVER: u8 = 0;
    pub const IN: u8 = 1;
    pub const OUT: u8 = 2;
    pub const ATOP: u8 = 3;
    pub const XOR: u8 = 4;
    pub const LIGHTER: u8 = 5;
    pub const ARITHMETIC: u8 = 6;
}

/// Light source tags for feDiffuseLighting / feSpecularLighting.
mod light_tag { pub const DISTANT: u8 = 0; pub const POINT: u8 = 1; pub const SPOT: u8 = 2; }

// Blend modes go over the wire as usvg::BlendMode's declaration order (Normal = 0 … PlusLighter = 16).

// ── Writer ───────────────────────────────────────────────────────────────────

/// Appends little-endian values to the stream buffer.
struct Writer {
    /// The stream being built; handed to C# as-is.
    buf: Vec<u8>,
}

/// Ok, or the reason the tree can't go down the GPU path (the caller then falls back to resvg).
type WriteResult = Result<(), String>;

impl Writer {
    /// One byte.
    fn u8(&mut self, v: u8) { self.buf.push(v); }
    /// Little-endian u32 (counts, lengths, seeds).
    fn u32(&mut self, v: u32) { self.buf.extend_from_slice(&v.to_le_bytes()); }
    /// Little-endian f32.
    fn f32(&mut self, v: f32) { self.buf.extend_from_slice(&v.to_le_bytes()); }
    /// Each value as an f32, in order.
    fn floats(&mut self, vs: &[f32]) { for v in vs { self.f32(*v); } }
    /// A u32 byte length followed by the UTF-8 bytes.
    fn str(&mut self, s: &str) { self.u32(s.len() as u32); self.buf.extend_from_slice(s.as_bytes()); }
    /// A transform as sx, ky, kx, sy, tx, ty (C# Matrix3x2 M11, M12, M21, M22, M31, M32).
    fn ts(&mut self, t: usvg::Transform) { self.floats(&[t.sx, t.ky, t.kx, t.sy, t.tx, t.ty]); }
    /// A rectangle as x, y, width, height.
    fn rect(&mut self, r: usvg::NonZeroRect) { self.floats(&[r.x(), r.y(), r.width(), r.height()]); }
    /// A colour as three bytes, red, green, blue.
    fn rgb(&mut self, c: usvg::Color) { self.u8(c.red); self.u8(c.green); self.u8(c.blue); }
    /// RGB followed by an f32 opacity — every colour that has an opacity is written this way.
    fn rgba(&mut self, c: usvg::Color, opacity: f32) { self.rgb(c); self.f32(opacity); }
}

/// The stream starts with svg_build_scene's OK status byte (0), so the FFI hands it over without a copy.
pub fn write_tree(tree: &usvg::Tree) -> Result<Vec<u8>, String> {
    let mut w = Writer { buf: Vec::with_capacity(64 * 1024) };
    w.u8(0);
    w.f32(tree.size().width());
    w.f32(tree.size().height());
    children(&mut w, tree.root(), false)?;
    Ok(w.buf)
}

/// Writes a group's children followed by END. In clip mode (a clipPath's content) paths are
/// written as opaque black fills and only transforms and nested clip paths are kept.
fn children(w: &mut Writer, g: &usvg::Group, clip_mode: bool) -> WriteResult {
    for n in g.children() {
        match n {
            Node::Group(c) => group(w, c, clip_mode)?,
            Node::Path(p) => path(w, p, clip_mode, 1.0)?,
            Node::Text(t) => group(w, t.flattened(), clip_mode)?,
            Node::Image(i) if !clip_mode => image(w, i)?,
            Node::Image(_) => {}
        }
    }
    w.u8(tag::END);
    Ok(())
}

/// GROUP: transform, opacity, blend mode, flags, filter count, then the optional clip and mask blocks,
/// the filters and the children. In clip mode only the transform and clip path are kept (opacity,
/// blend, mask and filters don't apply inside a clipPath).
fn group(w: &mut Writer, g: &usvg::Group, clip_mode: bool) -> WriteResult {
    w.u8(tag::GROUP);
    w.ts(g.transform());
    let (opacity, blend, isolate, mask, filters) = if clip_mode {
        (1.0, 0, false, None, &[][..])
    } else {
        (g.opacity().get(), g.blend_mode() as u8, g.isolate(), g.mask(), g.filters())
    };
    // A rectangular clip is sent as a rectangle (a cheap clip on the GPU side), or dropped when it cuts
    // nothing off: PDF exports clip every element to the page. Filtered groups keep it, because a
    // filter can spread past the content's bounds.
    let rect = g.clip_path().and_then(clip_rect);
    let b = g.stroke_bounding_box();
    let cuts_nothing = rect.is_some_and(|r| g.filters().is_empty()
        && r.left() <= b.left() && r.top() <= b.top() && r.right() >= b.right() && r.bottom() >= b.bottom());
    let clip_rect = rect.filter(|_| !cuts_nothing);
    let clip_path = g.clip_path().filter(|_| rect.is_none());
    // An opacity group around one path with only a fill or only a stroke looks the same with the opacity
    // folded into that paint (nothing overlaps inside it), and then needs no layer. Element opacity on a
    // path is such a group; maps have thousands, and each layer costs a target-sized surface in Direct2D.
    if let [Node::Path(p)] = g.children() {
        if opacity < 1.0 && blend == 0 && !isolate && mask.is_none() && filters.is_empty()
            && clip_path.is_none() && clip_rect.is_none() && p.fill().is_some() != p.stroke().is_some()
        {
            w.f32(1.0); w.u8(0); w.u8(0); w.u8(0); // opacity, blend, flags, filter count
            path(w, p, clip_mode, opacity)?;
            w.u8(tag::END);
            return Ok(());
        }
    }
    w.f32(opacity);
    w.u8(blend);
    let mut flags = 0;
    if clip_path.is_some() { flags |= group_flag::CLIP; }
    if mask.is_some() { flags |= group_flag::MASK; }
    if isolate { flags |= group_flag::ISOLATE; }
    if clip_rect.is_some() { flags |= group_flag::CLIP_RECT; }
    w.u8(flags);
    w.u8(filters.len() as u8);
    if let Some(c) = clip_path { clip(w, c)?; }
    if let Some(r) = clip_rect { w.floats(&[r.x(), r.y(), r.width(), r.height()]); }
    if let Some(m) = mask { write_mask(w, m)?; }
    for f in filters { write_filter(w, f)?; }
    children(w, g, clip_mode)
}

/// The clip path's area in the clipped group's user space when it is exactly one axis-aligned
/// rectangle (through transforms without rotation or skew), with no nested clip path.
fn clip_rect(c: &usvg::ClipPath) -> Option<usvg::Rect> {
    if c.clip_path().is_some() { return None; }
    let mut ts = c.transform();
    let mut node = c.root();
    loop {
        match node.children() {
            [Node::Group(inner)] if inner.clip_path().is_none() => {
                ts = ts.pre_concat(inner.transform());
                node = inner;
            }
            [Node::Path(p)] if p.is_visible() && p.fill().is_some() && !ts.has_skew() => {
                return rect_of(p.data()).and_then(|r| r.transform(ts));
            }
            _ => return None,
        }
    }
}

/// The path's bounds when the path is exactly an axis-aligned rectangle: a move and straight lines
/// through its four corners, each line along one axis, optionally back to the start and closed.
fn rect_of(d: &usvg::tiny_skia_path::Path) -> Option<usvg::Rect> {
    use usvg::tiny_skia_path::PathSegment as S;
    let mut pts = Vec::with_capacity(5);
    let mut closed = false;
    for (i, s) in d.segments().enumerate() {
        match s {
            S::MoveTo(p) if i == 0 => pts.push(p),
            S::LineTo(p) if !closed => pts.push(p),
            S::Close if !closed => closed = true,
            _ => return None,
        }
    }
    if pts.len() == 5 && pts[4] == pts[0] { pts.pop(); }
    if pts.len() != 4 { return None; }
    let b = d.bounds();
    for i in 0..4 {
        let (p, q) = (pts[i], pts[(i + 1) % 4]);
        if pts[i + 1..].contains(&p) { return None; } // a corner visited twice is not a rectangle
        let on_corner = (p.x == b.left() || p.x == b.right()) && (p.y == b.top() || p.y == b.bottom());
        let one_axis = (p.x == q.x) != (p.y == q.y);
        if !on_corner || !one_axis { return None; }
    }
    Some(b)
}

/// A clip path block: its transform, a has-nested byte (+ the nested clip path, which clips this one),
/// then its children written in clip mode.
fn clip(w: &mut Writer, c: &usvg::ClipPath) -> WriteResult {
    w.ts(c.transform());
    w.u8(c.clip_path().is_some() as u8);
    if let Some(n) = c.clip_path() { clip(w, n)?; }
    children(w, c.root(), true)
}

/// A mask block: its region (user space), an alpha-mask byte (0 = luminance), a has-nested byte
/// (+ the nested mask, applied to the content as resvg does), then its children.
fn write_mask(w: &mut Writer, m: &usvg::Mask) -> WriteResult {
    w.rect(m.rect());
    w.u8(matches!(m.kind(), usvg::MaskType::Alpha) as u8);
    w.u8(m.mask().is_some() as u8);
    if let Some(n) = m.mask() { write_mask(w, n)?; }
    children(w, m.root(), false)
}

/// PATH: flags, segments, then the fill (paint + opacity) and the stroke (paint + opacity, width, cap,
/// join, miter limit, dash array, dash offset) as the flags say. Invisible or unpainted paths are skipped.
/// In clip mode the fill is opaque black and the stroke is dropped. `opacity` multiplies both paints'
/// opacities (a folded group opacity; 1 otherwise).
fn path(w: &mut Writer, p: &usvg::Path, clip_mode: bool, opacity: f32) -> WriteResult {
    if !p.is_visible() { return Ok(()); }
    let fill = p.fill();
    let stroke = if clip_mode { None } else { p.stroke() };
    if fill.is_none() && stroke.is_none() { return Ok(()); }
    w.u8(tag::PATH);
    let mut flags = 0;
    if fill.is_some() { flags |= path_flag::FILL; }
    if stroke.is_some() { flags |= path_flag::STROKE; }
    if p.paint_order() == usvg::PaintOrder::StrokeAndFill { flags |= path_flag::STROKE_FIRST; }
    if p.rendering_mode().use_shape_antialiasing() { flags |= path_flag::ANTIALIAS; }
    if fill.is_some_and(|f| f.rule() == usvg::FillRule::EvenOdd) { flags |= path_flag::EVEN_ODD; }
    w.u8(flags);
    path_data(w, p.data());
    if let Some(f) = fill {
        if clip_mode {
            w.u8(paint_tag::SOLID); w.rgba(usvg::Color::black(), 1.0);
        } else {
            paint(w, f.paint())?;
            w.f32(f.opacity().get() * opacity);
        }
    }
    if let Some(s) = stroke {
        paint(w, s.paint())?;
        w.f32(s.opacity().get() * opacity);
        w.f32(s.width().get());
        w.u8(match s.linecap() { usvg::LineCap::Butt => cap::BUTT, usvg::LineCap::Round => cap::ROUND, usvg::LineCap::Square => cap::SQUARE });
        w.u8(match s.linejoin() {
            usvg::LineJoin::Miter | usvg::LineJoin::MiterClip => join::MITER,
            usvg::LineJoin::Round => join::ROUND,
            usvg::LineJoin::Bevel => join::BEVEL,
        });
        w.f32(s.miterlimit().get());
        let dashes = s.dasharray().unwrap_or(&[]);
        w.u32(dashes.len() as u32);
        w.floats(dashes);
        w.f32(s.dashoffset());
    }
    Ok(())
}

/// The segment count, then each segment as a verb tag and its points.
fn path_data(w: &mut Writer, d: &usvg::tiny_skia_path::Path) {
    use usvg::tiny_skia_path::PathSegment as S;
    w.u32(d.segments().count() as u32);
    for s in d.segments() {
        match s {
            S::MoveTo(p) => { w.u8(verb::MOVE); w.floats(&[p.x, p.y]); }
            S::LineTo(p) => { w.u8(verb::LINE); w.floats(&[p.x, p.y]); }
            S::QuadTo(a, b) => { w.u8(verb::QUAD); w.floats(&[a.x, a.y, b.x, b.y]); }
            S::CubicTo(a, b, c) => { w.u8(verb::CUBIC); w.floats(&[a.x, a.y, b.x, b.y, c.x, c.y]); }
            S::Close => w.u8(verb::CLOSE),
        }
    }
}

/// A paint, without its opacity (the caller writes that after it).
fn paint(w: &mut Writer, p: &usvg::Paint) -> WriteResult {
    match p {
        usvg::Paint::Color(c) => { w.u8(paint_tag::SOLID); w.rgb(*c); }
        usvg::Paint::LinearGradient(g) => {
            w.u8(paint_tag::LINEAR);
            w.floats(&[g.x1(), g.y1(), g.x2(), g.y2()]);
            gradient_base(w, g);
        }
        usvg::Paint::RadialGradient(g) => {
            w.u8(paint_tag::RADIAL);
            w.floats(&[g.cx(), g.cy(), g.r().get(), g.fx(), g.fy(), g.fr().get()]);
            gradient_base(w, g);
        }
        usvg::Paint::Pattern(p) => {
            w.u8(paint_tag::PATTERN);
            w.rect(p.rect());
            w.ts(p.transform());
            children(w, p.root(), false)?;
        }
    }
    Ok(())
}

/// What both gradient kinds share: gradientTransform, spread method, and the stops (offset + colour +
/// stop opacity). usvg has already resolved objectBoundingBox units into user space.
fn gradient_base(w: &mut Writer, g: &usvg::BaseGradient) {
    w.ts(g.transform());
    w.u8(match g.spread_method() {
        usvg::SpreadMethod::Pad => spread::PAD,
        usvg::SpreadMethod::Reflect => spread::REFLECT,
        usvg::SpreadMethod::Repeat => spread::REPEAT,
    });
    w.u32(g.stops().len() as u32);
    for s in g.stops() {
        w.f32(s.offset().get());
        w.rgba(s.color(), s.opacity().get());
    }
}

/// IMAGE: a nearest-neighbour byte (image-rendering pixelated / crisp-edges / optimizeSpeed) and the
/// encoded raster bytes, which C# decodes with WIC. A nested SVG image is written as a plain group instead.
/// usvg puts the image's placement into the parent group's transform, so it draws at (0, 0, width, height).
fn image(w: &mut Writer, i: &usvg::Image) -> WriteResult {
    if !i.is_visible() { return Ok(()); }
    let data = match i.kind() {
        usvg::ImageKind::JPEG(d) | usvg::ImageKind::PNG(d) | usvg::ImageKind::GIF(d) | usvg::ImageKind::WEBP(d) => d,
        // A nested SVG draws its root's children in a plain group: resvg's render_vector uses
        // render_nodes(tree.root()), which ignores the root group's own transform/clip/opacity, so
        // this must not go through group().
        usvg::ImageKind::SVG(t) => {
            w.u8(tag::GROUP);
            w.ts(usvg::Transform::identity());
            w.f32(1.0); w.u8(0); w.u8(0); w.u8(0); // opacity, blend, flags, filter count
            return children(w, t.root(), false);
        }
    };
    w.u8(tag::IMAGE);
    w.u8(matches!(i.rendering_mode(),
        usvg::ImageRendering::OptimizeSpeed | usvg::ImageRendering::CrispEdges | usvg::ImageRendering::Pixelated) as u8);
    w.u32(data.len() as u32);
    w.buf.extend_from_slice(data);
    Ok(())
}

// ── Filters ──────────────────────────────────────────────────────────────────

/// A filter block: its region (user space), the primitive count, then each primitive as subregion,
/// linearRGB byte (color-interpolation-filters), result name, and the primitive itself.
fn write_filter(w: &mut Writer, f: &filter::Filter) -> WriteResult {
    w.rect(f.rect());
    w.u32(f.primitives().len() as u32);
    for p in f.primitives() {
        w.rect(p.rect());
        w.u8(matches!(p.color_interpolation(), filter::ColorInterpolation::LinearRGB) as u8);
        w.str(p.result());
        primitive(w, p.kind())?;
    }
    Ok(())
}

/// A primitive input: SourceGraphic, SourceAlpha, or a reference to an earlier result by name.
fn input(w: &mut Writer, i: &filter::Input) {
    match i {
        filter::Input::SourceGraphic => w.u8(input_tag::SOURCE_GRAPHIC),
        filter::Input::SourceAlpha => w.u8(input_tag::SOURCE_ALPHA),
        filter::Input::Reference(s) => { w.u8(input_tag::REFERENCE); w.str(s); }
    }
}

/// A primitive's tag and parameters (lengths in user units; C# scales them into filter pixels).
/// Returns Err for the primitives the C# side can't draw yet, naming the element.
fn primitive(w: &mut Writer, k: &filter::Kind) -> WriteResult {
    use filter::Kind as K;
    match k {
        K::Blend(b) => { w.u8(fe::BLEND); input(w, b.input1()); input(w, b.input2()); w.u8(b.mode() as u8); }
        K::ColorMatrix(c) => { w.u8(fe::COLOR_MATRIX); input(w, c.input()); w.floats(&color_matrix(c.kind())); }
        K::ComponentTransfer(c) => {
            w.u8(fe::COMPONENT_TRANSFER); input(w, c.input());
            for f in [c.func_r(), c.func_g(), c.func_b(), c.func_a()] { transfer(w, f); }
        }
        K::Composite(c) => {
            w.u8(fe::COMPOSITE); input(w, c.input1()); input(w, c.input2());
            use filter::CompositeOperator as O;
            match c.operator() {
                O::Over => w.u8(composite_op::OVER),
                O::In => w.u8(composite_op::IN),
                O::Out => w.u8(composite_op::OUT),
                O::Atop => w.u8(composite_op::ATOP),
                O::Xor => w.u8(composite_op::XOR),
                O::Lighter => w.u8(composite_op::LIGHTER),
                O::Arithmetic { k1, k2, k3, k4 } => { w.u8(composite_op::ARITHMETIC); w.floats(&[k1, k2, k3, k4]); }
            }
        }
        K::DropShadow(d) => {
            w.u8(fe::DROP_SHADOW); input(w, d.input());
            w.floats(&[d.dx(), d.dy(), d.std_dev_x().get(), d.std_dev_y().get()]);
            w.rgba(d.color(), d.opacity().get());
        }
        K::Flood(f) => { w.u8(fe::FLOOD); w.rgba(f.color(), f.opacity().get()); }
        K::GaussianBlur(b) => { w.u8(fe::GAUSSIAN_BLUR); input(w, b.input()); w.floats(&[b.std_dev_x().get(), b.std_dev_y().get()]); }
        K::Merge(m) => { w.u8(fe::MERGE); w.u32(m.inputs().len() as u32); for i in m.inputs() { input(w, i); } }
        K::Morphology(m) => {
            w.u8(fe::MORPHOLOGY); input(w, m.input());
            w.u8(matches!(m.operator(), filter::MorphologyOperator::Dilate) as u8);
            w.floats(&[m.radius_x().get(), m.radius_y().get()]);
        }
        K::Offset(o) => { w.u8(fe::OFFSET); input(w, o.input()); w.floats(&[o.dx(), o.dy()]); }
        K::Turbulence(t) => {
            w.u8(fe::TURBULENCE);
            w.floats(&[t.base_frequency_x().get(), t.base_frequency_y().get()]);
            w.u32(t.num_octaves()); w.u32(t.seed() as u32);
            w.u8(t.stitch_tiles() as u8);
            w.u8(matches!(t.kind(), filter::TurbulenceKind::Turbulence) as u8);
        }
        K::DiffuseLighting(l) => {
            w.u8(fe::DIFFUSE_LIGHTING); input(w, l.input());
            w.floats(&[l.surface_scale(), l.diffuse_constant()]);
            w.rgb(l.lighting_color());
            light(w, l.light_source());
        }
        K::SpecularLighting(l) => {
            w.u8(fe::SPECULAR_LIGHTING); input(w, l.input());
            w.floats(&[l.surface_scale(), l.specular_constant(), l.specular_exponent()]);
            w.rgb(l.lighting_color());
            light(w, l.light_source());
        }
        other => return Err(format!("filter primitive {}", kind_name(other))),
    }
    Ok(())
}

/// The SVG element name of an unsupported primitive, for the fallback reason in the log.
fn kind_name(k: &filter::Kind) -> &'static str {
    use filter::Kind as K;
    match k {
        K::ConvolveMatrix(_) => "feConvolveMatrix", K::DisplacementMap(_) => "feDisplacementMap",
        K::Image(_) => "feImage", K::Tile(_) => "feTile", _ => "unknown",
    }
}

/// Distant: azimuth, elevation (degrees). Point: x, y, z.
/// Spot: x, y, z, pointsAt x/y/z, specularExponent, has-cone byte, cone angle (degrees).
fn light(w: &mut Writer, l: filter::LightSource) {
    match l {
        filter::LightSource::DistantLight(d) => { w.u8(light_tag::DISTANT); w.floats(&[d.azimuth, d.elevation]); }
        filter::LightSource::PointLight(p) => { w.u8(light_tag::POINT); w.floats(&[p.x, p.y, p.z]); }
        filter::LightSource::SpotLight(s) => {
            w.u8(light_tag::SPOT);
            w.floats(&[s.x, s.y, s.z, s.points_at_x, s.points_at_y, s.points_at_z, s.specular_exponent.get()]);
            w.u8(s.limiting_cone_angle.is_some() as u8);
            w.f32(s.limiting_cone_angle.unwrap_or(0.0));
        }
    }
}

/// One feComponentTransfer channel function: its tag and values (table/discrete: count + values;
/// linear: slope, intercept; gamma: amplitude, exponent, offset; identity: nothing).
fn transfer(w: &mut Writer, f: &filter::TransferFunction) {
    use filter::TransferFunction as T;
    match f {
        T::Identity => w.u8(transfer_tag::IDENTITY),
        T::Table(v) | T::Discrete(v) => {
            w.u8(if matches!(f, T::Table(_)) { transfer_tag::TABLE } else { transfer_tag::DISCRETE });
            w.u32(v.len() as u32);
            w.floats(v);
        }
        T::Linear { slope, intercept } => { w.u8(transfer_tag::LINEAR); w.floats(&[*slope, *intercept]); }
        T::Gamma { amplitude, exponent, offset } => { w.u8(transfer_tag::GAMMA); w.floats(&[*amplitude, *exponent, *offset]); }
    }
}

/// Every feColorMatrix type as the SVG 4×5 row-major matrix (SVG 1.1 §15.10).
fn color_matrix(k: &filter::ColorMatrixKind) -> [f32; 20] {
    use filter::ColorMatrixKind as C;
    match k {
        C::Matrix(m) => { let mut a = [0.0; 20]; a.copy_from_slice(&m[..20]); a }
        C::Saturate(s) => {
            let s = s.get();
            [0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s, 0.0, 0.0,
             0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s, 0.0, 0.0,
             0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s, 0.0, 0.0,
             0.0, 0.0, 0.0, 1.0, 0.0]
        }
        C::HueRotate(deg) => {
            let (s, c) = deg.to_radians().sin_cos();
            [0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928, 0.0, 0.0,
             0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283, 0.0, 0.0,
             0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072, 0.0, 0.0,
             0.0, 0.0, 0.0, 1.0, 0.0]
        }
        C::LuminanceToAlpha => {
            [0.0, 0.0, 0.0, 0.0, 0.0,
             0.0, 0.0, 0.0, 0.0, 0.0,
             0.0, 0.0, 0.0, 0.0, 0.0,
             0.2125, 0.7154, 0.0721, 0.0, 0.0]
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Parses `svg` with default options and writes its stream.
    fn build(svg: &str) -> Result<Vec<u8>, String> {
        let tree = usvg::Tree::from_str(svg, &usvg::Options::default()).unwrap();
        write_tree(&tree)
    }

    /// The stream starts with the OK status and the canvas size, and its root child list ends with END.
    #[test]
    fn simple_rect_starts_with_size_and_ends_with_end() {
        let b = build(r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="20"><rect width="5" height="5"/></svg>"#).unwrap();
        assert_eq!(b[0], 0); // OK status
        assert_eq!(f32::from_le_bytes(b[1..5].try_into().unwrap()), 10.0);
        assert_eq!(f32::from_le_bytes(b[5..9].try_into().unwrap()), 20.0);
        assert_eq!(*b.last().unwrap(), tag::END);
    }

    /// An unsupported filter primitive makes write_tree fail with a reason that names it.
    #[test]
    fn unsupported_primitive_is_reported() {
        let e = build(r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><filter id="f"><feTile/></filter><rect width="5" height="5" filter="url(#f)"/></svg>"#).unwrap_err();
        assert!(e.contains("feTile"));
    }

    /// A rectangular clip around the whole shape is dropped, one that cuts the shape is sent as a
    /// rectangle, and any other clip as a clip path.
    #[test]
    fn rectangular_clips_are_dropped_or_sent_as_rectangles() {
        let clip = |clip_shape: &str| build(&format!(
            r#"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><clipPath id="c">{clip_shape}</clipPath><rect x="10" y="10" width="20" height="20" clip-path="url(#c)"/></svg>"#)).unwrap();
        let no_clip = build(r#"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect x="10" y="10" width="20" height="20"/></svg>"#).unwrap();
        let page = clip(r#"<path d="M 0,0 H 100 V 100 H 0 Z" transform="scale(1.5)"/>"#);
        // Only the wrapping group remains: tag, transform, opacity, blend, flags, filter count, END.
        assert_eq!(page.len(), no_clip.len() + 1 + 24 + 4 + 1 + 1 + 1 + 1);
        assert_eq!(page[9], tag::GROUP);
        let flags = |b: &[u8]| b[9 + 1 + 24 + 4 + 1];
        assert_eq!(flags(&page), 0, "no clip expected");
        let cut = clip(r#"<path d="M 0,0 H 20 V 20 H 0 Z" transform="translate(5,0)"/>"#);
        assert_eq!(flags(&cut), group_flag::CLIP_RECT);
        let rect: Vec<f32> = cut[9 + 1 + 24 + 4 + 1 + 2..][..16].chunks(4).map(|c| f32::from_le_bytes(c.try_into().unwrap())).collect();
        assert_eq!(rect, [5.0, 0.0, 20.0, 20.0]);
        for path_clip in [r#"<circle r="100"/>"#, r#"<path d="M 0,0 H 100 V 100 H 100 Z"/>"#] {
            assert_eq!(flags(&clip(path_clip)), group_flag::CLIP, "{path_clip}");
        }
    }

    #[test]
    fn rect_of_accepts_only_rectangles() {
        use usvg::tiny_skia_path::PathBuilder;
        let rect = |pts: &[(f32, f32)], close: bool| {
            let mut pb = PathBuilder::new();
            pb.move_to(pts[0].0, pts[0].1);
            for p in &pts[1..] { pb.line_to(p.0, p.1); }
            if close { pb.close(); }
            rect_of(&pb.finish().unwrap())
        };
        assert!(rect(&[(0., 0.), (10., 0.), (10., 5.), (0., 5.)], true).is_some());
        assert!(rect(&[(0., 0.), (0., 5.), (10., 5.), (10., 0.), (0., 0.)], false).is_some());
        assert!(rect(&[(0., 0.), (10., 0.), (10., 10.), (10., 0.)], true).is_none()); // revisits a corner
        assert!(rect(&[(0., 0.), (10., 0.), (10., 5.), (2., 5.)], true).is_none()); // not on the bounds' corners
        assert!(rect(&[(0., 0.), (10., 1.), (10., 5.), (0., 5.)], true).is_none()); // slanted edge
    }
}
