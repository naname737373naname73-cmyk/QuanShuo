using System.Reflection;
using System.Runtime.InteropServices;
using QuanShuo.Core.Models;
using QuanShuo.Native.Recording;

internal static class Program
{
    private const int Down = 0x100, Up = 0x101, Character = 0x102;
    private const long Repeat = 1L << 30;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        // Synthetic windows and callbacks only: no coordinator, microphone, clipboard, user settings or network.
        // The transparent windows briefly take keyboard focus; run this suite on an interactive Windows desktop.
        try
        {
            Run("speech", (record, actions) => new SpeechWindow(record, actions, null, null));
            Run("card", (record, actions) => new RecordingCard(record, actions));
            Console.WriteLine($"PASS {checks} checks; no microphone or ASR started.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string name, Func<QuanShuoRecord, RecordingActions, RecordingSurface> create)
    {
        int toggles = 0;
        bool paused = false;
        RecordingSurface? current = null;
        var record = new QuanShuoRecord
        {
            Id = "keyboard-audit", Timestamp = DateTimeOffset.Now, AppName = "audit", WindowTitle = "audit",
            ImagePath = string.Empty, MarkdownPath = string.Empty,
            Text = "测试文字", Status = TranscriptionStatus.Recording,
        };
        var actions = new RecordingActions(
            () => Task.CompletedTask,
            () => { toggles++; paused = !paused; current!.SetPaused(paused); },
            (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        using var surface = create(record, actions);
        current = surface;
        surface.Opacity = 0;
        surface.Show();
        Application.DoEvents();
        var pause = (Control)typeof(RecordingSurface).GetProperty("pauseButton", Hidden)!.GetValue(surface)!;
        var text = (LiveTextBox)typeof(RecordingSurface).GetProperty("transcript", Hidden)!.GetValue(surface)!;
        text.ReplaceAll("测试文字");
        Check(name + ": starts on pause controls", pause.Focused && !text.Focused);

        Key(pause, Up, Repeat); // Space originally pressed in screenshot overlay, released in this window.
        Check(name + ": opening release does not pause", toggles == 0);
        Key(pause, Down, Repeat); // Same held opening key can also repeat after the card appears.
        Key(pause, Character, 0);
        Key(pause, Up, Repeat);
        Check(name + ": opening repeat does not pause", toggles == 0);

        string before = text.Text;
        Key(pause, Down, 0);
        Key(pause, Character, 0);
        Key(pause, Down, Repeat);
        Key(pause, Character, Repeat);
        Key(pause, Up, Repeat);
        Check(name + ": press and hold pauses once", toggles == 1 && paused && text.Text == before);
        Key(pause, Down, 0);
        Key(pause, Up, Repeat);
        Check(name + ": next press resumes once", toggles == 2 && !paused);

        surface.SelectNextControl(pause, true, true, true, true);
        Check(name + ": Tab reaches transcript", text.Focused);
        text.SelectionStart = text.TextLength;
        Key(text, Down, 0);
        Key(text, Character, 0);
        Key(text, Up, Repeat);
        Check(name + ": editing keeps space", toggles == 2 && text.Text == before + " ");
        typeof(LiveTextBox).GetField("composing", Hidden)!.SetValue(text, true);
        Check(name + ": IME space reaches editor", !Preview(surface, Down, 0) && toggles == 2);
        typeof(LiveTextBox).GetField("composing", Hidden)!.SetValue(text, false);

        pause.Focus();
        typeof(RecordingSurface).GetField("saving", Hidden)!.SetValue(surface, true);
        Key(pause, Down, 0);
        Key(pause, Up, Repeat);
        Check(name + ": finalizing does not pause", toggles == 2);
        typeof(RecordingSurface).GetField("saving", Hidden)!.SetValue(surface, false);

        // Finish while a shortcut is held: its later release must not activate another action.
        Key(pause, Down, 0);
        surface.SetFinished(record with { Text = text.Text, Status = TranscriptionStatus.Completed });
        Check(name + ": release after finishing is consumed", Preview(surface, Up, Repeat));
        Check(name + ": finished recording does not toggle", !Preview(surface, Down, 0) && toggles == 3);
        VerifyFinishKey(name, create, Keys.Enter);
        VerifyFinishKey(name, create, Keys.Escape);
    }

    private static void VerifyFinishKey(string name, Func<QuanShuoRecord, RecordingActions, RecordingSurface> create, Keys key)
    {
        int finishes = 0, pauses = 0;
        RecordingSurface? current = null;
        var record = new QuanShuoRecord
        {
            Id = "finish-key-audit", Timestamp = DateTimeOffset.Now, AppName = "audit", WindowTitle = "audit",
            ImagePath = string.Empty, MarkdownPath = string.Empty, Text = "保留的文字", Status = TranscriptionStatus.Recording,
        };
        var actions = new RecordingActions(
            () =>
            {
                finishes++;
                // Simulate the coordinator's finished notification without a real provider.
                // Incomplete keeps SpeechWindow from pasting anything into another application.
                current!.SetFinished(record with { Status = TranscriptionStatus.Incomplete });
                return Task.CompletedTask;
            },
            () => pauses++, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        using var surface = create(record, actions);
        current = surface;
        surface.Opacity = 0;
        surface.Show();
        Application.DoEvents();
        var pause = (Control)typeof(RecordingSurface).GetProperty("pauseButton", Hidden)!.GetValue(surface)!;
        SendMessage(pause.Handle, Down, (IntPtr)key, (IntPtr)1);
        SendMessage(pause.Handle, Up, (IntPtr)key, (IntPtr)(Repeat | 1));
        Check($"{name}: {key} still finishes recording without pausing", finishes == 1 && pauses == 0);
    }

    private static bool Preview(RecordingSurface surface, int message, long flags)
    {
        var packet = Message.Create(surface.Handle, message, (IntPtr)Keys.Space, (IntPtr)(flags | 1));
        object[] args = [packet];
        return (bool)typeof(RecordingSurface).GetMethod("ProcessKeyPreview", Hidden)!.Invoke(surface, args)!;
    }
    private static void Key(Control target, int message, long flags) => SendMessage(target.Handle, message, (IntPtr)Keys.Space, (IntPtr)(flags | 1));
    private static void Check(string name, bool passed)
    {
        if (!passed) throw new InvalidOperationException("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
