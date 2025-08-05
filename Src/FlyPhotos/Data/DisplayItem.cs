using Microsoft.Graphics.Canvas;
using System;
using System.Collections.Generic;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Data;

internal class DisplayItem
{
    public byte[] FileAsByteArray { get; set; }

    public enum PreviewSource
    {
        FromDiskCache,
        FromDisk,
        ErrorScreen, 
        Undefined
    }

    public CanvasBitmap Bitmap { get; set; }
    public SoftwareBitmap SoftwareBitmap { get; set; }
    public int Rotation { get; set; }

    public PreviewSource PreviewFrom { get; set; }

    // Animated image support
    public List<CanvasBitmap> AnimationFrames { get; set; }
    public List<TimeSpan> FrameDurations { get; set; }

    public DisplayItem(CanvasBitmap bitmap, PreviewSource previewFrom, int rotation = 0)
    {
        Bitmap = bitmap;
        PreviewFrom = previewFrom;
        Rotation = rotation;
    }

    public DisplayItem(SoftwareBitmap softwareBitmap, PreviewSource previewFrom, int rotation = 0)
    {
        SoftwareBitmap = softwareBitmap;
        PreviewFrom = previewFrom;
        Rotation = rotation;
    }

    public DisplayItem(List<CanvasBitmap> animationFrames, List<TimeSpan> frameDurations, PreviewSource previewFrom, int rotation = 0)
    {
        AnimationFrames = animationFrames;
        FrameDurations = frameDurations;
        PreviewFrom = previewFrom;
        Rotation = rotation;
        Bitmap = AnimationFrames[0];
    }

    public DisplayItem(byte[] fileFileBytes, PreviewSource previewFrom)
    {
        FileAsByteArray = fileFileBytes;
        PreviewFrom = previewFrom;
    }

    public static DisplayItem Empty()
    {
        CanvasBitmap bitmap = null;
        return new DisplayItem(bitmap, PreviewSource.Undefined, 0);
    }

    public bool IsGifOrAnimatedPng()
    {
        return FileAsByteArray != null;
    }
}