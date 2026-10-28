using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DynamicIsland;

static class DesktopComposition
{
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, bool topmost, out IntPtr target);
    }

    public static DesktopWindowTarget TargetFor(Compositor compositor, IntPtr hwnd)
    {
        compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(hwnd, false, out IntPtr target);
        DesktopWindowTarget windowTarget = DesktopWindowTarget.FromAbi(target);
        Marshal.Release(target);
        return windowTarget;
    }
}
