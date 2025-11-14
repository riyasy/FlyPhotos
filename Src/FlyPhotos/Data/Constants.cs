namespace FlyPhotos.Data
{
    internal static class Constants
    {
        // Pan Zoom Animation Related
        public const int PanZoomAnimationDurationForExit = 200;
        public const int PanZoomAnimationDurationNormal = 400;
        public const int OffScreenDrawDelayMs = 410;

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
