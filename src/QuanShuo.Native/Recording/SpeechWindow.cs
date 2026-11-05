using QuanShuo.Core.Models;
using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Recording;

/// <summary>
/// 不用截图、单独说话的悬浮窗。文字边说边出，随时能改，
/// 按 Enter 贴回刚才所在的窗口。文字同时保存成一条历史记录，贴错了也不会丢。
/// </summary>
public sealed class SpeechWindow : RecordingSurface
{
    private const int DefaultWidth = 560;
    private const int DefaultHeight = 280;
    private const int MinimumWidth = 540;
    private const int MinimumHeight = 240;
    private const int MaxAppNameLength = 12;
    private const int CopiedCloseDelay = 1200;
    private const int ResizeEdge = 7;
    /// <summary>海棠：题签离窗口边 22，右边留出一栏印花，露出一整朵花。</summary>
    private const int SlipMargin = 22;
    private const int PrintColumn = 146;
    /// <summary>印花原图 1254×1254，从这里开始取，右边那一栏正好是一朵开着的花。</summary>
    private static readonly PointF PrintOrigin = new(236, 104);
    private const float PrintZoom = .55F;
    private readonly GlassButton sendButton = new();
    private readonly GlassButton copyButton = new();
    private readonly System.Windows.Forms.Timer targetTimer = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer closeTimer = new() { Interval = CopiedCloseDelay };
    private PasteTarget? target;
    private bool arranged;
    private bool delivering;
    private int targetRevision;
    private bool retryShown;

    public SpeechWindow(QuanShuoRecord initialRecord, RecordingActions actions, PasteTarget? pasteTarget, Rectangle? lastBounds)
        : base(initialRecord, actions)
    {
        target = pasteTarget;
        ApplySkin();
        Text = "圈说 · 说话";
        closeButton.AccessibleName = "关闭说话窗口";
        copyButton.Text = "复制";
        copyButton.Click += (_, _) => CopyOnly();
        sendButton.Click += (_, _) => Deliver();
        Controls.AddRange([closeButton, transcript, pauseButton, copyButton, sendButton, retryButton]);
        copyButton.TabIndex = 2;
        sendButton.TabIndex = 3;
        ActiveControl = pauseButton;
        scale = NativeTheme.ScaleAt(Cursor.Position);
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = lastBounds?.Size ?? new Size(D(DefaultWidth), D(DefaultHeight));
        Location = lastBounds?.Location ?? new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - D(64));
        ApplyLayout(scale);
        OnRecordChanged();
        targetTimer.Tick += (_, _) => TrackTarget();
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
    }

    /// <summary>正在听或正在收尾时为 true。</summary>
    public bool IsListening => recording;

    protected override string ListeningStatus => EditingTranscript ? "随时可以改" : NativeTheme.IsCrabapple ? "正在听 · 全局空格暂停" : "全局空格暂停";
    protected override string PausedStatus => automaticallyPaused ? "静音已暂停" : EditingTranscript ? "已暂停，改好点继续" : NativeTheme.IsCrabapple ? "已暂停 · 全局空格继续" : "全局空格继续";
    protected override CrabapplePrint CreatePrint() => new(PrintOrigin, PrintZoom);

    protected override void StyleButtons()
    {
        Style(sendButton, ButtonSkin.Seal, true);
        Style(copyButton, ButtonSkin.Ink);
    }

    /// <summary>结束录音，把文字贴回去。快捷键第二次按下时走这里。</summary>
    public async void Deliver()
    {
        if (delivering) return;
        TrackTarget();
        PasteTarget? deliveryTarget = target;
        int deliveryRevision = targetRevision;
        delivering = true;
        sendButton.Enabled = false;
        try
        {
            if (recording)
            {
                if (!await FinishRecordingAsync()) return;
                await WhenFinished;
                if (IsDisposed) return;
                // 识别失败时留在原地，让用户看到原因，可以重新转写
                if (record.Status == TranscriptionStatus.Incomplete) return;
            }

            string text = transcript.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                Close();
                return;
            }

            await SaveEditedTextAsync();
            if (IsDisposed) return;
            TrackTarget();
            if (deliveryRevision != targetRevision)
            {
                SetStatus("等待识别时目标窗口变了。文字已保留，请确认目标后再按 Enter。", true);
                return;
            }
            ClipboardService.CopyText(text);
            if (deliveryTarget is not { } destination)
            {
                ShowCopied();
                return;
            }

            if (await PasteService.PasteAsync(destination.Window, () =>
                {
                    if (IsDisposed) return false;
                    TrackTarget();
                    return targetRevision == deliveryRevision;
                }))
            {
                Close();
                return;
            }

            SetStatus("贴不回去了，文字已复制，到目标窗口按 Ctrl+V。", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            SetError("文字留在这里，没有发送：" + exception.Message);
        }
        finally
        {
            delivering = false;
            if (!IsDisposed) sendButton.Enabled = true;
        }
    }

    public override void SetFinished(QuanShuoRecord completed)
    {
        base.SetFinished(completed);
        // 什么都没说就结束：这条记录会被删掉，窗口留着也没有东西可改
        if (!delivering && string.IsNullOrWhiteSpace(transcript.Text) && completed.Status == TranscriptionStatus.Completed) Close();
    }

    protected override void OnFinishing() => pauseButton.Enabled = false;
    protected override void OnFinishFailed() => pauseButton.Enabled = true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        FocusRecordingControls();
        transcript.SelectionStart = transcript.TextLength;
        targetTimer.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Deliver();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (recording) _ = FinishRecordingAsync();
            else Close();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            FocusRecordingControls();
            NativeTheme.DragWindow(Handle);
        }
    }

    protected override int FrameHitTest(Point client)
    {
        int edge = D(ResizeEdge);
        bool left = client.X < edge, right = client.X >= ClientSize.Width - edge;
        bool top = client.Y < edge, bottom = client.Y >= ClientSize.Height - edge;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return 1;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (arranged) ArrangeControls();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintCard(e.Graphics);
        DrawStatus(e.Graphics);
    }

    protected override void OnErrorStateChanged()
    {
        if (arranged) ArrangeControls();
    }

    protected override void ApplyLayout(float newScale)
    {
        scale = newScale;
        UseTranscriptFont();
        arranged = true;
        MinimumSize = new Size(D(MinimumWidth), D(MinimumHeight));
        ArrangeControls();
    }

    protected override void OnRecordChanged()
    {
        pauseButton.Visible = recording;
        retryShown = !recording && record.Status == TranscriptionStatus.Incomplete && !string.IsNullOrEmpty(record.AudioPath) && File.Exists(record.AudioPath);
        retryButton.Visible = retryShown;
        if (recording) SetStatus(paused ? PausedStatus : ListeningStatus);
        else if (record.Status == TranscriptionStatus.Completed) SetStatus(NativeTheme.IsCrabapple ? "说完了，改好按 Enter 发送" : "改好按 Enter 发送");
        else SetStatus("转写没完成，文字和录音都保留着", true);
        UpdateSendLabel();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        targetTimer.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            targetTimer.Dispose();
            closeTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>窗口开着时用户点到别的程序，就贴到那个程序：点到哪贴到哪。</summary>
    private void TrackTarget()
    {
        if (PasteService.ForegroundTarget() is { } current && current != target)
        {
            target = current;
            targetRevision++;
            UpdateSendLabel();
        }
        else if (target is { } previous && !PasteService.IsAlive(previous.Window))
        {
            target = null;
            targetRevision++;
            UpdateSendLabel();
        }
    }

    private void CopyOnly()
    {
        if (string.IsNullOrWhiteSpace(transcript.Text)) return;
        ClipboardService.CopyText(transcript.Text);
        copyButton.Text = "已复制";
        // 海棠的字按钮跟着字宽排，多一个字要重新排
        if (arranged && NativeTheme.IsCrabapple) ArrangeControls();
    }

    private void ShowCopied()
    {
        SetStatus("已复制，到想放的地方按 Ctrl+V");
        closeTimer.Start();
    }

    private void UpdateSendLabel()
    {
        string name = target?.AppName ?? string.Empty;
        if (name.Length > MaxAppNameLength) name = name[..MaxAppNameLength] + "…";
        sendButton.Text = target is null ? "复制  Enter" : $"贴到 {name}  Enter";
        sendButton.AccessibleName = target is null ? "复制文字" : "把文字贴到 " + target.Value.AppName;
        // 印和星湖的按钮都跟着字的宽度走
        if (arranged) ArrangeControls();
    }

    /// <summary>星湖：左上标题，右上月亮和关闭，文字写在玻璃上，按钮靠右下。</summary>
    private void ArrangeStarlake()
    {
        int width = ClientSize.Width, height = ClientSize.Height;
        rippleBounds = new Rectangle(width - D(98), D(23), D(40), D(40));
        headingBounds = new Rectangle(D(35), D(26), width - D(150), D(34));
        closeButton.SetBounds(width - D(53), D(30), D(26), D(26));
        int buttonY = height - D(57), buttonHeight = D(32), gap = D(8), small = D(56), right = width - D(27);
        int sendWidth = Math.Clamp(sendButton.MeasureContent(scale) + D(26), D(100), D(240));
        sendButton.SetBounds(right - sendWidth, buttonY, sendWidth, buttonHeight);
        copyButton.SetBounds(sendButton.Left - gap - small, buttonY, small, buttonHeight);
        pauseButton.SetBounds(copyButton.Left - gap - small, buttonY, small, buttonHeight);
        retryButton.SetBounds(copyButton.Left - gap - D(84), buttonY, D(84), buttonHeight);
        // 错误信息长，单独占一行，平时和按钮并排
        int hintRight = (recording ? pauseButton : retryButton.Visible ? retryButton : copyButton).Left - D(10);
        statusBounds = hasError
            ? new Rectangle(D(35), buttonY - D(42), width - D(70), D(36))
            : new Rectangle(D(35), buttonY + D(7), Math.Max(D(60), hintRight - D(35)), D(19));
        int textTop = D(73), textBottom = hasError ? statusBounds.Top - D(4) : buttonY - D(12);
        // 系统文字框自带 5 像素边距，往左让出来，字才和标题对齐
        transcript.SetBounds(D(30), textTop, width - D(60), Math.Max(D(40), textBottom - textTop));
        RefreshBackdrop();
    }

    private void ArrangeControls()
    {
        SuspendLayout();
        int width = ClientSize.Width, height = ClientSize.Height;
        bool paper = NativeTheme.IsCrabapple;
        if (!paper)
        {
            ArrangeStarlake();
            ResumeLayout();
            Invalidate();
            return;
        }

        // 海棠：左边一张题签写字，右边露出一栏印花。窗口拉大时题签跟着变大，印花栏宽度不变
        Rectangle slip = Rectangle.FromLTRB(D(SlipMargin), D(SlipMargin), width - D(PrintColumn), height - D(SlipMargin));
        panelBounds = slip;
        slips = [new PaperSlip(slip, true)];
        headingBounds = Rectangle.Empty;
        int textTop = ArrangeSlipHead(slip);
        GlassButton[] quiet = recording ? [pauseButton, copyButton] : retryShown ? [retryButton, copyButton] : [copyButton];
        int textBottom = ArrangeSlipButtons(slip, quiet, sendButton);
        PlaceSlipText(slip, textTop, textBottom);
        ResumeLayout();
        Invalidate();
    }
}
