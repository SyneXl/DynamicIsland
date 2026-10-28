using System.Windows;

namespace DynamicIsland;

static class VideoSurface
{
    public static bool HasVideoTag(string attributes) => attributes.Split(';')
        .Any(part => part.Equals("tag:video", StringComparison.OrdinalIgnoreCase));

    // Letterboxing is allowed, but an ordinary inline video in an F11 page is not.
    public static bool FillsScreen(Rect video, Rect screen)
    {
        if (video.IsEmpty || screen.IsEmpty || screen.Width <= 0 || screen.Height <= 0
            || !double.IsFinite(video.X + video.Y + video.Width + video.Height)) return false;
        Rect visible = Rect.Intersect(video, screen);
        if (visible.IsEmpty) return false;
        double w = visible.Width / screen.Width, h = visible.Height / screen.Height;
        double cx = Math.Abs(video.X + video.Width / 2 - screen.X - screen.Width / 2) / screen.Width;
        double cy = Math.Abs(video.Y + video.Height / 2 - screen.Y - screen.Height / 2) / screen.Height;
        return w * h >= 0.62 && w >= 0.60 && h >= 0.60 && (w >= 0.94 || h >= 0.94)
            && cx <= 0.04 && cy <= 0.04;
    }
}
