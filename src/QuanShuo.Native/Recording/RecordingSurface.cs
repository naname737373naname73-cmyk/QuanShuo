using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using QuanShuo.Core.Models;
using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Recording;

public sealed record RecordingActions(
    Func<Task> Finish,
    Action TogglePause,
    Func<QuanShuoRecord, string, Task> SaveText,
    Func<QuanShuoRecord, Task<QuanShuoRecord>> Retry,
    Action<string> TextEdited);

/// <summary>录音卡片和说话悬浮窗共用的部分：无边框外壳、音量、可编辑文字和保存。</summary>
public abstract class RecordingSurface : Form
{
    private const int MoonTicks = 40;
    private const float MoonTickInner = 14.5F;
    private const float MoonTickDim = 17;
    private const float MoonTickLit = 18.5F;
    private const float HeadingSize = 21;
    private const float HeadingSpacing = 1.7F;
    private const float TimeSize = 13;
    private const float TimeSpacing = 1.3F;
    /// <summary>时长和标题底部对齐，字小，要比居中再往下一点。</summary>
    private const float TimeBaseline = .8F;
    private const float TranscriptSize = 17;
    private const float StatusSize = 13;
    /// <summary>海棠“正在听”前面的小圆点和随音量扩开的一圈。</summary>
    private const float DotRadius = 3.5F;
    private const float DotRing = 3;
    private const float DotReach = 7;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmNcPaint = 0x0085;
    private const int WmNcActivate = 0x0086;
    private const int WmCtlColorEdit = 0x0133;
    private const int WmCtlColorStatic = 0x0138;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const long KeyWasAlreadyDown = 1L << 30;
    private const int TransparentBackground = 1;
    private const int NullBrush = 5;
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 550 };
    private readonly SemaphoreSlim editSaveLock = new(1, 1);
    private bool hasUnsavedText;
    private bool closingAfterSave;
    private readonly System.Windows.Forms.Timer rippleTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer secondTimer = new() { Interval = 250 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Stopwatch spoken = Stopwatch.StartNew();
    private readonly StarlakeGlass glass = new();
    private CardHalo? halo;
    private Bitmap? haloGlass;
    private Rectangle haloBounds;
    private Size cutSize;
    private readonly RecordingActions actions;
    private CrabapplePrint? print;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<(float Scale, bool Paper), Font> transcriptFonts = [];
    private int shownSeconds;
    private bool saving;
    private bool closeAfterFinish;
    private bool rescalePending;
    private bool quietMode;
    private bool reduceMotion;
    private bool pauseSpaceHeld;
    private float targetLevel;
    private float smoothLevel;
    private double lastLevelAt;

    protected RecordingSurface(QuanShuoRecord initialRecord, RecordingActions actions)
    {
        record = initialRecord;
        this.actions = actions;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        // 涟漪每秒重绘 30 次，不开双缓冲会闪，局部区域也会和渐变背景对不齐。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        reduceMotion = NativeTheme.ReduceMotionEnabled;
        NativeTheme.SkinChanged += OnSkinChanged;
        closeButton.Text = "×";
        closeButton.Click += (_, _) => Close();
        transcript.AccessibleName = "转写文字";
        transcript.AccessibleDescription = "点击或按 Tab 编辑文字；编辑时空格用于输入，不会暂停录音。";
        transcript.TabIndex = 1;
        transcript.GotFocus += (_, _) => RefreshRecordingHint();
        transcript.LostFocus += (_, _) => RefreshRecordingHint();
        transcript.Committed += (_, _) => OnTextCommitted();
        pauseButton.Text = "暂停";
        pauseButton.AccessibleName = "暂停录音，空格键";
        pauseButton.TabIndex = 0;
        pauseButton.Click += (_, _) => ToggleRecordingPause();
        closeButton.TabIndex = 9;
        retryButton.TabIndex = 5;
        retryButton.Text = "重新转写";
        retryButton.Click += async (_, _) => await RetryAsync();
        saveTimer.Tick += async (_, _) => await TrySaveEditedTextAsync();
        rippleTimer.Tick += (_, _) => AdvanceRipple();
        secondTimer.Tick += (_, _) => AdvanceTime();
        secondTimer.Start();
    }

    protected LiveTextBox transcript { get; } = new();
    protected GlassButton pauseButton { get; } = new();
    protected GlassButton retryButton { get; } = new();
    protected GlassButton closeButton { get; } = new();
    protected QuanShuoRecord record { get; private set; }
    protected bool recording { get; private set; } = true;
    protected bool paused { get; private set; }
    protected bool automaticallyPaused { get; private set; }
    protected bool hasError { get; private set; }
    protected string statusText { get; private set; } = string.Empty;
    protected float scale { get; set; } = 1;
    protected Rectangle rippleBounds { get; set; }
    protected Rectangle statusBounds { get; set; }
    /// <summary>海棠写字的那张题签。</summary>
    protected Rectangle panelBounds { get; set; }
    /// <summary>海棠印花上贴的纸：写字的题签，卡片还有垫截图的那张。</summary>
    protected IReadOnlyList<PaperSlip> slips { get; set; } = [];
    /// <summary>星湖的标题和时长。</summary>
    protected Rectangle headingBounds { get; set; }
    protected Task WhenFinished => finished.Task;
    /// <summary>圆角和阴影那一层，测试用。</summary>
    internal Form? Halo => halo;

    public string RecordId => record.Id;

    /// <summary>当前这一条录音的文字，给协调器合并识别结果用。</summary>
    public string MergeRecognized(string recognized, bool sentenceEnd) => transcript.Merge(recognized, sentenceEnd);

    /// <summary>协调器结束保存前调用，确保暂存的最终识别也被落盘。</summary>
    public string CaptureFinalText()
    {
        transcript.FlushDeferred();
        return transcript.Text;
    }

    [DefaultValue(false)]
    public bool QuietMode
    {
        get => quietMode;
        set { quietMode = value; SyncRipple(); Invalidate(rippleBounds); }
    }

    protected override bool ShowWithoutActivation => true;
    private bool MotionAllowed => !quietMode && !reduceMotion;
    protected int D(float value) => NativeTheme.Px(value, scale);

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // 保留 DWM 判断阴影与圆角所需的框架样式，非客户区在下面收回。
            parameters.Style |= 0x00040000 | 0x00C00000;
            return parameters;
        }
    }

    public void SetLevel(float value)
    {
        if (!recording) return;
        targetLevel = Math.Clamp(value, 0, 1);
        lastLevelAt = clock.Elapsed.TotalSeconds;
        SyncRipple();
    }

    public void SetPaused(bool value, bool automatic = false)
    {
        if (!recording || saving) return;
        paused = value;
        automaticallyPaused = value && automatic;
        if (value) spoken.Stop();
        else spoken.Start();
        pauseButton.Text = value ? "继续" : "暂停";
        pauseButton.AccessibleName = value ? "继续录音，空格键" : "暂停录音，空格键";
        if (!hasError) SetStatus(value ? PausedStatus : ListeningStatus);
        SyncRipple();
        Invalidate(rippleBounds);
        Invalidate(headingBounds);
    }

    public virtual void SetFinished(QuanShuoRecord completed)
    {
        recording = false;
        saving = false;
        spoken.Stop();
        secondTimer.Stop();
        SyncRipple();
        // 收尾这一两秒里用户可能还在打字，组字中的识别结果也可能还没写进来，都以文字框为准
        transcript.FlushDeferred();
        bool diverged = transcript.Text != completed.Text;
        record = completed with { Text = transcript.Text };
        if (diverged) ScheduleSave();
        Invalidate(rippleBounds);
        Invalidate(headingBounds);
        OnRecordChanged();
        finished.TrySetResult();
        if (closeAfterFinish) Close();
    }

    public void SetError(string message)
    {
        SetStatus(message, true);
        OnErrorShown();
    }

    /// <summary>把还在防抖的文字写进文件。切换到下一条录音前调用。</summary>
    public async Task SaveEditedTextAsync()
    {
        if (recording) return;
        saveTimer.Stop();
        hasUnsavedText = true;
        await editSaveLock.WaitAsync();
        try
        {
            // 关闭/截图/退出都等待此任务；写盘时的新输入也必须保存后才能返回。
            do
            {
                string text = transcript.Text;
                record = record with { Text = text };
                await actions.SaveText(record, text);
                hasUnsavedText = transcript.Text != text;
            } while (hasUnsavedText);
        }
        finally { editSaveLock.Release(); }
    }

    private async Task<bool> TrySaveEditedTextAsync()
    {
        try { await SaveEditedTextAsync(); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError("文字尚未保存：" + exception.Message);
            return false;
        }
    }

    private async Task SaveAndCloseAsync()
    {
        try
        {
            if (await TrySaveEditedTextAsync() && !hasUnsavedText) Close();
        }
        finally { closingAfterSave = false; }
    }

    protected abstract string ListeningStatus { get; }
    protected abstract string PausedStatus { get; }
    protected abstract void ApplyLayout(float newScale);
    protected abstract void OnRecordChanged();
    protected virtual void OnErrorShown() { }
    protected virtual void OnFinishing() { }
    protected virtual void OnFinishFailed() { }
    protected virtual int FrameHitTest(Point client) => 1;

    /// <summary>错误信息比平时的状态长，窗口要多留一行给它。</summary>
    protected virtual void OnErrorStateChanged() { }

    /// <summary>海棠的印花。第一次画的时候才读文件，星湖用不着。</summary>
    protected abstract CrabapplePrint CreatePrint();

    /// <summary>窗口上的按钮按当前皮肤上色。基类的构造函数会调它，此时子类的字段已经就绪。</summary>
    protected virtual void StyleButtons() { }

    protected static void Style(GlassButton button, ButtonSkin paper, bool primary = false)
    {
        button.Skin = NativeTheme.IsCrabapple ? paper : ButtonSkin.Starlit;
        button.Primary = primary;
    }

    /// <summary>用户在录音结束后改了文字。</summary>
    protected virtual void OnEditedAfterFinish() { }

    /// <summary>开始时留在录音控制上，空格可以直接暂停；Tab 或点击文字后仍然正常打字。</summary>
    protected void FocusRecordingControls()
    {
        if (recording && !saving) pauseButton.Focus();
        else transcript.Focus();
    }

    private void RefreshRecordingHint()
    {
        if (recording && !saving && !hasError) SetStatus(paused ? PausedStatus : ListeningStatus);
    }

    private void ToggleRecordingPause()
    {
        if (recording && !saving && pauseButton.Enabled) actions.TogglePause();
    }

    protected override bool ProcessKeyPreview(ref Message message)
    {
        if ((Keys)message.WParam.ToInt32() != Keys.Space) return base.ProcessKeyPreview(ref message);
        if (message.Msg == WmKeyUp && (pauseSpaceHeld || recording && !transcript.ContainsFocus))
        {
            // GlassButton 的 KeyUp 也会点击；连打开卡片前按下的空格抬键也要忽略。
            pauseSpaceHeld = false;
            return true;
        }
        if (pauseSpaceHeld && message.Msg is WmKeyDown or WmChar) return true;
        if (message.Msg == WmKeyDown && ModifierKeys == Keys.None && !transcript.ContainsFocus && recording && !saving && pauseButton.Enabled)
        {
            pauseSpaceHeld = true;
            // 截图可能由仍按着的空格开启，系统重复消息不能把新录音立刻暂停。
            if ((message.LParam.ToInt64() & KeyWasAlreadyDown) == 0) ToggleRecordingPause();
            return true;
        }
        return base.ProcessKeyPreview(ref message);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        pauseSpaceHeld = false;
        base.OnDeactivate(e);
    }

    /// <summary>海棠题签的上沿：圆点、状态和关闭按钮。返回文字从哪里开始，出错时状态占两行，文字往下让。</summary>
    protected int ArrangeSlipHead(Rectangle slip)
    {
        int reach = D(14);
        var dot = new Point(slip.Left + D(27.5F), slip.Top + D(28));
        rippleBounds = new Rectangle(dot.X - reach, dot.Y - reach, reach * 2, reach * 2);
        closeButton.SetBounds(slip.Right - D(42), slip.Top + D(16), D(24), D(24));
        // 说完了圆点就不画，状态往左挪到圆点的位置
        int statusLeft = slip.Left + D(recording ? 41 : 24);
        statusBounds = new Rectangle(statusLeft, slip.Top + D(18), Math.Max(D(40), closeButton.Left - D(8) - statusLeft), D(hasError ? 38 : 20));
        return statusBounds.Bottom + D(hasError ? 8 : 12);
    }

    /// <summary>题签下沿：左边是只有字的按钮，右边是一方印。返回文字框能用到哪里。</summary>
    protected int ArrangeSlipButtons(Rectangle slip, IReadOnlyList<GlassButton> quiet, GlassButton seal)
    {
        int sealHeight = D(34), sealY = slip.Bottom - D(18) - sealHeight;
        int sealWidth = Math.Clamp(seal.MeasureContent(scale), D(96), Math.Max(D(96), slip.Width - D(150)));
        seal.SetBounds(slip.Right - D(20) - sealWidth, sealY, sealWidth, sealHeight);
        int x = slip.Left + D(18);
        foreach (GlassButton button in quiet)
        {
            int width = button.MeasureContent(scale);
            button.SetBounds(x, sealY + D(2), width, D(30));
            x += width + D(8);
        }

        return sealY - D(8);
    }

    /// <summary>系统文字框自带 5 像素边距，往左让出来，字才和状态对齐。</summary>
    protected void PlaceSlipText(Rectangle slip, int top, int bottom)
    {
        int left = slip.Left + D(19);
        transcript.SetBounds(left, top, slip.Right - D(22) - left, Math.Max(D(40), bottom - top));
    }

    protected void ScheduleSave()
    {
        hasUnsavedText = true;
        saveTimer.Stop();
        saveTimer.Start();
    }

    protected void SetStatus(string text, bool isError = false)
    {
        bool layoutChanged = hasError != isError;
        statusText = text;
        hasError = isError;
        if (layoutChanged) OnErrorStateChanged();
        Invalidate(statusBounds);
    }

    protected async Task<bool> FinishRecordingAsync()
    {
        if (!recording) return true;
        OnFinishing();
        saving = true;
        Invalidate(headingBounds);
        SetStatus("正在结束并保存…");
        try
        {
            await actions.Finish();
            return true;
        }
        catch (Exception exception)
        {
            saving = false;
            closeAfterFinish = false;
            Invalidate(headingBounds);
            SetError("保存没有完成：" + exception.Message);
            OnFinishFailed();
            return false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
        // 裁过形状的窗口不归 DWM 画边框，系统会把老式标题栏直接画在内容上
        if (!cutSize.IsEmpty && m.Msg == WmNcPaint) { m.Result = IntPtr.Zero; return; }
        if (!cutSize.IsEmpty && m.Msg == WmNcActivate) { m.Result = new IntPtr(1); return; }
        if (m.Msg is WmCtlColorEdit or WmCtlColorStatic && transcript.HasBackdrop && transcript.IsHandleCreated && m.LParam == transcript.Handle)
        {
            SetBkMode(m.WParam, TransparentBackground);
            SetTextColor(m.WParam, ColorTranslator.ToWin32(transcript.ForeColor));
            m.Result = GetStockObject(NullBrush);
            return;
        }

        base.WndProc(ref m);
        if (m.Msg == 0x0084)
        {
            long location = m.LParam.ToInt64();
            m.Result = new IntPtr(FrameHitTest(PointToClient(new Point((short)(location & 0xffff), (short)((location >> 16) & 0xffff)))));
        }

        if (m.Msg == 0x001A)
        {
            reduceMotion = NativeTheme.ReduceMotionEnabled;
            SyncRipple();
            Invalidate(rippleBounds);
        }

        // 窗口被提到最上面时，下面那层要跟上，不能让别的窗口夹在中间
        if (m.Msg == WmWindowPosChanged && halo is { Visible: true } && !halo.IsDisposed) halo.StayBelow(Handle);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (halo is { Visible: true }) halo.Follow(Bounds, Handle, scale);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrame();
        CutCorners();
        RefreshBackdrop();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        CutCorners();
        RefreshBackdrop();
    }

    private void ApplyFrame()
    {
        // 海棠是一张纸，用系统的小圆角去掉尖角。星湖的圆角比系统的大，自己裁
        NativeTheme.ApplyWindowFrame(Handle, NativeTheme.IsCrabapple, small: true);
        NativeTheme.RefreshFrame(Handle);
    }

    /// <summary>窗口裁成圆角并小一圈，边缘露出下面的 <see cref="CardHalo"/>，那里有抗锯齿。</summary>
    private void CutCorners()
    {
        if (!IsHandleCreated) return;
        if (NativeTheme.IsCrabapple)
        {
            if (cutSize.IsEmpty) return;
            cutSize = Size.Empty;
            SetWindowRgn(Handle, IntPtr.Zero, true);
            return;
        }

        if (Size == cutSize) return;
        cutSize = Size;
        int inset = Math.Max(1, D(1));
        int diameter = D(StarlakeGlass.CornerRadius * 2) - inset * 2;
        SetWindowRgn(Handle, CreateRoundRectRgn(inset, inset, Width - inset, Height - inset, diameter, diameter), true);
    }

    private void SyncHalo()
    {
        // 测试时窗口是全透明打开的，这一层不能露出来
        bool wanted = !NativeTheme.IsCrabapple && IsHandleCreated && Visible && Opacity >= 1 && ClientSize.Width > 0 && ClientSize.Height > 0;
        if (!wanted)
        {
            halo?.Hide();
            return;
        }

        halo ??= new CardHalo();
        Bitmap source = RenderGlass();
        if (!ReferenceEquals(source, haloGlass) || Bounds.Size != haloBounds.Size)
        {
            halo.Draw(source, Bounds, scale);
            haloGlass = source;
            haloBounds = Bounds;
        }

        if (!halo.Visible) halo.Show();
        halo.Follow(Bounds, Handle, scale);
    }

    /// <summary>构造函数和换皮肤时都走这里。</summary>
    protected void ApplySkin()
    {
        bool paper = NativeTheme.IsCrabapple;
        BackColor = paper ? NativeTheme.PrintGround : NativeTheme.Surface;
        ForeColor = paper ? NativeTheme.SlipInk : NativeTheme.Text;
        // 海棠的字写在题签上
        transcript.BackColor = paper ? NativeTheme.Slip : NativeTheme.Surface;
        transcript.ForeColor = paper ? NativeTheme.SlipInk : NativeTheme.Text;
        Style(closeButton, ButtonSkin.PaperClose);
        if (!paper) closeButton.Skin = ButtonSkin.StarlitClose;
        Style(pauseButton, ButtonSkin.Ink);
        Style(retryButton, ButtonSkin.Ink);
        StyleButtons();
    }

    /// <summary>录音时打开设置换了皮肤，开着的窗口当场跟着换。</summary>
    private void OnSkinChanged()
    {
        if (IsDisposed) return;
        ApplySkin();
        if (IsHandleCreated) ApplyFrame();
        ApplyLayout(scale);
        CutCorners();
        RefreshBackdrop();
        SyncHalo();
        Invalidate(true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyLayout(e.DeviceDpiNew / 96F);
        // 子控件的字体由系统在这之后再放大一次，所以等它放大完再设回去。
        // 窗口创建时系统会来回发 144→96→144，这里要读当时真实的缩放，不能记事件里的值。
        if (rescalePending) return;
        rescalePending = true;
        BeginInvoke(() =>
        {
            rescalePending = false;
            if (!IsDisposed) ApplyLayout(DeviceDpi / 96F);
        });
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        SyncRipple();
        SyncHalo();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (recording)
        {
            e.Cancel = true;
            if (!closeAfterFinish)
            {
                closeAfterFinish = true;
                _ = FinishRecordingAsync();
            }
            return;
        }

        if (hasUnsavedText || editSaveLock.CurrentCount == 0)
        {
            e.Cancel = true;
            if (!closingAfterSave)
            {
                closingAfterSave = true;
                _ = SaveAndCloseAsync();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NativeTheme.SkinChanged -= OnSkinChanged;
            saveTimer.Dispose();
            rippleTimer.Dispose();
            secondTimer.Dispose();
            print?.Dispose();
            halo?.Dispose();
        }

        base.Dispose(disposing);
        if (disposing)
        {
            // 窗口销毁途中还可能来一次重绘，玻璃要等它彻底没了才能释放
            glass.Dispose();
            foreach (Font font in transcriptFonts.Values) font.Dispose();
            transcriptFonts.Clear();
        }
    }

    /// <summary>
    /// 换屏时窗口的缩放比例可能来回变。Control.Font 遇到“相等”的字体不会替换，
    /// 换一次释放一次的话，文字框手里的可能正是刚释放的那个，创建句柄时就会报 Parameter is not valid。
    /// 所以每种比例的字体留到窗口销毁时才释放。
    /// </summary>
    protected void UseTranscriptFont()
    {
        bool paper = NativeTheme.IsCrabapple;
        if (!transcriptFonts.TryGetValue((scale, paper), out Font? font))
        {
            // 两套皮肤都用宋体、字号一样；按皮肤分开存，换皮肤时文字框才会重新设一次字体
            font = NativeTheme.TitleFont(TranscriptSize, scale);
            transcriptFonts[(scale, paper)] = font;
        }

        transcript.Font = font;
    }

    /// <summary>星湖的文字直接写在玻璃上，布局一变就重新切一块背景给文字框。</summary>
    protected void RefreshBackdrop()
    {
        if (NativeTheme.IsCrabapple || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0 || !ClientRectangle.Contains(transcript.Bounds) || transcript.Width <= 0 || transcript.Height <= 0)
        {
            transcript.SetBackdrop(null);
            return;
        }

        using Bitmap slice = RenderGlass().Clone(transcript.Bounds, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        transcript.SetBackdrop(slice);
        SyncHalo();
    }

    private Bitmap RenderGlass()
    {
        glass.MoonCenter = new PointF(rippleBounds.Left + rippleBounds.Width / 2F, rippleBounds.Top + rippleBounds.Height / 2F);
        return glass.Render(ClientSize, scale);
    }

    protected void PaintCard(Graphics graphics)
    {
        NativeTheme.Prepare(graphics);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (NativeTheme.IsCrabapple)
        {
            print ??= CreatePrint();
            Blit(graphics, print.Render(ClientSize, scale, slips));
            DrawListeningDot(graphics);
            return;
        }

        Blit(graphics, RenderGlass());
        DrawHeading(graphics);
        DrawMoonLevel(graphics);
    }

    /// <summary>缓存好的底图直接贴上去。音量每秒重绘 30 次，这里不能慢。</summary>
    private void Blit(Graphics graphics, Bitmap source)
    {
        GraphicsState state = graphics.Save();
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var whole = new Rectangle(Point.Empty, ClientSize);
        graphics.DrawImage(source, whole, whole, GraphicsUnit.Pixel);
        graphics.Restore(state);
    }

    /// <summary>标题说现在的状态，时长用桃色，写法和主窗口的“今天亮起 8 颗星”一样。</summary>
    private void DrawHeading(Graphics graphics)
    {
        if (headingBounds.IsEmpty || !graphics.IsVisible(headingBounds)) return;
        string heading = saving ? "正在保存" : recording ? paused ? "已暂停" : "正在听" : record.Status == TranscriptionStatus.Completed ? "说完了" : "没转写完";
        using Font title = NativeTheme.TitleFont(HeadingSize, scale);
        using Font small = NativeTheme.TitleFont(TimeSize, scale);
        // 思源宋体是可变字体，GDI+ 只会取最细的字重。标题和正文一样走 GDI，才是常规字重
        const TextFormatFlags Exact = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        float x = headingBounds.Left;
        int top = headingBounds.Top;
        foreach (char letter in heading)
        {
            TextRenderer.DrawText(graphics, letter.ToString(), title, new Point((int)x, top), NativeTheme.Text, Exact);
            x += (HeadingSize + HeadingSpacing) * scale;
        }

        x += 8 * scale;
        int titleHeight = TextRenderer.MeasureText(graphics, "听", title, Size.Empty, Exact).Height;
        int timeHeight = TextRenderer.MeasureText(graphics, "0", small, Size.Empty, Exact).Height;
        int timeTop = top + (int)((titleHeight - timeHeight) * TimeBaseline);
        int seconds = (int)spoken.Elapsed.TotalSeconds;
        foreach (char digit in $"{seconds / 60}:{seconds % 60:00}")
        {
            string text = digit.ToString();
            TextRenderer.DrawText(graphics, text, small, new Point((int)x, timeTop), NativeTheme.Peach, Exact);
            x += TextRenderer.MeasureText(graphics, text, small, Size.Empty, Exact).Width + TimeSpacing * scale;
        }
    }

    /// <summary>月亮外面一圈刻度，随音量点亮。月亮本身在玻璃上。</summary>
    private void DrawMoonLevel(Graphics graphics)
    {
        if (!graphics.IsVisible(rippleBounds)) return;
        float cx = rippleBounds.Left + rippleBounds.Width / 2F, cy = rippleBounds.Top + rippleBounds.Height / 2F;
        float level = MotionAllowed && !paused ? MathF.Sqrt(smoothLevel) : 0;
        int lit = (int)MathF.Round(level * MoonTicks);
        using var dim = new Pen(Color.FromArgb(61, 207, 220, 255), scale);
        using var bright = new Pen(Color.FromArgb(224, NativeTheme.Moon), scale);
        for (int tick = 0; tick < MoonTicks; tick++)
        {
            float angle = -MathF.PI / 2 + tick * MathF.PI * 2 / MoonTicks;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            bool on = tick < lit;
            float inner = MoonTickInner * scale, outer = (on ? MoonTickLit : MoonTickDim) * scale;
            graphics.DrawLine(on ? bright : dim, cx + cos * inner, cy + sin * inner, cx + cos * outer, cy + sin * outer);
        }
    }

    protected void DrawStatus(Graphics graphics, StringAlignment alignment = StringAlignment.Near)
    {
        if (NativeTheme.IsCrabapple)
        {
            // 宋体走 GDI 才是常规字重。出错时两行，放不下的部分省略
            using var serif = NativeTheme.TitleFont(StatusSize, scale);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                | (hasError ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, statusText, serif, statusBounds, hasError ? NativeTheme.Danger : NativeTheme.Bloom, flags);
            return;
        }

        using var font = NativeTheme.TextFont(12, scale);
        using var brush = new SolidBrush(hasError ? NativeTheme.Danger : NativeTheme.TextMuted);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, Alignment = alignment };
        graphics.DrawString(statusText, font, brush, statusBounds, format);
    }

    private void OnTextCommitted()
    {
        if (recording)
        {
            actions.TextEdited(transcript.Text);
            return;
        }

        ScheduleSave();
        OnEditedAfterFinish();
    }

    /// <summary>海棠：“正在听”前面一个花色小圆点，外面一圈随音量扩开。暂停时只剩淡淡的点，说完就不画。</summary>
    private void DrawListeningDot(Graphics graphics)
    {
        if (!recording || rippleBounds.IsEmpty || !graphics.IsVisible(rippleBounds)) return;
        float cx = rippleBounds.Left + rippleBounds.Width / 2F, cy = rippleBounds.Top + rippleBounds.Height / 2F;
        float dot = DotRadius * scale;
        if (paused)
        {
            using var faint = new SolidBrush(NativeTheme.Alpha(NativeTheme.Bloom, 110));
            graphics.FillEllipse(faint, cx - dot, cy - dot, dot * 2, dot * 2);
            return;
        }

        float level = MotionAllowed ? MathF.Sqrt(smoothLevel) : 0;
        float ring = dot + (DotRing + level * DotReach) * scale;
        using var halo = new SolidBrush(NativeTheme.Alpha(NativeTheme.Bloom, (int)(40 + 70 * level)));
        graphics.FillEllipse(halo, cx - ring, cy - ring, ring * 2, ring * 2);
        using var fill = new SolidBrush(NativeTheme.Bloom);
        graphics.FillEllipse(fill, cx - dot, cy - dot, dot * 2, dot * 2);
    }

    private void AdvanceRipple()
    {
        float target = clock.Elapsed.TotalSeconds - lastLevelAt > .18 ? 0 : targetLevel;
        smoothLevel += (target - smoothLevel) * .2F;
        if (target <= .003F && smoothLevel <= .003F) { smoothLevel = 0; rippleTimer.Stop(); }
        Invalidate(rippleBounds);
    }

    private void SyncRipple()
    {
        if (!recording || paused || !MotionAllowed || !Visible) { rippleTimer.Stop(); return; }
        if (targetLevel > .003F || smoothLevel > .003F) rippleTimer.Start();
    }

    private void AdvanceTime()
    {
        int seconds = (int)spoken.Elapsed.TotalSeconds;
        if (seconds == shownSeconds) return;
        shownSeconds = seconds;
        if (!NativeTheme.IsCrabapple) Invalidate(headingBounds);
    }

    private async Task RetryAsync()
    {
        retryButton.Enabled = false;
        transcript.ReadOnly = true;
        SetStatus("正在用保留的录音重新转写…");
        try
        {
            await SaveEditedTextAsync();
            QuanShuoRecord updated = await actions.Retry(record);
            transcript.ReplaceAll(updated.Text);
            SetFinished(updated);
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
            retryButton.Enabled = true;
        }
        finally
        {
            transcript.ReadOnly = false;
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr dc, int color);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool redraw);
}
