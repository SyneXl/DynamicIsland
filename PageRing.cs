namespace DynamicIsland;

// Page zero is always Home. Media pages follow the live session order.
static class PageRing
{
    public static int Next(int current, int sources, int direction)
    {
        int count = Math.Max(0, sources) + 1;
        current = Math.Clamp(current, 0, count - 1);
        return (current + Math.Sign(direction) + count) % count;
    }
}
