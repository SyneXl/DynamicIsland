using System.Windows;

namespace DynamicIsland;

// An old menu footprint is only a collapse guard, never a window/hit-test region.
sealed class MenuHoverRetention
{
    public Rect Bounds { get; private set; } = Rect.Empty;
    public bool Active => !Bounds.IsEmpty;

    static bool Valid(Rect rect) => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0
        && double.IsFinite(rect.Left) && double.IsFinite(rect.Top) && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    public void Resize(Rect previous, Rect next, Point pointer)
    {
        if (!Valid(previous) || !Valid(next)) { Clear(); return; }
        if (Active && Retains(next, pointer)) return; // Do not accumulate footprints during rapid navigation.
        if ((next.Width < previous.Width - .5 || next.Height < previous.Height - .5)
            && previous.Contains(pointer) && !next.Contains(pointer)) Bounds = previous;
    }

    public bool Retains(Rect destination, Point pointer)
    {
        if (!Active) return false;
        if (!Valid(destination) || !Bounds.Contains(pointer) || destination.Contains(pointer)) { Clear(); return false; }
        return true;
    }

    public void Clear() => Bounds = Rect.Empty;
}
