using FlyPhotos.Utils;
using System;

namespace FlyPhotos.Data
{
    internal static class Constants
    {
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

        public static string ShortCuts = $"{Environment.NewLine}Left/Right Arrow Keys : Navigate Photos" +
                                            $"{Environment.NewLine}Up/Down Arrow Keys : Zoom In or Out" +
                                            $"{Environment.NewLine}Mouse Left Click and Drag : Pan Photo" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}Mouse Wheel : Zoom In or Out/ Navigate Photos - based on setting" +
                                            $"{Environment.NewLine}Ctrl + Mouse Wheel : Zoom In or Out" +
                                            $"{Environment.NewLine}Alt + Mouse Wheel : Navigate Photos" +
                                            $"{Environment.NewLine}Tilt Mouse Wheel Left or Right: Navigate Photos" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}Ctrl + 'Arrow Keys' : Pan Photo" +
                                            $"{Environment.NewLine}Ctrl + '+' : Zoom In" +
                                            $"{Environment.NewLine}Ctrl + '-' : Zoom Out" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}Alt + 'Left/Right Arrow Keys' : Navigate pages in multi-page TIFF" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}Page Up/Page Down : Zoom In/Out to next Preset (100%,400%,Fit)" +
                                            $"{Environment.NewLine}Home : Navigate to first photo" +
                                            $"{Environment.NewLine}End : Navigate to last photo" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}Mouse wheel on Thumbnail strip: Navigate Photos" +
                                            $"{Environment.NewLine}Mouse wheel on On Screen Left/Right Button: Navigate Photos" +
                                            $"{Environment.NewLine}Mouse wheel on On Screen Rotate Button: Rotate Photo" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}TouchPad two finger swipe Left or Right: Navigate Photos" +
                                            $"{Environment.NewLine}TouchPad two finger swipe Up or Down: Zoom In or Out/ Navigate Photos - based on Mouse Wheel setting" +
                                            $"{Environment.NewLine}TouchPad pinch open or close: Zoom In or Out" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}D : Show File Properties" +
                                            $"{Environment.NewLine}Del : Delete Photo" +
                                            Environment.NewLine +
                                            $"{Environment.NewLine}Esc : Close Settings or Exit App";

    }
}
