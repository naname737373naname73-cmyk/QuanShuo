using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Recording;
using QuanShuo.Native.Transcription;
using QuanShuo.Native.Shell;
using QuanShuo.Native.Capture;
using QuanShuo.Native.Input;

// Synthetic records/providers only. No microphone, user settings, API key or external service.
internal static class RecordingPersistenceAudit
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("model-switch-start-rejection-finishes-surface-and-keeps-manual-text", () => RunSta(() =>
        {
            var f = Fixture.Create(root).GetAwaiter().GetResult();
            Set(f.Coordinator, "current", null);
            Set(f.Coordinator, "provider", null);
            object gate = typeof(RecordingCoordinator).GetField("modelActivity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Coordinator)!;
            using var switching = (IDisposable)gate.GetType().GetMethod("EnterSwitch")!.Invoke(gate, null)!;
            // Avoid the tray constructor: it loads real settings and registers application hotkeys.
            var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
            Set(context, "store", f.Store);
            Set(context, "recordingCoordinator", f.Coordinator);
            var actions = (RecordingActions)typeof(TrayApplicationContext).GetMethod("BuildRecordingActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
            using var surface = new AuditSurface(f.Record, actions) { SuppressDisplay = true };
            surface.Edit("切换期间手动输入的文字不能丢失。");
            var start = (Task)typeof(TrayApplicationContext).GetMethod("StartSurfaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, [surface, f.Record])!;
            start.GetAwaiter().GetResult();
            var saved = MarkdownRecordFile.Parse(f.Record.MarkdownPath, File.ReadAllText(f.Record.MarkdownPath));
            Require(f.Coordinator.Current is null, "rejected start created an active recording");
            Require(surface.RecordStatus == TranscriptionStatus.Incomplete && surface.FinishAcknowledged && !surface.RequestClose(), "rejected start left a listening surface or blocked close");
            Require(saved.Status == TranscriptionStatus.Incomplete && saved.Text == surface.VisibleText && saved.Text == "切换期间手动输入的文字不能丢失。", "rejected start lost manual text or claimed success");
            Require(surface.ErrorVisible && f.Coordinator.TextMerger is null && f.Coordinator.FinalTextSnapshot is null, "rejected start kept stale UI callbacks or hid the failure");
        }));
        yield return ("finish-missing-markdown-keeps-wav-and-reports-failure", async () =>
        {
            var f = await Fixture.Create(root); bool finished = false;
            f.Coordinator.Finished += (_, _) => finished = true;
            File.Delete(f.Record.MarkdownPath);
            await ExpectIOException(() => f.Coordinator.FinishAsync());
            Require(!finished && File.Exists(f.Record.AudioPath), "missing Markdown was treated as saved or WAV deleted");
        }
        );
        yield return ("retry-missing-markdown-does-not-start-provider", async () =>
        {
            var f = await Fixture.Create(root); int created = 0;
            Set(f.Coordinator, "current", null); Set(f.Coordinator, "provider", null);
            Property(f.Coordinator, "ProviderFactory", (Func<ITranscriptionProvider>)(() => { created++; return new FakeProvider(); }));
            File.Delete(f.Record.MarkdownPath);
            await ExpectIOException(() => f.Coordinator.RetryAsync(f.Record));
            Require(created == 0 && File.Exists(f.Record.AudioPath), "retry ran or deleted WAV after record was removed");
        }
        );
        yield return ("retry-markdown-disappears-before-final-save-keeps-wav", async () =>
        {
            var f = await Fixture.Create(root); Set(f.Coordinator, "current", null); Set(f.Coordinator, "provider", null);
            Property(f.Coordinator, "ProviderFactory", (Func<ITranscriptionProvider>)(() => new FakeProvider { OnFinish = () => File.Delete(f.Record.MarkdownPath) }));
            await ExpectIOException(() => f.Coordinator.RetryAsync(f.Record));
            Require(File.Exists(f.Record.AudioPath), "final retry save failure deleted WAV");
        }
        );
        yield return ("microphone-failure-cannot-complete-or-delete-wav", async () =>
        {
            var f = await Fixture.Create(root); QuanShuoRecord? completed = null;
            f.Coordinator.Finished += (_, v) => completed = v;
            Invoke(f.Coordinator, "OnRecorderFailed", null, "Synthetic microphone disconnected");
            await f.Coordinator.FinishAsync();
            Require(completed?.Status == TranscriptionStatus.Incomplete && File.Exists(f.Record.AudioPath), "microphone failure became Completed or deleted WAV");
        }
        );
        yield return ("queued-final-callback-is-merged-before-persistence", async () =>
        {
            var f = await Fixture.Create(root); var context = new QueuedContext();
            Set(f.Coordinator, "uiContext", context); Set(f.Coordinator, "latestText", string.Empty);
            var provider = new FakeProvider { OnFinish = () => Invoke(f.Coordinator, "OnTranscriptReceived", null, new TranscriptUpdate("UI 队列里的最终文本。", true)) };
            Set(f.Coordinator, "provider", provider);
            Task finish = f.Coordinator.FinishAsync(); var watch = Stopwatch.StartNew();
            while (!finish.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(3)) { context.Drain(); await Task.Delay(5); }
            await finish.WaitAsync(TimeSpan.FromSeconds(1)); context.Drain();
            var saved = MarkdownRecordFile.Parse(f.Record.MarkdownPath, await File.ReadAllTextAsync(f.Record.MarkdownPath));
            Require(saved.Text == "UI 队列里的最终文本。", "network finished before queued final callback persisted");
        }
        );
        yield return ("deferred-ime-final-text-is-saved-before-wav-cleanup", () => RunSta(() =>
        {
            var f = Fixture.Create(root).GetAwaiter().GetResult(); using var box = new LiveTextBox();
            Set(box, "composing", true);
            Set(f.Coordinator, "latestText", box.Merge("输入法期间的最终识别。", true));
            Property(f.Coordinator, "FinalTextSnapshot", (Func<string>)(() => { box.FlushDeferred(); return box.Text; }), optional: true);
            QuanShuoRecord? completed = null; f.Coordinator.Finished += (_, v) => completed = v;
            f.Coordinator.FinishAsync().GetAwaiter().GetResult();
            var saved = MarkdownRecordFile.Parse(f.Record.MarkdownPath, File.ReadAllText(f.Record.MarkdownPath));
            Require(completed?.Text == "输入法期间的最终识别。" && saved.Text == completed.Text, "deferred final text missed final save and can trigger empty-record discard");
        }));
        yield return ("audio-drain-timeout-cancels-send-before-dispose", async () =>
        {
            var f = await Fixture.Create(root); using var cancellation = new CancellationTokenSource(); var slow = new FakeProvider { BlockSend = true };
            Set(f.Coordinator, "provider", slow); Set(f.Coordinator, "sendCancellation", cancellation, optional: true);
            Property(f.Coordinator, "AudioDrainTimeout", TimeSpan.FromMilliseconds(30), optional: true);
            Task sending = slow.SendAudioAsync(new byte[3200], cancellation.Token); Set(f.Coordinator, "sendTask", sending);
            QuanShuoRecord? completed = null; f.Coordinator.Finished += (_, v) => completed = v;
            try
            {
                await f.Coordinator.FinishAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Require(completed?.Status == TranscriptionStatus.Incomplete && File.Exists(f.Record.AudioPath), "slow send did not retain incomplete recording");
                Require(slow.Disposed && !slow.DisposedDuringSend && slow.Finishes == 0, "provider disposed during send or finalized a partial transfer");
            }
            finally { if (!slow.Disposed) cancellation.Cancel(); try { await sending; } catch (OperationCanceledException) { } }
        }
        );
        yield return ("explicit-save-waits-for-edits-made-during-write", () => RunSta(() =>
        {
            var f=Fixture.Create(root).GetAwaiter().GetResult();
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saved=new List<string>();
            async Task Save(QuanShuoRecord _,string text)
            {
                saved.Add(text);
                if(saved.Count==1) await release.Task;
            }
            using var surface=new AuditSurface(f.Record,new RecordingActions(()=>Task.CompletedTask,()=>{},Save,r=>Task.FromResult(r),_=>{}));
            surface.Edit("已保存"); surface.SetFinished(f.Record with { Text="已保存",Status=TranscriptionStatus.Completed });
            surface.Edit("第一次修改");
            Task writing=surface.SaveEditedTextAsync();
            surface.Edit("写盘期间的最后一次修改");
            release.SetResult();
            writing.GetAwaiter().GetResult();
            Require(saved.Last()=="写盘期间的最后一次修改", "explicit save returned with new edits still only in a debounce timer");
        }));
        yield return ("close-after-save-failure-keeps-visible-user-text", () => RunSta(() =>
        {
            var f = Fixture.Create(root).GetAwaiter().GetResult();
            using var surface = new AuditSurface(f.Record, new RecordingActions(() => Task.CompletedTask, () => { }, (_, _) => Task.FromException(new IOException("Synthetic save denied")), r => Task.FromResult(r), _ => { }));
            surface.Edit("旧文字"); surface.SetFinished(f.Record with { Text = "旧文字", Status = TranscriptionStatus.Completed }); surface.Edit("不能丢失的手动文字");
            bool cancelled = surface.RequestClose();
            Require(cancelled && surface.ErrorVisible && surface.VisibleText == "不能丢失的手动文字", "failed autosave allowed closing or hid the error");
        }));
        yield return ("capture-preparation-ignores-reentry-without-taking-screenshot", async () =>
        {
            var f=await Fixture.Create(root);
            using var controller=new CaptureController(new ScreenCaptureService(new ForegroundContextService()),f.Store);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int entered=0,errors=0;
            controller.BeforeCaptureAsync=async ()=>
            {
                Interlocked.Increment(ref entered);
                await release.Task;
                // Deliberately fail before CaptureNow: no actual desktop screenshot is taken.
                throw new IOException("Synthetic preparation failure");
            };
            controller.CaptureFailed += (_,_)=> { if(Interlocked.Increment(ref errors)==Volatile.Read(ref entered)) handled.TrySetResult(); };
            controller.BeginCapture(); controller.BeginCapture();
            int concurrent=Volatile.Read(ref entered);
            release.SetResult();
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(concurrent==1,"capture hotkey reentered while final transcript/save was pending");
        });
        yield return ("second-close-cannot-destroy-a-finishing-surface", () => RunSta(() =>
        {
            var f = Fixture.Create(root).GetAwaiter().GetResult(); var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var surface = new AuditSurface(f.Record, new RecordingActions(() => never.Task, () => { }, (_, _) => Task.CompletedTask, r => Task.FromResult(r), _ => { }));
            Require(surface.RequestClose() && surface.RequestClose(), "second close bypassed active recording/finish protection");
        }));
    }

    private sealed record Fixture(RecordingCoordinator Coordinator, RecordStore Store, QuanShuoRecord Record)
    {
        internal static async Task<Fixture> Create(string root)
        {
            string dir = Path.Combine(root, "persistence-audit-" + Guid.NewGuid().ToString("N")); var store = new RecordStore(dir);
            var c = new RecordingCoordinator(store, new DpapiSecretStore(Path.Combine(dir, "never-created.dpapi")), new AppSettings { SpeechProvider = "local", DataDirectory = dir, DeleteAudioAfterSuccess = true });
            var r = await store.CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "Audit", WindowTitle = "Synthetic" }, TranscriptionStatus.Recording);
            r = r with { AudioPath = store.GetAudioPath(r), Text = "原来的手动文字" }; await File.WriteAllBytesAsync(r.AudioPath, Wave());
            Set(c, "current", r); Set(c, "provider", new FakeProvider()); Set(c, "latestText", r.Text);
            return new(c, store, r);
        }
    }
    private sealed class FakeProvider : ITranscriptionProvider
    {
        public event EventHandler<TranscriptUpdate>? TranscriptReceived;
        internal Action? OnFinish { get; init; }
        internal bool BlockSend { get; init; }
        internal bool Sending { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool DisposedDuringSend { get; private set; }
        internal int Finishes { get; private set; }
        public Task StartAsync(TranscriptionOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken token)
        {
            Sending = true; try { if (BlockSend) await Task.Delay(Timeout.InfiniteTimeSpan, token); } finally { Sending = false; }
        }
        public Task FinishAsync(CancellationToken token) { Finishes++; OnFinish?.Invoke(); TranscriptReceived?.Invoke(this, new TranscriptUpdate("完整识别结果。", true)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposedDuringSend = Sending; Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue((d, state));
        internal void Drain() { while (queue.TryDequeue(out var work)) work.Callback(work.State); }
    }
    private sealed class AuditSurface(QuanShuoRecord record, RecordingActions actions) : RecordingSurface(record, actions)
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool SuppressDisplay { get; init; }
        protected override void SetVisibleCore(bool value) { if (!SuppressDisplay) base.SetVisibleCore(value); }
        protected override string ListeningStatus => "listening";
        protected override string PausedStatus => "paused";
        protected override void ApplyLayout(float scale) { }
        protected override void OnRecordChanged() { }
        protected override CrabapplePrint CreatePrint() => throw new NotSupportedException();
        internal void Edit(string text) => transcript.Text = text;
        internal string VisibleText => transcript.Text;
        internal bool ErrorVisible => hasError;
        internal TranscriptionStatus RecordStatus => base.record.Status;
        internal bool FinishAcknowledged => WhenFinished.IsCompletedSuccessfully;
        internal bool RequestClose() { var e = new FormClosingEventArgs(CloseReason.UserClosing, false); OnFormClosing(e); return e.Cancel; }
    }
    private static Task RunSta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { WindowsFormsSynchronizationContext.AutoInstall = false; SynchronizationContext.SetSynchronizationContext(null); try { action(); result.SetResult(); } catch (Exception ex) { result.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    private static async Task ExpectIOException(Func<Task> action) { try { await action(); } catch (IOException) { return; } throw new Exception("expected a save failure"); }
    private static void Require(bool ok, string error) { if (!ok) throw new Exception(error); }
    private static void Set(object value, string name, object? fieldValue, bool optional = false) { var f = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic); if (f is null && optional) return; f!.SetValue(value, fieldValue); }
    private static void Property(object value, string name, object? fieldValue, bool optional = false) { var p = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (p is null && optional) return; p!.SetValue(value, fieldValue); }
    private static void Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static byte[] Wave()
    {
        using var s = new MemoryStream(); using var w = new BinaryWriter(s); w.Write("RIFF"u8); w.Write(36 + 3200); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(3200); w.Write(new byte[3200]); return s.ToArray();
    }
}
