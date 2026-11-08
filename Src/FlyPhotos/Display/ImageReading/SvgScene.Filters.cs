#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace FlyPhotos.Display.ImageReading;

internal sealed partial class SvgScene
{
    private sealed partial class NodeDrawer
    {
        /// <summary>Largest blur deviation run at full resolution; bigger blurs run on a smaller copy.</summary>
        private const float MaxBlurDev = 64f;
        /// <summary>Filter results built per consumer before falling back to sharing them (see ApplyFilter).</summary>
        private const int MaxFreshBuilds = 256;

        /// <summary>Resolution index of a filter result: full, or reduced for big blurs.</summary>
        private const int FullRes = 0, ReducedRes = 1;

        /// <summary>A filter result, built on demand at <see cref="FullRes"/> or <see cref="ReducedRes"/>.</summary>
        private delegate (ICanvasImage Image, bool Linear) FilterResult(int res);

        /// <summary><paramref name="f"/> built at most once per resolution; later calls return the same node.</summary>
        private static FilterResult Memo(FilterResult f)
        {
            var cache = new (ICanvasImage, bool)?[2];
            return res => cache[res] ??= f(res);
        }

        /// <summary>A blur primitive's largest deviation in filter pixels (0 for every other kind).</summary>
        private static float BlurDeviation(Primitive p, float sx, float sy) => p switch
        {
            BlurPrimitive b => MathF.Max(b.StdDev.X * sx, b.StdDev.Y * sy),
            DropShadowPrimitive d => MathF.Max(d.StdDev.X * sx, d.StdDev.Y * sy),
            _ => 0f,
        };

        /// <summary>A primitive's subregion in a filter pixel space, clipped to the filter region (the region if empty).</summary>
        private static Rect SubRect(Primitive p, Matrix3x2 space, Rect region)
        {
            var sub = TransformRect(p.Subregion, space);
            sub.Intersect(region);
            return sub.IsEmpty ? region : sub;
        }

        /// <summary>
        /// Runs one &lt;filter&gt; on <paramref name="source"/>. Everything is in the pixel space
        /// <paramref name="filterSpace"/> maps user units to; (sx, sy) is its scale, used for lengths like blur radii.
        /// </summary>
        /// <remarks>
        /// Big blurs (deep zoom) run at a reduced resolution, as browsers do: their inputs are re-recorded small
        /// through <paramref name="recordSourceAt"/> rather than rasterised in full and shrunk, which would cost a
        /// huge texture. Results are built lazily per resolution, so the sharp parts (e.g. the shape a drop shadow
        /// sits under) stay at full resolution and only the blurred branch is small.
        /// <para>
        /// No node is shared between consumers: Direct2D renders an image that feeds several effects over its
        /// whole bounds, which at deep zoom is a huge filter region (seconds per frame). Each consumer gets a
        /// fresh source and fresh results, so only the area it needs is rendered.
        /// </para>
        /// </remarks>
        private ICanvasImage ApplyFilter(FilterDef f, ICanvasImage source, Func<float, ICanvasImage> recordSourceAt,
            Matrix3x2 filterSpace, float sx, float sy)
        {
            var maxDev = f.Primitives.Max(p => BlurDeviation(p, sx, sy));
            var reduction = maxDev > MaxBlurDev ? MaxBlurDev / maxDev : 1f;
            float[] resScale = [1f, reduction];
            Matrix3x2[] space = [filterSpace, filterSpace * Matrix3x2.CreateScale(reduction)];
            Rect[] region = [TransformRect(f.Region, space[FullRes]), TransformRect(f.Region, space[ReducedRes])];

            var sourceUsed = false;
            ICanvasImage Source(int res)
            {
                var img = res == ReducedRes ? recordSourceAt(reduction) : sourceUsed ? recordSourceAt(1f) : source;
                if (res == FullRes) sourceUsed = true;
                return Crop(img, region[res]);
            }
            ICanvasImage Upscale(ICanvasImage reduced) => Xform(reduced, Matrix3x2.CreateScale(1 / reduction));

            var results = new Dictionary<string, FilterResult>();
            var builds = 0;
            FilterResult sourceGraphic = res => (Source(res), false);
            FilterResult sourceAlpha = res => (AlphaOf(Source(res)), false);
            var last = sourceGraphic;

            FilterResult Resolve(Input input) => input.Kind switch
            {
                InputKind.SourceGraphic => sourceGraphic,
                InputKind.SourceAlpha => sourceAlpha,
                _ => results.TryGetValue(input.Name, out var v) ? v : sourceGraphic,
            };

            foreach (var p in f.Primitives)
            {
                var reducedOnly = BlurDeviation(p, sx, sy) > MaxBlurDev; // runs small: deviation × reduction ≤ MaxBlurDev
                // An input in this primitive's colour space.
                ICanvasImage Get(Input input, int res)
                {
                    var (img, isLinear) = Resolve(input)(res);
                    return ToSpace(img, isLinear, p.Linear);
                }

                ICanvasImage Compute(int res)
                {
                    var s = resScale[res];
                    float ax = sx * s, ay = sy * s; // user units → pixels at this resolution
                    switch (p)
                    {
                        case BlendPrimitive b:
                            return Blend(Get(b.In2, res), Get(b.In, res), b.Mode);
                        case ColorMatrixPrimitive c:
                            return Keep(new ColorMatrixEffect { Source = Get(c.In, res), ColorMatrix = ToMatrix5x4(c.Matrix), ClampOutput = true });
                        case ComponentTransferPrimitive c:
                            return ComponentTransfer(Get(c.In, res), c.Channels);
                        case CompositePrimitive c:
                            return Composite(Get(c.In, res), Get(c.In2, res), c.Op, c.K);
                        case DropShadowPrimitive d:
                        {
                            var shadowRes = reducedOnly ? ReducedRes : res;
                            float shx = sx * resScale[shadowRes], shy = sy * resScale[shadowRes];
                            ICanvasImage shadow = Keep(new ColorMatrixEffect
                            {
                                Source = Blur(AlphaOf(Get(d.In, shadowRes)), d.StdDev.X * shx, d.StdDev.Y * shy),
                                // Colour the blurred alpha: rgb = shadow colour, a = alpha × shadow opacity.
                                ColorMatrix = new Matrix5x4
                                {
                                    M44 = d.Color.A / 255f,
                                    M51 = d.Color.R / 255f, M52 = d.Color.G / 255f, M53 = d.Color.B / 255f,
                                },
                                ClampOutput = true,
                            });
                            shadow = Xform(shadow, Matrix3x2.CreateTranslation(d.Offset.X * shx, d.Offset.Y * shy));
                            if (shadowRes != res) shadow = Upscale(shadow);
                            return Over(shadow, Get(d.In, res))!;
                        }
                        case FloodPrimitive fl:
                            return Keep(new ColorSourceEffect { Color = fl.Color });
                        case BlurPrimitive b:
                            return Blur(Get(b.In, res), b.StdDev.X * ax, b.StdDev.Y * ay);
                        case MergePrimitive m:
                        {
                            var merge = Keep(new CompositeEffect { Mode = CanvasComposite.SourceOver });
                            foreach (var i in m.Inputs) merge.Sources.Add(Get(i, res));
                            return merge;
                        }
                        case MorphologyPrimitive m:
                            return Morphology(Get(m.In, res), m.Dilate, m.Radius.X * ax, m.Radius.Y * ay);
                        case OffsetPrimitive o:
                            return Xform(Get(o.In, res), Matrix3x2.CreateTranslation(o.Offset.X * ax, o.Offset.Y * ay));
                        case TurbulencePrimitive t:
                            return Turbulence(t, space[res], region[res], ax, ay);
                        case LightingPrimitive l:
                        {
                            // Direct2D lights only where its input has pixels; SVG lights the whole subregion, so
                            // pad the height map with transparent pixels out to it.
                            var padded = Crop(Over(Keep(new ColorSourceEffect { Color = Color.FromArgb(0, 0, 0, 0) }), Get(l.In, res))!,
                                SubRect(p, space[res], region[res]));
                            return Lighting(l, padded, space[res], ax, ay);
                        }
                        default: throw new InvalidDataException("Bad SVG filter primitive");
                    }
                }

                // Every primitive is clipped to its subregion and the filter region (resvg skips feOffset).
                ICanvasImage Clip(ICanvasImage img, int res) =>
                    Crop(img, p is OffsetPrimitive ? region[res] : SubRect(p, space[res], region[res]));

                var outLinear = p is not FloodPrimitive && p.Linear; // the flood colour is sRGB
                FilterResult build = p is BlurPrimitive && reducedOnly
                    ? res => (res == ReducedRes ? Clip(Compute(ReducedRes), ReducedRes) : Upscale(Clip(Compute(ReducedRes), ReducedRes)), outLinear)
                    : res => (Clip(Compute(res), res), outLinear);
                // A fresh graph per consumer (see remarks). Results used many times would grow the graph
                // exponentially, so past MaxFreshBuilds per filter they are shared instead.
                var shared = Memo(build);
                FilterResult result = res => ++builds > MaxFreshBuilds ? shared(res) : build(res);
                last = result;
                results[p.Result] = result;
            }
            var (final, finalLinear) = last(FullRes);
            return Crop(ToSpace(final, finalLinear, false), region[FullRes]);
        }

        /// <summary>
        /// <paramref name="img"/> converted between sRGB and linearRGB as needed (alpha untouched). Win2D for
        /// WinAppSDK has no sRGB↔linear effect; the exact curve as a 256-entry table does the same.
        /// </summary>
        private ICanvasImage ToSpace(ICanvasImage img, bool isLinear, bool wantLinear)
        {
            if (isLinear == wantLinear) return img;
            var lut = wantLinear ? ToLinearLut : ToSrgbLut;
            return Keep(new TableTransferEffect
            {
                Source = img, AlphaDisable = true, RedTable = lut, GreenTable = lut, BlueTable = lut, ClampOutput = true,
            });
        }

        /// <summary>sRGB → linear transfer curve, 256 entries over 0..1.</summary>
        private static readonly float[] ToLinearLut = Lut(v => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f));
        /// <summary>linear → sRGB transfer curve, 256 entries over 0..1.</summary>
        private static readonly float[] ToSrgbLut = Lut(v => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f);

        /// <summary><paramref name="f"/> sampled at 256 evenly spaced points over 0..1.</summary>
        private static float[] Lut(Func<float, float> f)
        {
            var t = new float[256];
            for (var i = 0; i < t.Length; i++) t[i] = f(i / 255f);
            return t;
        }

        /// <summary>An sRGB colour's channels converted to linear (for lighting-color in a linearRGB primitive).</summary>
        private static Color ToLinear(Color c)
        {
            static byte L(byte v) => (byte)Math.Round(ToLinearLut[v] * 255);
            return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
        }

        /// <summary><paramref name="img"/> cropped to <paramref name="r"/> (pixels outside become transparent).</summary>
        private ICanvasImage Crop(ICanvasImage img, Rect r) => Keep(new CropEffect { Source = img, SourceRectangle = r });

        /// <summary>
        /// feTurbulence. SVG evaluates the noise at user-space points; Direct2D at its own pixels, over the
        /// rectangle (Offset, Offset + Size). So the noise is generated in a space shifted by the filter space's
        /// translation (the user→pixel scale folded into the frequency), then moved back by that translation.
        /// </summary>
        private ICanvasImage Turbulence(TurbulencePrimitive t, Matrix3x2 space, Rect region, float ax, float ay)
        {
            var sub = SubRect(t, space, region);
            var noise = Keep(new TurbulenceEffect
            {
                Offset = new Vector2((float)sub.X - space.M31, (float)sub.Y - space.M32),
                Size = new Vector2((float)sub.Width, (float)sub.Height),
                Frequency = new Vector2(t.BaseFrequency.X / ax, t.BaseFrequency.Y / ay),
                Octaves = Math.Max(1, t.Octaves),
                Seed = t.Seed,
                Noise = t.FractalNoise ? TurbulenceEffectNoise.FractalSum : TurbulenceEffectNoise.Turbulence,
                Tileable = t.Stitch,
            });
            return Xform(noise, Matrix3x2.CreateTranslation(space.M31, space.M32));
        }

        /// <summary>
        /// feDiffuseLighting / feSpecularLighting: the input's alpha is the height map. Light positions are
        /// mapped into the filter's pixel space; z is scaled like resvg (by the RMS of the axis scales).
        /// Six Win2D effect classes with no shared property interface, hence one arm each.
        /// </summary>
        private ICanvasImage Lighting(LightingPrimitive p, ICanvasImage input, Matrix3x2 space, float ax, float ay)
        {
            var color = p.Linear ? ToLinear(p.Color) : p.Color;
            var zScale = MathF.Sqrt((ax * ax + ay * ay) / 2);
            Vector3 Map(Vector3 v) => new(Vector2.Transform(new Vector2(v.X, v.Y), space), v.Z * zScale);
            static float Rad(float deg) => deg * MathF.PI / 180f;
            var l = p.Light;
            float azimuth = Rad(l.Azimuth), elevation = Rad(l.Elevation);
            var position = Map(l.Position);
            var target = Map(l.Target);
            var cone = l.ConeAngle is { } c ? Rad(Math.Clamp(MathF.Abs(c), 0, 90)) : MathF.PI / 2;
            float surface = p.SurfaceScale, amount = p.Constant, exponent = Math.Clamp(p.Exponent, 1f, 128f);
            return Keep<ICanvasImage>((p.Specular, l.Kind) switch
            {
                (false, LightKind.Distant) => new DistantDiffuseEffect { Source = input, Azimuth = azimuth, Elevation = elevation, DiffuseAmount = amount, HeightMapScale = surface, LightColor = color },
                (false, LightKind.Point) => new PointDiffuseEffect { Source = input, LightPosition = position, DiffuseAmount = amount, HeightMapScale = surface, LightColor = color },
                (false, _) => new SpotDiffuseEffect { Source = input, LightPosition = position, LightTarget = target, Focus = l.Focus, LimitingConeAngle = cone, DiffuseAmount = amount, HeightMapScale = surface, LightColor = color },
                (true, LightKind.Distant) => new DistantSpecularEffect { Source = input, Azimuth = azimuth, Elevation = elevation, SpecularAmount = amount, SpecularExponent = exponent, HeightMapScale = surface, LightColor = color },
                (true, LightKind.Point) => new PointSpecularEffect { Source = input, LightPosition = position, SpecularAmount = amount, SpecularExponent = exponent, HeightMapScale = surface, LightColor = color },
                (true, _) => new SpotSpecularEffect { Source = input, LightPosition = position, LightTarget = target, Focus = l.Focus, LimitingConeAngle = cone, SpecularAmount = amount, SpecularExponent = exponent, HeightMapScale = surface, LightColor = color },
            });
        }

        /// <summary>Gaussian blur with per-axis deviations in pixels (ApplyFilter keeps them ≤ MaxBlurDev).</summary>
        private ICanvasImage Blur(ICanvasImage img, float sdx, float sdy)
        {
            if (sdx <= 0 && sdy <= 0) return img;
            if (MathF.Abs(sdx - sdy) < 0.01f)
                return Keep(new GaussianBlurEffect { Source = img, BlurAmount = sdx, BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Balanced });
            if (sdx > 0) img = Keep(new DirectionalBlurEffect { Source = img, BlurAmount = sdx, Angle = 0, BorderMode = EffectBorderMode.Soft });
            if (sdy > 0) img = Keep(new DirectionalBlurEffect { Source = img, BlurAmount = sdy, Angle = MathF.PI / 2, BorderMode = EffectBorderMode.Soft });
            return img;
        }

        /// <summary>
        /// feMorphology with radii in pixels: kernel 2·round(r)+1, clamped to Direct2D's 1..100. A zero radius
        /// disables the effect.
        /// </summary>
        private ICanvasImage Morphology(ICanvasImage img, bool dilate, float rx, float ry)
        {
            if (rx <= 0 || ry <= 0) return img;
            static int Kernel(float r) => Math.Clamp((int)MathF.Round(r) * 2 + 1, 1, 100);
            return Keep(new MorphologyEffect
            {
                Source = img, Mode = dilate ? MorphologyEffectMode.Dilate : MorphologyEffectMode.Erode,
                Width = Kernel(rx), Height = Kernel(ry),
            });
        }

        /// <summary>
        /// feComposite: <paramref name="in1"/> (source) composited onto <paramref name="in2"/> (destination);
        /// arithmetic computes k1·in1·in2 + k2·in1 + k3·in2 + k4.
        /// </summary>
        private ICanvasImage Composite(ICanvasImage in1, ICanvasImage in2, CompositeOp op, Vector4 k)
        {
            if (op == CompositeOp.Arithmetic)
                return Keep(new ArithmeticCompositeEffect
                {
                    Source1 = in1, Source2 = in2,
                    MultiplyAmount = k.X, Source1Amount = k.Y, Source2Amount = k.Z, Offset = k.W, ClampOutput = true,
                });
            var mode = op switch
            {
                CompositeOp.In => CanvasComposite.SourceIn, CompositeOp.Out => CanvasComposite.SourceOut,
                CompositeOp.Atop => CanvasComposite.SourceAtop, CompositeOp.Xor => CanvasComposite.Xor,
                CompositeOp.Lighter => CanvasComposite.Add, _ => CanvasComposite.SourceOver,
            };
            // CompositeEffect draws Sources[0] as the destination and Sources[1] as the source.
            return Keep(new CompositeEffect { Mode = mode, Sources = { in2, in1 } });
        }

        /// <summary>A valid table for disabled channels (Direct2D needs one even when the channel is off).</summary>
        private static readonly float[] IdentityTable = [0, 1];

        /// <summary>
        /// feComponentTransfer: one Direct2D transfer effect per function kind present, each covering every
        /// channel of that kind (the others pass through). Channels are independent, so this equals applying
        /// them one at a time.
        /// </summary>
        private ICanvasImage ComponentTransfer(ICanvasImage img, Transfer[] ch)
        {
            // An empty table/discrete list is the identity, per spec.
            bool Uses(int c, TransferKind k) => ch[c].Kind == k
                && (k is not (TransferKind.Table or TransferKind.Discrete) || ch[c].Values.Length > 0);
            bool Any(TransferKind k) => Uses(0, k) || Uses(1, k) || Uses(2, k) || Uses(3, k);
            float[] Table(int c, TransferKind k) => Uses(c, k) ? ch[c].Values : IdentityTable;
            float V(int c, TransferKind k, int i) => Uses(c, k) ? ch[c].Values[i] : 0f;

            if (Any(TransferKind.Table))
            {
                const TransferKind k = TransferKind.Table;
                img = Keep(new TableTransferEffect
                {
                    Source = img, ClampOutput = true,
                    RedDisable = !Uses(0, k), GreenDisable = !Uses(1, k), BlueDisable = !Uses(2, k), AlphaDisable = !Uses(3, k),
                    RedTable = Table(0, k), GreenTable = Table(1, k), BlueTable = Table(2, k), AlphaTable = Table(3, k),
                });
            }
            if (Any(TransferKind.Discrete))
            {
                const TransferKind k = TransferKind.Discrete;
                img = Keep(new DiscreteTransferEffect
                {
                    Source = img, ClampOutput = true,
                    RedDisable = !Uses(0, k), GreenDisable = !Uses(1, k), BlueDisable = !Uses(2, k), AlphaDisable = !Uses(3, k),
                    RedTable = Table(0, k), GreenTable = Table(1, k), BlueTable = Table(2, k), AlphaTable = Table(3, k),
                });
            }
            if (Any(TransferKind.Linear))
            {
                const TransferKind k = TransferKind.Linear; // values: slope, intercept
                img = Keep(new LinearTransferEffect
                {
                    Source = img, ClampOutput = true,
                    RedDisable = !Uses(0, k), GreenDisable = !Uses(1, k), BlueDisable = !Uses(2, k), AlphaDisable = !Uses(3, k),
                    RedSlope = V(0, k, 0), GreenSlope = V(1, k, 0), BlueSlope = V(2, k, 0), AlphaSlope = V(3, k, 0),
                    RedOffset = V(0, k, 1), GreenOffset = V(1, k, 1), BlueOffset = V(2, k, 1), AlphaOffset = V(3, k, 1),
                });
            }
            if (Any(TransferKind.Gamma))
            {
                const TransferKind k = TransferKind.Gamma; // values: amplitude, exponent, offset
                img = Keep(new GammaTransferEffect
                {
                    Source = img, ClampOutput = true,
                    RedDisable = !Uses(0, k), GreenDisable = !Uses(1, k), BlueDisable = !Uses(2, k), AlphaDisable = !Uses(3, k),
                    RedAmplitude = V(0, k, 0), GreenAmplitude = V(1, k, 0), BlueAmplitude = V(2, k, 0), AlphaAmplitude = V(3, k, 0),
                    RedExponent = V(0, k, 1), GreenExponent = V(1, k, 1), BlueExponent = V(2, k, 1), AlphaExponent = V(3, k, 1),
                    RedOffset = V(0, k, 2), GreenOffset = V(1, k, 2), BlueOffset = V(2, k, 2), AlphaOffset = V(3, k, 2),
                });
            }
            return img;
        }
    }
}
