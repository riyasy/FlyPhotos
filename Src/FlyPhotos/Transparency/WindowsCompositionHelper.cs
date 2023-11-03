#nullable enable
using System;
using System.Runtime.InteropServices;
using WinCompositor = Windows.UI.Composition.Compositor;
using WinDispatcherQueue = Windows.System.DispatcherQueue;
using WinDispatcherQueueController = Windows.System.DispatcherQueueController;

namespace FlyPhotos.Transparency;

public static class WindowsCompositionHelper
{
    private static WinCompositor? _compositor;
    private static WinDispatcherQueue? _dispatcherQueue;
    private static WinDispatcherQueueController? _dispatcherQueueController;
    private static readonly object Locker = new();

    public static WinCompositor Compositor => EnsureCompositor();

    private static WinCompositor EnsureCompositor()
    {
        if (_compositor == null)
            lock (Locker)
            {
                if (_compositor == null)
                {
                    _dispatcherQueue = WinDispatcherQueue.GetForCurrentThread()
                                       ?? (_dispatcherQueueController = InitializeCoreDispatcher()).DispatcherQueue;

                    _compositor = new WinCompositor();
                }
            }

        return _compositor;
    }

    private static WinDispatcherQueueController InitializeCoreDispatcher()
    {
        var options = new DispatcherQueueOptions
        {
            apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
            threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
            dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions))
        };

        CreateDispatcherQueueController(options, out var raw);

        return WinDispatcherQueueController.FromAbi(raw);
    }

    [Guid("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInspectable
    {
        void GetIids();
        int GetRuntimeClassName([Out][MarshalAs(UnmanagedType.HString)] out string name);
        void GetTrustLevel();
    }

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwndTarget, bool isTopmost, out IntPtr test);
    }

    private enum DISPATCHERQUEUE_THREAD_APARTMENTTYPE
    {
        DQTAT_COM_NONE = 0,
        DQTAT_COM_ASTA = 1,
        DQTAT_COM_STA = 2
    }

    private enum DISPATCHERQUEUE_THREAD_TYPE
    {
        DQTYPE_THREAD_DEDICATED = 1,
        DQTYPE_THREAD_CURRENT = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;

        [MarshalAs(UnmanagedType.I4)] public DISPATCHERQUEUE_THREAD_TYPE threadType;

        [MarshalAs(UnmanagedType.I4)] public DISPATCHERQUEUE_THREAD_APARTMENTTYPE apartmentType;
    }

    [DllImport("coremessaging.dll", EntryPoint = "CreateDispatcherQueueController", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDispatcherQueueController(DispatcherQueueOptions options,
        out IntPtr dispatcherQueueController);
}