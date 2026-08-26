using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TokensLimitsExtension;

/// <summary>
/// Keeps the detailed page opened from the Dock centered over the item that was
/// clicked. PowerToys currently exposes only an edge anchor for transient pages,
/// so this small adapter adjusts the already-created Command Palette window.
/// </summary>
internal static partial class DockPalettePositioner
{
    private const string CommandPaletteProcessName = "Microsoft.CmdPal.UI";
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void ScheduleCenterOnInvokingWidget(Action<string> logger)
    {
        try
        {
            if (!GetCursorPos(out var cursorPosition))
            {
                return;
            }

            // A visible, uncloaked palette means this is the page's periodic
            // refresh, not a new Dock invocation. Do not move an open window to
            // the user's current cursor position in that case.
            var existingWindow = FindCommandPaletteWindow();
            if (existingWindow != IntPtr.Zero && IsWindowVisible(existingWindow) && !IsCloaked(existingWindow))
            {
                return;
            }

            _ = PositionWhenPaletteAppearsAsync(cursorPosition, logger);
        }
        catch (Exception ex)
        {
            logger($"[TokensLimits] Dock palette positioning failed: {ex.Message}");
        }
    }

    private static async Task PositionWhenPaletteAppearsAsync(
        NativePoint cursorPosition,
        Action<string> logger)
    {
        try
        {
            // The host first loads the page and then reveals the transient palette.
            // Poll briefly so this remains race-free without blocking the extension
            // COM call that supplies the page items.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(attempt == 0 ? 20 : 50).ConfigureAwait(false);

                var paletteWindow = FindCommandPaletteWindow();
                if (paletteWindow == IntPtr.Zero || !IsWindowVisible(paletteWindow) || IsCloaked(paletteWindow))
                {
                    continue;
                }

                if (TryCenterAlongDockAxis(paletteWindow, cursorPosition, out var x, out var y))
                {
                    if (SetWindowPos(
                        paletteWindow,
                        IntPtr.Zero,
                        x,
                        y,
                        0,
                        0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate))
                    {
                        logger($"[TokensLimits] Dock palette centered at ({x}, {y}) for widget click ({cursorPosition.X}, {cursorPosition.Y}).");
                    }

                    return;
                }
            }

            logger("[TokensLimits] Dock palette window was not found for repositioning.");
        }
        catch (Exception ex)
        {
            logger($"[TokensLimits] Dock palette positioning failed: {ex.Message}");
        }
    }

    private static bool TryCenterAlongDockAxis(
        IntPtr paletteWindow,
        NativePoint cursorPosition,
        out int x,
        out int y)
    {
        x = 0;
        y = 0;

        if (!GetWindowRect(paletteWindow, out var windowRect))
        {
            return false;
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var distanceToHorizontalDockEdge = Math.Min(
            Math.Abs(windowRect.Top - cursorPosition.Y),
            Math.Abs(windowRect.Bottom - cursorPosition.Y));
        var distanceToVerticalDockEdge = Math.Min(
            Math.Abs(windowRect.Left - cursorPosition.X),
            Math.Abs(windowRect.Right - cursorPosition.X));

        // Top/bottom docks need horizontal centering; left/right docks need
        // vertical centering. Keep the host-selected coordinate on the dock
        // axis so the popup still opens on the correct side of the Dock.
        if (distanceToHorizontalDockEdge <= distanceToVerticalDockEdge)
        {
            x = cursorPosition.X - (width / 2);
            y = windowRect.Top;
        }
        else
        {
            x = windowRect.Left;
            y = cursorPosition.Y - (height / 2);
        }

        ClampToMonitorWorkArea(cursorPosition, width, height, ref x, ref y);
        return true;
    }

    private static void ClampToMonitorWorkArea(
        NativePoint cursorPosition,
        int width,
        int height,
        ref int x,
        ref int y)
    {
        var monitor = MonitorFromPoint(cursorPosition, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        x = Math.Clamp(x, monitorInfo.Work.Left, Math.Max(monitorInfo.Work.Left, monitorInfo.Work.Right - width));
        y = Math.Clamp(y, monitorInfo.Work.Top, Math.Max(monitorInfo.Work.Top, monitorInfo.Work.Bottom - height));
    }

    private static IntPtr FindCommandPaletteWindow()
    {
        var processIds = new System.Collections.Generic.HashSet<int>();
        foreach (var process in Process.GetProcessesByName(CommandPaletteProcessName))
        {
            try
            {
                processIds.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
                // The host may be restarting between polling attempts.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (processIds.Count == 0)
        {
            return IntPtr.Zero;
        }

        var largestWindow = IntPtr.Zero;
        long largestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!processIds.Contains((int)processId) || !IsWindowVisible(hwnd))
            {
                return true;
            }

            var titleBuffer = Marshal.AllocHGlobal(128 * sizeof(char));
            string title;
            try
            {
                var titleLength = GetWindowText(hwnd, titleBuffer, 128);
                title = Marshal.PtrToStringUni(titleBuffer, Math.Max(0, titleLength)) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(titleBuffer);
            }

            if (string.Equals(title, "PowerDock", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var area = (long)width * height;
            if (width >= 300 && height >= 200 && area > largestArea)
            {
                largestArea = area;
                largestWindow = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return largestWindow;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    private static partial int GetWindowText(IntPtr hwnd, IntPtr text, int maxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out int value,
        int valueSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
