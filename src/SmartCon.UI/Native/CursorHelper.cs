using System.Runtime.InteropServices;
using System.Windows;

namespace SmartCon.UI.Native;

public static class CursorHelper
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public static Point GetCursorPosition()
    {
        if (!GetCursorPos(out var pt)) return default;
        return new Point(pt.X, pt.Y);
    }
}
