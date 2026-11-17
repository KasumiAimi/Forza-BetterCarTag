using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FH6GarageEditor;

internal static class ExistingEditorWindow
{
    private const string WindowMarker = "FH6GarageEditor.MainWindow";
    private const int Restore = 9;

    internal static void Mark(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!SetProp(handle, WindowMarker, 1) || HwndSource.FromHwnd(handle) is not { } source) return;
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x0082) RemoveProp(hwnd, WindowMarker); // WM_NCDESTROY
            return 0;
        });
    }

    internal static void Activate()
    {
        nint window = 0;
        var timeout = Stopwatch.StartNew();
        // A second launch can arrive before the first one creates its window.
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            EnumWindows((handle, _) =>
            {
                if (GetProp(handle, WindowMarker) == 0 || !IsWindowVisible(handle)) return true;
                window = handle;
                return false;
            }, 0);
            if (window != 0) break;
            Thread.Sleep(50);
        }
        if (window == 0) return;
        if (IsIconic(window)) ShowWindowAsync(window, Restore);
        if (!SetForegroundWindow(window)) FlashWindow(window, true);
    }

    private delegate bool EnumWindowsCallback(nint window, nint state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(nint window, string name, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(nint window, bool invert);
}
