using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
namespace GameScreenTranslator;

/// <summary>画面上の範囲(物理ピクセル)。</summary>
public record ScreenRect(int X, int Y, int Width, int Height)
{
    public bool IntersectsWith(ScreenRect other) =>
        X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
}

/// <summary>Windows の機能(画面の撮影・カーソル位置・キーの状態・ホットキー・窓の性質)を呼び出す。</summary>
public static class ScreenCapture
{
    /// <summary>画面の指定範囲を撮影する(ゲームには触れず、表示されている画面をコピーするだけ)。</summary>
    public static BitmapSource Capture(ScreenRect rect)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, rect.Width, rect.Height);
        var old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("画面を撮影できませんでした。");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>マウスカーソルの位置(物理ピクセル)。</summary>
    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>キーがいま押されているか(このアプリが手前になくても分かる)。</summary>
    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;

    /// <summary>窓をクリックが通り抜ける状態にする/戻す。</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = enabled ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>窓の範囲(物理ピクセル)。</summary>
    public static ScreenRect? WindowRect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) ? new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }

    public static bool RegisterHotKey(Window window, int id, uint modifiers, uint key) => RegisterHotKey(new WindowInteropHelper(window).Handle, id, modifiers | MOD_NOREPEAT, key);
    public static void UnregisterHotKey(Window window, int id) => UnregisterHotKey(new WindowInteropHelper(window).Handle, id);
    public const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
