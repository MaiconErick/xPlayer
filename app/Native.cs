using System.Runtime.InteropServices;

namespace xPlayer;

static class Native
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;
    public const uint VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;

    /// <summary>Dark title bar in the app's night colour (Windows 11; harmlessly ignored elsewhere).</summary>
    public static void DarkTitleBar(IntPtr hwnd, int captionBgr, int textBgr)
    {
        int one = 1;
        DwmSetWindowAttribute(hwnd, 20, ref one, 4);               // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref captionBgr, 4);        // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref textBgr, 4);           // DWMWA_TEXT_COLOR
        DwmSetWindowAttribute(hwnd, 34, ref captionBgr, 4);        // DWMWA_BORDER_COLOR
    }
}
