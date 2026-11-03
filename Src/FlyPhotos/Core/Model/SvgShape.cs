#nullable enable
using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace FlyPhotos.Core.Model;

internal enum SvgPaintKind : byte { Solid, LinearGradient, RadialGradient }

/// <summary>What is needed to recreate an SVG paint's brush: a solid color or a gradient's parameters.</summary>
internal sealed class SvgPaint
{
    public required SvgPaintKind Kind { get; init; }
    /// <summary>Straight-alpha color; solid paints only.</summary>
    public Color Solid { get; init; }
    /// <summary>Linear: the gradient start. Radial: the center.</summary>
    public Vector2 Start { get; init; }
    /// <summary>Linear: the gradient end. Radial: the focal point.</summary>
    public Vector2 End { get; init; }
    /// <summary>Radial gradients only.</summary>
    public float Radius { get; init; }
    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;
    public CanvasEdgeBehavior Edge { get; init; }
    public float Opacity { get; init; } = 1;
    public CanvasGradientStop[] Stops { get; init; } = [];
}

/// <summary>
/// One fill or stroke exactly as the command stream describes it, in the op's local coordinates. The scene keeps it
/// next to the item's recorded command list so a huge item can be re-drawn from a pre-chopped copy of its path.
/// </summary>
internal sealed class SvgShape
{
    public required bool IsStroke { get; init; }
    /// <summary>Op-local to SVG user space.</summary>
    public required Matrix3x2 Transform { get; init; }
    /// <summary>Path verbs: 0 Move, 1 Line, 2 Quad, 3 Cubic, 4 Close.</summary>
    public required byte[] Verbs { get; init; }
    /// <summary>Path points as x,y pairs.</summary>
    public required float[] Points { get; init; }
    /// <summary>0 nonzero, 1 evenodd; always 0 for strokes.</summary>
    public required byte FillRule { get; init; }
    public required bool Aliased { get; init; }
    public required SvgPaint Paint { get; init; }

    // Strokes only.
    public float Width { get; init; }
    public CanvasCapStyle Cap { get; init; }
    public CanvasLineJoin Join { get; init; }
    public float MiterLimit { get; init; }
    /// <summary>Dash lengths in op-local units (empty: solid), as in the stream.</summary>
    public float[] Dashes { get; init; } = [];
    public float DashOffset { get; init; }

    /// <summary>
    /// How far a stroke can reach beyond its path, in op-local units: half the width, widened by a miter join
    /// (up to the miter limit) or a square cap (the corner is at 45 degrees).
    /// </summary>
    public float StrokeOutset
    {
        get
        {
            var outset = Width / 2;
            if (Join is CanvasLineJoin.Miter or CanvasLineJoin.MiterOrBevel) outset *= Math.Max(1f, MiterLimit);
            if (Cap == CanvasCapStyle.Square) outset *= MathF.Sqrt(2f);
            return outset;
        }
    }
}
