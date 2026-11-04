using System.Runtime.InteropServices;

namespace QuanShuo.Native.Input;

public sealed class GlobalHotkeyService : IDisposable
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private readonly HotkeyWindow window;
    private HotkeyDefinition definition;

    public GlobalHotkeyService(string hotkey = "Ctrl+Alt+A")
    {
        definition = Parse(hotkey);
        window = new HotkeyWindow();
        window.HotkeyPressed += (_, _) => HotkeyPressed?.Invoke(this, EventArgs.Empty);
        if (!RegisterHotKey(window.Handle, 1, definition.Modifiers | ModNoRepeat, (uint)definition.Key))
        {
            window.Dispose();
            throw new InvalidOperationException(hotkey + " 已被其他程序占用，请在设置里更换备用快捷键。");
        }
    }

    public event EventHandler? HotkeyPressed;

    public static void ValidateHotkey(string hotkey) => Parse(hotkey);

    public void Update(string hotkey)
    {
        HotkeyDefinition updated = Parse(hotkey);
        if (updated == definition) return;
        UnregisterHotKey(window.Handle, 1);
        if (!RegisterHotKey(window.Handle, 1, updated.Modifiers | ModNoRepeat, (uint)updated.Key))
        {
            RegisterHotKey(window.Handle, 1, definition.Modifiers | ModNoRepeat, (uint)definition.Key);
            throw new InvalidOperationException(hotkey + " 已被其他程序占用。原快捷键仍保留，请换一个。");
        }

        definition = updated;
    }

    public void Dispose() => window.Dispose();

    private static HotkeyDefinition Parse(string hotkey)
    {
        string[] parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidOperationException("快捷键应写成 Ctrl+Alt+A 这样的组合。 ");
        uint modifiers = 0;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" => ModWin,
                _ => throw new InvalidOperationException("不认识快捷键修饰键：" + parts[index]),
            };
        }

        if (modifiers == 0) throw new InvalidOperationException("快捷键至少要有 Ctrl、Alt、Shift 或 Win 中的一项。");
        string keyName = parts[^1];
        if (!Enum.TryParse(keyName, true, out Keys key) || key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey)
        {
            throw new InvalidOperationException("不认识快捷键中的按键：" + keyName);
        }

        return new HotkeyDefinition(modifiers, key);
    }

    private readonly record struct HotkeyDefinition(uint Modifiers, Keys Key);

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public HotkeyWindow() => CreateHandle(new CreateParams());

        public event EventHandler? HotkeyPressed;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                UnregisterHotKey(Handle, 1);
                DestroyHandle();
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam == 1)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return;
            }

            base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
