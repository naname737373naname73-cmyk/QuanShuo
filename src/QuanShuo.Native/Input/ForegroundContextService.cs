using System.Diagnostics;
using System.Runtime.InteropServices;
using QuanShuo.Core.Models;

namespace QuanShuo.Native.Input;

public sealed class ForegroundContextService
{
    public CaptureMetadata CaptureMetadata()
    {
        IntPtr window = GetForegroundWindow();
        string processName = window == IntPtr.Zero ? "Windows" : GetProcessName(window);
        int length = GetWindowTextLength(window);
        string title = length <= 0 ? string.Empty : GetWindowTitle(window, length + 1);
        return new CaptureMetadata
        {
            Timestamp = DateTimeOffset.Now,
            AppName = string.IsNullOrWhiteSpace(processName) ? "未知程序" : processName,
            WindowTitle = title,
        };
    }

    private static string GetProcessName(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string GetWindowTitle(IntPtr window, int capacity)
    {
        var buffer = new System.Text.StringBuilder(capacity);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximumCount);
}
