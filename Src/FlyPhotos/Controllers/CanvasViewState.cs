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

        public void UpdateTransform()
        {
            Mat = Matrix3x2.Identity;
            Mat *= Matrix3x2.CreateTranslation((float)(-ImageRect.Width * 0.5f), (float)(-ImageRect.Height * 0.5f));
            Mat *= Matrix3x2.CreateScale(Scale, Scale);
            Mat *= Matrix3x2.CreateRotation((float)(Math.PI * Rotation / 180f));
            Mat *= Matrix3x2.CreateTranslation((float)ImagePos.X, (float)ImagePos.Y);
            Matrix3x2.Invert(Mat, out MatInv);
        }
    }
}