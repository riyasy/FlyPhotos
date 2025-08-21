namespace FlyPhotos.Data
{
    public enum DisplayLevel
    {
        None,
        PlaceHolder,
        Preview,
        Hq
    }

    public enum NavDirection
    {
        Next,
        Prev
    }

    public enum PreviewSource
    {
        FromDiskCache,
        FromDisk,
        ErrorScreen,
        Undefined
    }

    public enum WindowBackdropType
    {
        None,
        Mica,
        MicaAlt,
        Acrylic,
        AcrylicThin,
        Transparent,
        Frozen
    }

    public enum ZoomDirection
    {
        In,
        Out
    }

}
