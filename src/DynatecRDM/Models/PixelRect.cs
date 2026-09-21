namespace DynatecRDM.Models;

/// <summary>A rectangle in physical virtual-desktop pixels, as Windows and the .rdp file use them.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public PixelRect Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    /// <summary>Area shared with <paramref name="other"/>; zero when they do not touch.</summary>
    public long OverlapArea(PixelRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w > 0 && h > 0 ? (long)w * h : 0;
    }

    public bool Contains(PixelRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}
