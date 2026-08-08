namespace WindowShuttle.Core;

public readonly record struct PointPx(int X, int Y);

public readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public long Area => (long)Width * Height;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;

    public bool Contains(PointPx p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public static RectPx FromLTWH(int left, int top, int width, int height)
        => new(left, top, left + width, top + height);

    public static long OverlapArea(RectPx a, RectPx b)
    {
        int w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        int h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w <= 0 || h <= 0 ? 0 : (long)w * h;
    }
}

public enum ShowState { Normal, Minimized, Maximized }
