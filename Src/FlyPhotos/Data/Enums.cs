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

    public enum Origin
    {
        DiskCache,
        Disk,
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

    public enum DefaultMouseWheelBehavior
    {
        Zoom,
        Navigate
    }

    public enum ThumbnailSetting
    {
        ShowHide,
        SelectionColor,
        Size
    }

}
