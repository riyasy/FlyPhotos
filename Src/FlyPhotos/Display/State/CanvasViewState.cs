using System;
using System.Numerics;
using Windows.Foundation;
using FlyPhotos.Infra.Configuration;

namespace FlyPhotos.Display.State;

internal class CanvasViewState
{
    public Rect ImageRect;
    public Point ImagePos = new(0, 0);
    public Matrix3x2 Mat;
    public Matrix3x2 MatInv;

    public float Scale = 1.0f;
    public float LastScaleTo = 1.0f;

    public int Rotation = 0;

    public void UpdateTransform()
    {
        // Rounding the translations to integers avoids subpixel rendering glitches seen on some
        // NVIDIA GPUs (#55), but quantizes the animation tail into visible 1px shivers.
        var snap = AppConfig.Settings.UseSubPixelSnapping;

        Mat = Matrix3x2.Identity;

        float translateX1 = (float)(-ImageRect.Width * 0.5f);
        float translateY1 = (float)(-ImageRect.Height * 0.5f);
        if (snap)
        {
            translateX1 = MathF.Round(translateX1);
            translateY1 = MathF.Round(translateY1);
        }
        Mat *= Matrix3x2.CreateTranslation(translateX1, translateY1);

        Mat *= Matrix3x2.CreateScale(Scale, Scale);

        Mat *= Matrix3x2.CreateRotation((float)(Math.PI * Rotation / 180f));

        float translateX2 = (float)ImagePos.X;
        float translateY2 = (float)ImagePos.Y;
        if (snap)
        {
            translateX2 = MathF.Round(translateX2);
            translateY2 = MathF.Round(translateY2);
        }
        Mat *= Matrix3x2.CreateTranslation(translateX2, translateY2);

        // Calculate inverse transform
        Matrix3x2.Invert(Mat, out MatInv);
    }

    public string GetAsString()
    {
        return $@"
--Image Rect Properties--
Bitmap is scaled if its 1:1 is greater than display area.
Display Area is canvas control minus the padding set in settings.
Scaled to ImageRect := [x={ImageRect.X:0.00}, y={ImageRect.Y:0.00}, Width={ImageRect.Width:0.00}, Height={ImageRect.Height:0.00}]
ImagePos := [x={ImagePos.X:0.00}, y={ImagePos.Y:0.00}]

Zoom Scale := {Scale:0.00}
Rotation := {Rotation:00}deg

--Transform Matrix--
Step1: Translate to origin := [x={-ImageRect.Width * 0.5f:0.00}, y={-ImageRect.Height * 0.5f:0.00}]
Step2: Scale := [ScaleX={Scale:0.00}, ScaleY={Scale:0.00}]
Step3: Rotate := {Rotation:00}deg
Step4: Translate to ImagePos := [x={ImagePos.X:0.00}, y={ImagePos.Y:0.00}]

";
    }
}