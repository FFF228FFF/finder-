using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ArtFinder
{
    // Тёмный заголовок окна (Windows 10 1809+ / Windows 11). Без этого
    // системная полоса заголовка остаётся белой над тёмным интерфейсом.
    internal static class WindowTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 до 20H1
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;               // только Windows 11

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void ApplyDark(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

                // Цвет заголовка = фон окна (#0B0B10), COLORREF — 0x00BBGGRR
                int caption = 0x00100B0B;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
            catch { /* старая Windows — остаётся системный заголовок */ }
        }
    }
}
