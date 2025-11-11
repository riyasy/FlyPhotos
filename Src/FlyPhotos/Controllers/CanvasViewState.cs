using System;
using System.Numerics;
using Windows.Foundation;

namespace FlyPhotos.Controllers
{
    internal class CanvasViewState
    {
        public Rect ImageRect;
        public Point ImagePos = new(0, 0);
        public Matrix3x2 Mat;
        public Matrix3x2 MatInv;

        public float Scale = 1.0f;
        public float LastScaleTo = 1.0f;

        public int Rotation = 0;

        /// <summary>
        /// Applies the core view properties from another state object to this one.
        /// </summary>
        public void Apply(CanvasViewState source)
        {
            this.Scale = source.Scale;
            this.LastScaleTo = source.LastScaleTo;
            this.ImagePos = source.ImagePos;
            this.Rotation = source.Rotation;
        }

        /// <summary>
        /// Creates a new CanvasViewState instance with a copy of the core view properties.
        /// </summary>
        public CanvasViewState Clone()
        {
            return new CanvasViewState
            {
                Scale = this.Scale,
                LastScaleTo = this.LastScaleTo,
                ImagePos = this.ImagePos,
                Rotation = this.Rotation
            };
        }

        public void UpdateTransform()
        {
            Mat = Matrix3x2.Identity;

            // Round the first translation to nearest integer to avoid subpixel rendering
            float translateX1 = MathF.Round((float)(-ImageRect.Width * 0.5f));
            float translateY1 = MathF.Round((float)(-ImageRect.Height * 0.5f));
            Mat *= Matrix3x2.CreateTranslation(translateX1, translateY1);

            // Scale operation remains unchanged
            Mat *= Matrix3x2.CreateScale(Scale, Scale);

            // Rotation remains unchanged
            Mat *= Matrix3x2.CreateRotation((float)(Math.PI * Rotation / 180f));

            // Round the second translation to nearest integer to avoid subpixel rendering
            float translateX2 = MathF.Round((float)ImagePos.X);
            float translateY2 = MathF.Round((float)ImagePos.Y);
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
}