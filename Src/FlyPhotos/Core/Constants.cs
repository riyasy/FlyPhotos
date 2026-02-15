namespace FlyPhotos.Core
{
    internal static class Constants
    {
        public const string AppVersion = "2.5.15";

        // Pan Zoom Animation Related
        public const int PanZoomAnimationDurationForExit = 200;
        public const int PanZoomAnimationDurationNormal = 600;
        public const int OffScreenDrawDelayMs = 650;

        // Related to Shrug Animation for Delete Failure
        public const double ShrugAnimationDurationMs = 350;
        public const double ShrugAmplitude = 20; // How many pixels to shake
        public const double ShrugFrequency = 4;  // How many "wiggles"       
        
        // Thumbnail Related
        public const int ThumbnailPadding = 2;
        public const float ThumbnailSelectionBorderThickness = 3.0f;
        public const float ThumbnailCornerRadius = 4.0f;

        // Others
        public const int CheckerSize = 10;
    }
}
