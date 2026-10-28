using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace DynamicIsland;

// Read-only MSAA / IAccessible2. HTML video is only a generic Group in UIA;
// the IA2 tag attribute distinguishes it from images, audio and generic containers.
static class VideoAccessibility
{
    const uint ClientObject = 0xFFFFFFFC;
    const int MaxNodes = 160, MaxDepth = 24, MaxChildren = 64;
    static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(160);
    static readonly Guid AccessibleId = new("618736e0-3c3d-11cf-810c-00aa00389b71");
    static readonly Guid Accessible2Id = new("e89f726e-c4f4-4c19-bb19-b647d7fa8478");

    public static bool ContainsFullscreenVideo(IntPtr window, Rect screen)
    {
        var budget = Stopwatch.StartNew();
        // Activate the browser frame's IA2 bridge as well as the renderer bridge.
        // Chromium otherwise exposes only a childless MSAA compatibility stub.
        Guid bridgeId = AccessibleId;
        IAccessible? bridge = null;
        try
        {
            if (AccessibleObjectFromWindow(window, ClientObject, ref bridgeId, out bridge) == 0 && bridge != null)
                _ = Attributes(bridge);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
        finally { Release(bridge); }
        var roots = new List<IntPtr>();
        EnumChildWindows(window, (child, _) =>
        {
            var name = new StringBuilder(80);
            GetClassName(child, name, name.Capacity);
            // Chromium's accessibility-only render-widget HWND can itself be
            // hidden while its document is visible. Check MSAA state instead.
            if (name.ToString() == "Chrome_RenderWidgetHostHWND") roots.Add(child);
            return roots.Count < 16;
        }, IntPtr.Zero);
        // Chromium's render-widget HWND avoids walking the browser chrome. Firefox
        // exposes its document beneath the window's client accessibility object.
        if (roots.Count == 0) roots.Add(window);
        // Warm up only the small renderer bridges, not their document trees.
        foreach (IntPtr root in roots)
        {
            if (budget.Elapsed > ScanBudget) break;
            Guid iid = AccessibleId;
            IAccessible? accessible = null;
            try
            {
                if (AccessibleObjectFromWindow(root, ClientObject, ref iid, out accessible) == 0 && accessible != null)
                    _ = Attributes(accessible);
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
            finally { Release(accessible); }
        }
        // Hidden tabs can retain perfectly ordinary MSAA geometry/state. A hit
        // test selects the document actually displayed, not an inactive renderer.
        var point = new ScreenPoint((int)(screen.Left + screen.Width / 2), (int)(screen.Top + screen.Height / 2));
        if (GetAncestor(WindowFromPoint(point), 2) != window || budget.Elapsed > ScanBudget) return false;
        IAccessible? current = null;
        try
        {
            if (AccessibleObjectFromPoint(point, out current, out _) != 0) return false;
            for (int depth = 0; current != null && depth <= MaxDepth && budget.Elapsed <= ScanBudget; depth++)
            {
                string attributes = Attributes(current);
                int nodes = 0;
                if (VideoSurface.HasVideoTag(attributes) || attributes.Split(';').Any(a => a == "tag:#document"))
                    return Walk(current, screen, budget, ref nodes, 0);
                object? parent = current.accParent;
                Release(current);
                current = parent as IAccessible;
                if (current == null) Release(parent);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException) { }
        finally { Release(current); }
        return false;
    }

    static bool Walk(IAccessible element, Rect screen, Stopwatch budget, ref int nodes, int depth)
    {
        if (++nodes > MaxNodes || depth > MaxDepth || budget.Elapsed > ScanBudget) return false;
        try
        {
            // Activate the renderer IA2 bridge before reading its initial MSAA stub.
            string? attributes = depth == 0 ? Attributes(element) : null;
            const int Invisible = 0x8000, Offscreen = 0x10000;
            if (element[0] is int state && (state & (Invisible | Offscreen)) != 0) return false;
            element.accLocation(out int x, out int y, out int width, out int height, 0);
            if (width <= 0 || height <= 0) return false;
            var bounds = new Rect(x, y, width, height);
            if (!bounds.IntersectsWith(screen)) return false;
            if (VideoSurface.FillsScreen(bounds, screen) && VideoSurface.HasVideoTag(attributes ?? Attributes(element))) return true;
            // A full-screen video cannot be inside a tiny sidebar or toolbar.
            if (width < screen.Width * 0.60 || height < screen.Height * 0.60) return false;

            int count = Math.Clamp(element.accChildCount, 0, MaxChildren);
            if (count == 0) return false;
            var children = new object[count];
            try
            {
                if (AccessibleChildren(element, 0, count, children, out int obtained) < 0) return false;
                for (int i = 0; i < obtained && nodes < MaxNodes && budget.Elapsed <= ScanBudget; i++)
                    if (children[i] is IAccessible child && Walk(child, screen, budget, ref nodes, depth + 1)) return true;
            }
            finally { foreach (object? child in children) Release(child); }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException) { }
        return false;
    }

    static string Attributes(IAccessible accessible)
    {
        if (accessible is not IServiceProvider service) return "";
        Guid sid = AccessibleId, iid = Accessible2Id;
        if (service.QueryService(ref sid, ref iid, out IntPtr pointer) != 0 || pointer == IntPtr.Zero) return "";
        try
        {
            // IAccessible2 ABI: IUnknown(3), IDispatch(4), IAccessible(21), then
            // IA2's 18 methods. get_attributes is slot 45. QI above verifies IID.
            IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), 45 * IntPtr.Size);
            var read = Marshal.GetDelegateForFunctionPointer<GetAttributes>(method);
            IntPtr text = IntPtr.Zero;
            try { return read(pointer, out text) == 0 && text != IntPtr.Zero ? Marshal.PtrToStringBSTR(text) : ""; }
            finally { if (text != IntPtr.Zero) Marshal.FreeBSTR(text); }
        }
        finally { Marshal.Release(pointer); }
    }

    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetAttributes(IntPtr self, out IntPtr attributes);
    delegate bool EnumProc(IntPtr window, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] readonly struct ScreenPoint(int x, int y) { public readonly int X = x, Y = y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, EnumProc callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr window, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromPoint(ScreenPoint point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("oleacc.dll")] static extern int AccessibleChildren(IAccessible accessible, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);

    [ComImport, Guid("618736e0-3c3d-11cf-810c-00aa00389b71"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IAccessible
    {
        [DispId(-5000)] object? accParent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(-5001)] int accChildCount { get; }
        [DispId(-5007)] object this[[MarshalAs(UnmanagedType.Struct)] object child] { [return: MarshalAs(UnmanagedType.Struct)] get; }
        [DispId(-5015)] void accLocation(out int x, out int y, out int width, out int height, [MarshalAs(UnmanagedType.Struct)] object child);
    }
    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
    }
}





