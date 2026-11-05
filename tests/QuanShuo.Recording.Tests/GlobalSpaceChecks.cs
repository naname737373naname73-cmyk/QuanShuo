using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Recording;
using QuanShuo.Native.Shell;
using QuanShuo.Native.Transcription;

internal static class GlobalSpaceChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const ushort Space = 0x20;
    private static readonly List<object> Results = [];
    private static int failures;
    private static IntPtr targetHandle;
    private static IntPtr targetInput;
    private static string targetFile = string.Empty;

    public static int RunTarget(string statePath)
    {
        using var window = new Form { Text = "圈说键盘回归 · 隔离输入目标", Size = new Size(440, 130), StartPosition = FormStartPosition.Manual, Location = new Point(30, 30) };
        using var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ImeMode = ImeMode.Disable, AccessibleName = "测试空格接收器" };
        window.Controls.Add(input);
        void Save() => File.WriteAllText(statePath, JsonSerializer.Serialize(new { handle = window.Handle.ToInt64(), inputHandle = input.Handle.ToInt64(), text = input.Text }));
        window.Shown += (_, _) => { input.Focus(); Save(); };
        Application.Run(window);
        return 0;
    }

    public static int Run(bool baseline)
    {
        string root = Path.Combine(Path.GetTempPath(), "quanshuo-global-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        targetFile = Path.Combine(root, "target.json");
        using var context = new ApplicationContext();
        Process? target = null;
        using var start = new System.Windows.Forms.Timer { Interval = 40 };
        start.Tick += async (_, _) =>
        {
            start.Stop();
            try
            {
                target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "--keyboard-target", targetFile } });
                for (int n = 0; n < 100 && !File.Exists(targetFile); n++) await Task.Delay(50);
                using (JsonDocument data = ReadTarget())
                {
                    targetHandle = (IntPtr)data.RootElement.GetProperty("handle").GetInt64();
                    targetInput = (IntPtr)data.RootElement.GetProperty("inputHandle").GetInt64();
                }
                foreach (string skin in baseline ? new[] { "starlake" } : new[] { "starlake", "crabapple" })
                {
                    NativeTheme.UseSkin(skin);
                    await Scenario(skin + "/speech", (r, a) => new SpeechWindow(r, a, null, null), baseline);
                    if (!baseline) await Scenario(skin + "/card", (r, a) => new RecordingCard(r, a), false);
                }
                if (!baseline) { await LifecycleAndConflict(); await CoordinatorStopping(root); }
            }
            catch (Exception e) { Check("test infrastructure", false, e.ToString()); }
            finally
            {
                if (target is not null && !target.HasExited)
                {
                    target.CloseMainWindow();
                    if (!target.WaitForExit(2000)) { target.Kill(entireProcessTree: true); target.WaitForExit(2000); }
                }
                target?.Dispose();
                context.ExitThread();
            }
        };
        start.Start(); Application.Run(context);
        string output = Path.Combine(Environment.CurrentDirectory, baseline ? "global-space-before.json" : "global-space-after.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { checks = Results, failures }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Global Space: {Results.Count - failures}/{Results.Count} passed. {output}");
        try { Directory.Delete(root, true); } catch (IOException) { }
        return failures == 0 ? 0 : 1;
    }

    private static async Task Scenario(string name, Func<QuanShuoRecord, RecordingActions, RecordingSurface> create, bool baseline)
    {
        var record = new QuanShuoRecord { Id = "global-space-audit", Timestamp = DateTimeOffset.Now, AppName = "audit", WindowTitle = "audit", ImagePath = "", MarkdownPath = "", Text = "文字", Status = TranscriptionStatus.Recording };
        int toggles = 0; bool paused = false;
        RecordingSurface? current = null;
        var actions = new RecordingActions(() => Task.CompletedTask, () => { toggles++; paused = !paused; current!.SetPaused(paused); }, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        await Foreground(targetHandle);
        using var surface = create(record, actions); current = surface;
        surface.Show(); await Task.Delay(200);
        var editor = (LiveTextBox)typeof(RecordingSurface).GetProperty("transcript", Hidden)!.GetValue(surface)!;
        var pause = (Control)typeof(RecordingSurface).GetProperty("pauseButton", Hidden)!.GetValue(surface)!;
        IntPtr openingForeground = GetForegroundWindow();
        Check(name + ": opening foreground belongs to test", openingForeground == surface.Handle || openingForeground == targetHandle);
        await Press(openingForeground);
        Check(name + ": first Space without clicking pauses exactly once", toggles == 1 && paused, $"toggles={toggles}");

        await Foreground(targetHandle);
        int previous = toggles; int beforeText = TargetText().Length;
        await Press(targetHandle);
        Check(name + ": external app foreground Space toggles once", toggles == previous + 1, $"before={previous}, after={toggles}");
        Check(name + ": global Space is not typed into external app", TargetText().Length == beforeText);
        if (baseline) { surface.SetFinished(record with { Status = TranscriptionStatus.Incomplete }); surface.Close(); await Task.Delay(100); return; }

        previous = toggles;
        await Press(targetHandle);
        Check(name + ": next external Space resumes once", toggles == previous + 1);
        previous = toggles;
        RequireForeground(targetHandle); Send(Space, false);
        try { for (int n = 0; n < 5; n++) { await Task.Delay(70); RequireForeground(targetHandle); Send(Space, false); } }
        finally { Send(Space, true); }
        await Task.Delay(180);
        Check(name + ": held Space only toggles once", toggles == previous + 1, $"before={previous}, after={toggles}");

        await Foreground(surface.Handle); editor.Focus(); await Task.Delay(120);
        string textBefore = editor.Text; previous = toggles; editor.SelectionStart = editor.TextLength;
        await Press(surface.Handle);
        Check(name + ": editing keeps literal Space without toggling", editor.Text == textBefore + " " && toggles == previous);
        typeof(LiveTextBox).GetField("composing", Hidden)!.SetValue(editor, true);
        await Press(surface.Handle);
        Check(name + ": IME composition Space does not pause", toggles == previous);
        typeof(LiveTextBox).GetField("composing", Hidden)!.SetValue(editor, false);
        await Foreground(targetHandle); previous = toggles;
        await Press(targetHandle);
        Check(name + ": leaving editor restores external Space", toggles == previous + 1);
        previous = toggles; surface.SetPaused(true, automatic: true); paused = true;
        await Press(targetHandle);
        Check(name + ": external Space resumes idle pause", toggles == previous + 1 && !paused);

        surface.SetFinished(record with { Status = TranscriptionStatus.Incomplete }); await Task.Delay(100);
        beforeText = TargetText().Length; previous = toggles;
        await Press(targetHandle);
        Check(name + ": finished window releases ordinary Space", toggles == previous && TargetText().Length == beforeText + 1, $"beforeLength={beforeText}, afterLength={TargetText().Length}, registered={Registered(surface)}");
        surface.Close(); await Task.Delay(100);
        beforeText = TargetText().Length; await Press(targetHandle);
        Check(name + ": closed window releases ordinary Space", TargetText().Length == beforeText + 1, $"beforeLength={beforeText}, afterLength={TargetText().Length}");

        await OpeningHeld(name, create, record);
    }

    private static async Task OpeningHeld(string name, Func<QuanShuoRecord, RecordingActions, RecordingSurface> create, QuanShuoRecord record)
    {
        int toggles = 0;
        var actions = new RecordingActions(() => Task.CompletedTask, () => toggles++, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        await Foreground(targetHandle); RequireForeground(targetHandle); Send(Space, false);
        using var surface = create(record, actions);
        try
        {
            surface.Show(); await Task.Delay(120);
            IntPtr focus = GetForegroundWindow();
            if (focus != targetHandle && focus != surface.Handle) throw new InvalidOperationException("Focus left test during opening hold");
            for (int n = 0; n < 3; n++) { Send(Space, false); await Task.Delay(50); }
        }
        finally { Send(Space, true); }
        await Task.Delay(180);
        Check(name + ": Space held while opening does not pause", toggles == 0);
        await Foreground(targetHandle); await Press(targetHandle);
        Check(name + ": fresh Space after opening hold works", toggles == 1);
        surface.SetFinished(record with { Status = TranscriptionStatus.Incomplete }); surface.Close(); await Task.Delay(100);
    }

    private static async Task LifecycleAndConflict()
    {
        var record = new QuanShuoRecord { Id = "global-space-lifecycle", Timestamp = DateTimeOffset.Now, AppName = "audit", WindowTitle = "audit", ImagePath = "", MarkdownPath = "", Text = "保留文字", Status = TranscriptionStatus.Recording };
        int toggles = 0;
        var actions = new RecordingActions(() => Task.CompletedTask, () => toggles++, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        using var blocker = new BlockingHotkey();
        using var first = new SpeechWindow(record, actions, null, null);
        first.Show(); await Task.Delay(150);
        string status = (string)typeof(RecordingSurface).GetProperty("statusText", Hidden)!.GetValue(first)!;
        Check("conflict: occupied Space is reported and not stolen", !Registered(first) && status.Contains("占用", StringComparison.Ordinal), status);
        await Foreground(targetHandle); await Press(targetHandle);
        Check("conflict: existing hotkey owner receives Space", blocker.Presses == 1 && toggles == 0);
        var pause = (Control)typeof(RecordingSurface).GetProperty("pauseButton", Hidden)!.GetValue(first)!;
        typeof(Control).GetMethod("OnClick", Hidden)!.Invoke(pause, [EventArgs.Empty]);
        Check("conflict: pause button remains usable", toggles == 1);
        blocker.Release(); await Task.Delay(1300); await Press(targetHandle);
        Check("conflict: shortcut recovers when previous owner releases", Registered(first) && toggles == 2);

        first.Hide(); await Task.Delay(100);
        int beforeText = TargetText().Length;
        await Press(targetHandle);
        Check("lifecycle: hiding a recording releases Space", !Registered(first) && TargetText().Length == beforeText + 1);
        first.Show(); await Task.Delay(150); await Foreground(targetHandle); await Press(targetHandle);
        Check("lifecycle: showing the recording restores Space", Registered(first) && toggles == 3);

        int secondToggles = 0;
        var secondActions = new RecordingActions(() => Task.CompletedTask, () => secondToggles++, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        using var second = new SpeechWindow(record with { Id = "second" }, secondActions, null, null);
        second.Show(); await Task.Delay(150); await Foreground(targetHandle); await Press(targetHandle);
        Check("multiple surfaces: only one global owner toggles", Registered(first) && !Registered(second) && toggles == 4 && secondToggles == 0);
        first.SetFinished(record with { Status = TranscriptionStatus.Incomplete }); first.Close(); await Task.Delay(1300); await Press(targetHandle);
        Check("multiple surfaces: waiting owner acquires after release", Registered(second) && secondToggles == 1);
        second.NotifyRecordingStopping(); await Task.Delay(100);
        beforeText = TargetText().Length; await Press(targetHandle);
        Check("finalizing: Space releases before final transcription completes", !Registered(second) && secondToggles == 1 && TargetText().Length == beforeText + 1);
        await Foreground(second.Handle); await Foreground(targetHandle);
        Check("finalizing: focus changes cannot reacquire Space", !Registered(second));
        second.SetFinished(record with { Status = TranscriptionStatus.Incomplete }); second.Close(); await Task.Delay(100);
        beforeText = TargetText().Length; await Press(targetHandle);
        Check("lifecycle: no global Space remains after all surfaces close", TargetText().Length == beforeText + 1);
    }

    private static async Task CoordinatorStopping(string root)
    {
        var store = new RecordStore(Path.Combine(root, "coordinator"));
        await using var coordinator = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory });
        var record = new QuanShuoRecord { Id = "pending-start", Timestamp = DateTimeOffset.Now, AppName = "audit", WindowTitle = "audit", ImagePath = "", MarkdownPath = "", Text = "", Status = TranscriptionStatus.Recording };
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RecordingCoordinator).GetField("current", Hidden)!.SetValue(coordinator, record);
        typeof(RecordingCoordinator).GetField("startup", Hidden)!.SetValue(coordinator, startup);
        string? stoppedId = null;
        coordinator.Stopping += (_, id) => stoppedId = id;
        using var cancel = new CancellationTokenSource();
        try
        {
            Task finishing = coordinator.FinishAsync(cancel.Token);
            Check("coordinator: stopping notification precedes pending model startup", stoppedId == record.Id && !finishing.IsCompleted);
            cancel.Cancel();
            try { await finishing; } catch (OperationCanceledException) { }
        }
        finally
        {
            typeof(RecordingCoordinator).GetField("current", Hidden)!.SetValue(coordinator, null);
            typeof(RecordingCoordinator).GetField("startup", Hidden)!.SetValue(coordinator, null);
        }
    }

    private sealed class BlockingHotkey : NativeWindow, IDisposable
    {
        private bool owned;
        public int Presses { get; private set; }
        public BlockingHotkey()
        {
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });
            owned = RegisterHotKey(Handle, 20001, 0x4000, Space);
            if (!owned) throw new InvalidOperationException("Cannot establish isolated hotkey conflict fixture");
        }
        public void Release() { if (owned) UnregisterHotKey(Handle, 20001); owned = false; }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312) { Presses++; return; }
            base.WndProc(ref message);
        }
        public void Dispose() { Release(); if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    private static JsonDocument ReadTarget()
    {
        for (int n = 0; n < 10; n++)
        {
            try { return JsonDocument.Parse(File.ReadAllText(targetFile)); }
            catch (Exception e) when (e is IOException or JsonException) { Thread.Sleep(10); }
        }
        throw new IOException("Cannot read isolated keyboard target state");
    }
    private static string TargetText()
    {
        int length = (int)SendMessage(targetInput, 0x000E, IntPtr.Zero, IntPtr.Zero);
        var buffer = new StringBuilder(length + 1);
        SendMessageText(targetInput, 0x000D, (IntPtr)buffer.Capacity, buffer);
        return buffer.ToString();
    }
    private static bool Registered(RecordingSurface surface)
    {
        object? service = typeof(RecordingSurface).GetField("pauseShortcut", Hidden)?.GetValue(surface);
        return service is not null && (bool)service.GetType().GetProperty("Registered")!.GetValue(service)!;
    }
    private static async Task Foreground(IntPtr handle)
    {
        SetForegroundWindow(handle);
        for (int n = 0; n < 10 && GetForegroundWindow() != handle; n++) { await Task.Delay(50); SetForegroundWindow(handle); }
        RequireForeground(handle); await Task.Delay(100);
    }
    private static void RequireForeground(IntPtr handle)
    {
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("Test input stopped: expected isolated target is no longer foreground.");
    }
    private static async Task Press(IntPtr handle)
    {
        RequireForeground(handle); Send(Space, false); await Task.Delay(35); Send(Space, true); await Task.Delay(180);
    }
    private static void Check(string name, bool passed, string detail = "")
    {
        Results.Add(new { name, passed, detail }); if (!passed) failures++;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} {detail}");
    }
    private static void Send(ushort key, bool up)
    {
        var inputs = new[] { new INPUT { type = 1, keyboard = new KEYBDINPUT { wVk = key, dwFlags = up ? 2u : 0 } } };
        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1) throw new InvalidOperationException("SendInput refused isolated test input");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr extraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageText(IntPtr handle, int message, IntPtr wParam, StringBuilder text);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
