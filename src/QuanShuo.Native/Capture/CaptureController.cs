using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using SelectionMode = QuanShuo.Core.Models.SelectionMode;

namespace QuanShuo.Native.Capture;

public sealed class CaptureController : IDisposable
{
    private readonly ScreenCaptureService captureService;
    private readonly RecordStore store;
    private OverlayForm? overlay;
    private bool disposed;
    private bool preparing;

    public CaptureController(ScreenCaptureService captureService, RecordStore store)
    {
        this.captureService = captureService;
        this.store = store;
    }

    public event EventHandler<QuanShuoRecord>? RecordCreated;
    public event EventHandler<QuanShuoRecord>? RecordingRequested;
    public event EventHandler<string>? CaptureFailed;
    public Func<Task>? BeforeCaptureAsync { get; set; }
    public SelectionMode LastSelectionMode { get; set; } = SelectionMode.Rectangle;
    public Color FreeformFillColor { get; set; } = Color.White;
    public bool AutoSpeakAfterSelection { get; set; }
    public bool QuietMode { get; set; }

    public async void BeginCapture()
    {
        if (disposed || preparing || (overlay is not null && !overlay.IsDisposed))
        {
            return;
        }

        preparing = true;
        try
        {
            if (BeforeCaptureAsync is not null)
            {
                await BeforeCaptureAsync();
            }

            if (disposed) return;
            FrozenCapture frozen = captureService.CaptureNow();
            overlay = new OverlayForm(frozen, LastSelectionMode) { QuietMode = QuietMode };
            overlay.ConfirmAsync = SaveSelectionAsync;
            overlay.FormClosed += OverlayClosed;
            overlay.Show();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            CaptureFailed?.Invoke(this, exception.Message);
        }
        finally { preparing = false; }
    }

    private async Task<bool> SaveSelectionAsync(SelectionResult selection)
    {
        Diagnostics.AppTrace.Log("save-selection-start-" + selection.Bounds);
        LastSelectionMode = selection.Mode;
        bool speak = selection.Speak || AutoSpeakAfterSelection;
        QuanShuoRecord record = await store.CreateAsync(
            selection.Capture.Metadata,
            (path, cancellationToken) => SelectionImageWriter.SaveAsync(selection, path, FreeformFillColor, cancellationToken),
            string.Empty,
            speak ? TranscriptionStatus.Recording : TranscriptionStatus.NotRequested);
        RecordCreated?.Invoke(this, record);
        if (speak)
        {
            RecordingRequested?.Invoke(this, record);
        }

        return true;
    }

    private void OverlayClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is OverlayForm closed)
        {
            closed.FormClosed -= OverlayClosed;
            if (ReferenceEquals(overlay, closed)) overlay = null;
            closed.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (overlay is not null && !overlay.IsDisposed)
        {
            overlay.FormClosed -= OverlayClosed;
            overlay.Close();
            overlay.Dispose();
            overlay = null;
        }
    }
}
