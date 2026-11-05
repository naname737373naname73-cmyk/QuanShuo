using System.Drawing.Drawing2D;
using QuanShuo.Core.Models;
using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Recording;

/// <summary>圈选之后的录音卡片，带截图缩略图。</summary>
public sealed class RecordingCard : RecordingSurface
{
    private const int StarlakeHeight = 296;
    private const int PaperHeight = 373;
    /// <summary>印花原图 1254×1254，从这里开始取，截图下面露出的是一整朵花。</summary>
    private static readonly PointF PrintOrigin = new(274, 420);
    private const float PrintZoom = .55F;
    private readonly GlassButton finishButton = new();
    private readonly GlassButton copyButton = new();
    private readonly GlassButton moreButton = new();
    private readonly System.Windows.Forms.Timer autoHideTimer = new() { Interval = 8000 };
    private Bitmap? thumbnail;
    private bool touched;
    private bool retryShown;
    private Rectangle thumbnailBounds;

    public RecordingCard(QuanShuoRecord initialRecord, RecordingActions actions)
        : base(initialRecord, actions)
    {
        ApplySkin();
        BuildLayout();
        ApplyLayout(NativeTheme.ScaleAt(Cursor.Position));
        PositionCard(Screen.FromPoint(Cursor.Position).WorkingArea);
        LoadThumbnail();
        transcript.ReplaceAll(initialRecord.Text);
        OnRecordChanged();
        autoHideTimer.Tick += (_, _) => { if (!recording && !touched) Hide(); };
        transcript.MouseDown += (_, _) => Touch();
        transcript.KeyDown += (_, _) => Touch();
    }

    protected override string ListeningStatus => EditingTranscript ? "随时可以改" : NativeTheme.IsCrabapple ? "正在听 · 全局空格暂停" : "全局空格暂停";
    protected override string PausedStatus => automaticallyPaused ? "静音已暂停" : EditingTranscript ? "已暂停，改好点继续" : NativeTheme.IsCrabapple ? "已暂停 · 全局空格继续" : "全局空格继续";
    protected override CrabapplePrint CreatePrint() => new(PrintOrigin, PrintZoom);

    protected override void StyleButtons()
    {
        Style(finishButton, ButtonSkin.Seal, true);
        Style(copyButton, ButtonSkin.Seal, true);
        Style(moreButton, ButtonSkin.Ink);
    }

    public override void SetFinished(QuanShuoRecord completed)
    {
        base.SetFinished(completed);
        autoHideTimer.Start();
    }

    protected override void OnErrorShown() => autoHideTimer.Stop();
    protected override void OnFinishing() => finishButton.Enabled = false;
    protected override void OnFinishFailed() => finishButton.Enabled = true;
    protected override void OnEditedAfterFinish() => Touch();

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionCard(Screen.FromControl(this).WorkingArea);
        Activate();
        FocusRecordingControls();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (recording || !touched) return;
        // 用户离开卡片后才开始计时收起，改到一半想事情时卡片不会自己消失
        touched = false;
        autoHideTimer.Stop();
        autoHideTimer.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (recording && e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _ = FinishRecordingAsync();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (recording) _ = FinishRecordingAsync();
            else Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            autoHideTimer.Dispose();
            thumbnail?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Touch();
        if (thumbnailBounds.Contains(e.Location))
        {
            if (File.Exists(record.ImagePath))
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { record.ImagePath });
                DoDragDrop(data, DragDropEffects.Copy);
            }

            return;
        }

        FocusRecordingControls();
        NativeTheme.DragWindow(Handle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = thumbnailBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintCard(e.Graphics);
        DrawThumbnail(e.Graphics);
        DrawStatus(e.Graphics);
    }

    protected override void ApplyLayout(float newScale)
    {
        scale = newScale;
        SuspendLayout();
        // 不能用 ClientSize：窗口样式里带边框，句柄创建后设它会多加一圈边框的尺寸，底图就被拉变形了
        bool paper = NativeTheme.IsCrabapple;
        Size = new Size(D(540), D(paper ? PaperHeight : StarlakeHeight));
        if (!paper)
        {
            ArrangeStarlake();
            UseTranscriptFont();
            ResumeLayout();
            RefreshBackdrop();
            Invalidate();
            return;
        }

        // 海棠：截图垫一张纸放在左上，右边一张题签写字，其余地方露出印花
        var mat = new Rectangle(D(22), D(22), D(170), D(126));
        thumbnailBounds = Rectangle.Inflate(mat, -D(6), -D(6));
        panelBounds = Rectangle.FromLTRB(D(208), D(22), D(518), D(PaperHeight - 22));
        slips = [new PaperSlip(mat, false), new PaperSlip(panelBounds, true)];
        headingBounds = Rectangle.Empty;
        UseTranscriptFont();
        ArrangeButtons();
        ResumeLayout();
        Invalidate();
    }

    protected override void OnErrorStateChanged()
    {
        if (NativeTheme.IsCrabapple) ArrangeButtons();
        else ArrangeStarlake();
    }

    protected override void OnRecordChanged()
    {
        pauseButton.Visible = recording;
        finishButton.Visible = recording;
        copyButton.Visible = !recording;
        moreButton.Visible = !recording;
        retryShown = !recording && record.Status == TranscriptionStatus.Incomplete && !string.IsNullOrEmpty(record.AudioPath) && File.Exists(record.AudioPath);
        retryButton.Visible = retryShown;
        ArrangeButtons();
        SetStatus(recording ? paused ? PausedStatus : ListeningStatus
            : record.Status == TranscriptionStatus.Completed ? NativeTheme.IsCrabapple ? "转写完成，可以直接改" : "可以直接改" : "转写没完成，文字和录音都保留着", !recording && record.Status == TranscriptionStatus.Incomplete);
        copyButton.Text = "复制给 AI";
    }

    /// <summary>星湖：缩略图在左，文字在右，按钮靠右下，和说话窗口一样。</summary>
    private void ArrangeStarlake()
    {
        int width = D(540), height = D(StarlakeHeight);
        rippleBounds = new Rectangle(width - D(98), D(23), D(40), D(40));
        headingBounds = new Rectangle(D(35), D(26), width - D(150), D(34));
        closeButton.SetBounds(width - D(53), D(30), D(26), D(26));
        thumbnailBounds = new Rectangle(D(35), D(75), D(182), D(118));
        int buttonY = height - D(57), buttonHeight = D(32), right = width - D(27), gap = D(8);
        var row = new List<GlassButton>();
        if (recording) row.AddRange([pauseButton, finishButton]);
        else
        {
            if (retryButton.Visible) row.Add(retryButton);
            row.AddRange([moreButton, copyButton]);
        }

        for (int index = row.Count - 1; index >= 0; index--)
        {
            int buttonWidth = row[index].MeasureContent(scale) + D(26);
            row[index].SetBounds(right - buttonWidth, buttonY, buttonWidth, buttonHeight);
            right -= buttonWidth + gap;
        }

        statusBounds = hasError
            ? new Rectangle(D(35), buttonY - D(42), width - D(70), D(36))
            : new Rectangle(D(35), buttonY + D(7), Math.Max(D(60), right - D(35)), D(19));
        int textLeft = thumbnailBounds.Right + D(12), textTop = D(71);
        int textBottom = hasError ? statusBounds.Top - D(4) : buttonY - D(12);
        transcript.SetBounds(textLeft, textTop, width - D(30) - textLeft, Math.Max(D(40), textBottom - textTop));
        RefreshBackdrop();
        Invalidate();
    }

    /// <summary>
    /// 海棠的题签：上沿是状态，下沿左边是只有字的按钮、右边是印，中间写字。
    /// 录音时印是“结束”，说完了是“复制给 AI”。
    /// </summary>
    private void ArrangeButtons()
    {
        if (!NativeTheme.IsCrabapple)
        {
            ArrangeStarlake();
            return;
        }

        int textTop = ArrangeSlipHead(panelBounds);
        GlassButton[] quiet = recording ? [pauseButton] : retryShown ? [moreButton, retryButton] : [moreButton];
        int textBottom = ArrangeSlipButtons(panelBounds, quiet, recording ? finishButton : copyButton);
        PlaceSlipText(panelBounds, textTop, textBottom);
        Invalidate();
    }

    private void Touch()
    {
        touched = true;
        autoHideTimer.Stop();
    }

    private void DrawThumbnail(Graphics graphics)
    {
        if (!graphics.IsVisible(thumbnailBounds)) return;
        bool paper = NativeTheme.IsCrabapple;
        var state = graphics.Save();
        // 海棠的截图贴在垫纸上，直角；比例不合时空出来的地方就是垫纸
        using var path = paper ? new GraphicsPath() : NativeTheme.RoundedRectangle(thumbnailBounds, NativeTheme.ControlRadius * scale);
        if (paper) path.AddRectangle(thumbnailBounds);
        graphics.SetClip(path, CombineMode.Intersect);
        using var fill = new SolidBrush(paper ? NativeTheme.Slip : NativeTheme.Sapphire);
        graphics.FillRectangle(fill, thumbnailBounds);
        if (thumbnail is not null)
        {
            float ratio = Math.Min(thumbnailBounds.Width / (float)thumbnail.Width, thumbnailBounds.Height / (float)thumbnail.Height);
            float width = thumbnail.Width * ratio, height = thumbnail.Height * ratio;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(thumbnail, thumbnailBounds.X + (thumbnailBounds.Width - width) / 2, thumbnailBounds.Y + (thumbnailBounds.Height - height) / 2, width, height);
        }

        graphics.Restore(state);
        if (paper) return;
        using var border = new Pen(NativeTheme.Hairline, scale);
        graphics.DrawPath(border, path);
    }

    private void BuildLayout()
    {
        closeButton.AccessibleName = "关闭录音卡片";
        finishButton.Text = "结束  Enter";
        finishButton.Click += (_, _) => _ = FinishRecordingAsync();
        copyButton.Text = "复制给 AI";
        copyButton.Click += async (_, _) =>
        {
            await SaveEditedTextAsync();
            ClipboardService.CopyForAI([record]);
            copyButton.Text = "已复制";
        };
        moreButton.Text = "更多复制";
        moreButton.Click += (_, _) => ShowCopyMenu();
        Controls.AddRange([closeButton, transcript, pauseButton, finishButton, copyButton, moreButton, retryButton]);
        finishButton.TabIndex = 2;
        copyButton.TabIndex = 3;
        moreButton.TabIndex = 4;
        ActiveControl = pauseButton;
    }

    private void PositionCard(Rectangle area) => Location = new Point(area.Right - Width - D(24), area.Bottom - Height - D(24));

    private void ShowCopyMenu()
    {
        var menu = GlassMenuRenderer.CreateMenu();
        menu.Items.Add("复制图片路径", null, (_, _) => ClipboardService.CopyFilePath(record.ImagePath));
        menu.Items.Add("复制文字文件路径", null, (_, _) => ClipboardService.CopyFilePath(record.MarkdownPath));
        menu.Items.Add("复制文字内容", null, (_, _) => ClipboardService.CopyText(record.Text));
        menu.Items.Add("复制图片本身", null, (_, _) => ClipboardService.CopyImage(record.ImagePath));
        menu.Show(moreButton, new Point(0, moreButton.Height));
        menu.Closed += (_, _) => menu.Dispose();
    }

    private void LoadThumbnail()
    {
        if (!File.Exists(record.ImagePath)) return;
        using var source = new Bitmap(record.ImagePath);
        thumbnail = new Bitmap(source);
    }
}
