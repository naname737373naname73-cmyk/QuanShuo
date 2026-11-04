using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuanShuo.Native.Recording;

/// <summary>文字要贴回去的窗口。</summary>
public readonly record struct PasteTarget(IntPtr Window, string AppName);

/// <summary>把剪贴板里的文字贴进另一个程序。</summary>
public static class PasteService
{
    private const int ForegroundTimeoutMilliseconds = 600;
    private const int ModifierTimeoutMilliseconds = 1500;
    private const int PollMilliseconds = 15;
    private const int SwRestore = 9;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkRightWin = 0x5C;
    private const ushort VkV = 0x56;

    // 任务栏、桌面、开始菜单这些不是能接收文字的窗口
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
        "NotifyIconOverflowWindow",
    };

    /// <summary>当前前台窗口。是圈说自己或系统外壳时返回 null。</summary>
    public static PasteTarget? ForegroundTarget()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId) return null;
        var className = new StringBuilder(128);
        if (GetClassName(window, className, className.Capacity) > 0 && ShellClasses.Contains(className.ToString())) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new PasteTarget(window, process.ProcessName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool IsAlive(IntPtr window) => IsWindow(window);

    /// <summary>切到目标窗口并发送 Ctrl+V。窗口切不过去时返回 false，文字还在剪贴板里。</summary>
    public static async Task<bool> PasteAsync(IntPtr window, Func<bool>? canPaste = null)
    {
        if (!IsWindow(window)) return false;
        // 第二次按快捷键时 Ctrl+Alt 可能还按着，这时发 V 就成了 Ctrl+Alt+V
        var waiting = Stopwatch.StartNew();
        while (AnyModifierDown() && waiting.ElapsedMilliseconds < ModifierTimeoutMilliseconds) await Task.Delay(PollMilliseconds);
        if (AnyModifierDown() || canPaste?.Invoke() == false) return false;
        if (IsIconic(window)) ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
        waiting.Restart();
        while (GetForegroundWindow() != window)
        {
            if (waiting.ElapsedMilliseconds >= ForegroundTimeoutMilliseconds || canPaste?.Invoke() == false) return false;
            await Task.Delay(PollMilliseconds);
        }

        if (AnyModifierDown() || canPaste?.Invoke() == false || GetForegroundWindow() != window) return false;
        Input[] inputs =
        [
            Key(VkControl, 0),
            Key(VkV, 0),
            Key(VkV, KeyUp),
            Key(VkControl, KeyUp),
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static bool AnyModifierDown()
    {
        foreach (ushort key in new[] { VkControl, VkShift, VkMenu, VkLeftWin, VkRightWin })
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        }

        return false;
    }

    private static Input Key(ushort key, uint flags) => new() { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    // 联合体的大小由最大的成员决定。只声明键盘成员的话结构体偏小，SendInput 会直接拒绝。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
