using System.Runtime.InteropServices;

namespace QuanShuo.Native.Recording;

/// <summary>Only the live recording owns Space. No keyboard hook or background key log.</summary>
internal sealed class RecordingPauseHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly System.Windows.Forms.Timer retry = new() { Interval = 25 };
    private bool enabled;
    private bool disposed;
    private int registrationId;

    public RecordingPauseHotkey()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        retry.Tick += (_, _) => TryRegister();
    }

    public bool Registered { get; private set; }
    public bool RegistrationFailed { get; private set; }
    public bool WaitingForRelease { get; private set; }
    public event EventHandler? Pressed;
    public event EventHandler? AvailabilityChanged;

    public void SetEnabled(bool value)
    {
        if (disposed || enabled == value) return;
        enabled = value;
        if (value) TryRegister();
        else
        {
            retry.Stop();
            if (Registered) UnregisterHotKey(Handle, registrationId);
            Registered = false;
            WaitingForRelease = false;
            SetFailure(false);
        }
    }

    private void TryRegister()
    {
        if (!enabled || disposed || Registered) return;
        // A screenshot can be confirmed by the Space key still being held down.
        // Do not let its repeat become the first pause of the new recording.
        WaitingForRelease = (GetAsyncKeyState((int)Keys.Space) & 0x8000) != 0;
        if (WaitingForRelease)
        {
            retry.Interval = 25;
            retry.Start();
            return;
        }
        // Fresh IDs reject already queued notifications from a previous focus/session.
        registrationId = registrationId % 0xBFFF + 1;
        Registered = RegisterHotKey(Handle, registrationId, ModNoRepeat, (uint)Keys.Space);
        SetFailure(!Registered);
        if (Registered) retry.Stop();
        else
        {
            retry.Interval = 1000;
            retry.Start();
        }
    }

    private void SetFailure(bool value)
    {
        if (RegistrationFailed == value) return;
        RegistrationFailed = value;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
        {
            if (enabled && Registered && message.WParam.ToInt32() == registrationId)
                Pressed?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false);
        disposed = true;
        retry.Dispose();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
