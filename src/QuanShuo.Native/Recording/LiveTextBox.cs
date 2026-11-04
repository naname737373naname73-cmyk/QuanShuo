using System.Runtime.InteropServices;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Recording;

/// <summary>
/// 录音时可以随便改的文字框。识别结果只替换“正在修订的那一段”，不重设全文，
/// 所以光标、选区和用户正在看的位置都不会被打断。
/// </summary>
public sealed class LiveTextBox : TextBox
{
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const int WmSetRedraw = 0x000B;
    private const int WmSetText = 0x000C;
    private const int WmPaint = 0x000F;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int WmImeStartComposition = 0x010D;
    private const int WmImeEndComposition = 0x010E;
    private const int WmImeComposition = 0x010F;
    private const int WmTimer = 0x0113;
    private const int WmVScroll = 0x0115;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmMouseWheel = 0x020A;
    private const int WmCut = 0x0300;
    private const int WmPaste = 0x0302;
    private const int WmClear = 0x0303;
    private const int WmUndo = 0x0304;
    private const int EmSetSel = 0x00B1;
    private const int EmLineScroll = 0x00B6;
    private const int EmScrollCaret = 0x00B7;
    private const int EmReplaceSel = 0x00C2;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const char ControlBackspaceChar = '\x7f';
    private const int SrcCopy = 0x00CC0020;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwNoErase = 0x0020;
    private const uint RdwUpdateNow = 0x0100;
    private readonly LiveTranscript live = new();
    private string shadow = string.Empty;
    private bool applying;
    private bool composing;
    private bool selecting;
    private bool redrawHeld;
    private (string Text, bool SentenceEnd)? deferred;
    private int wheelRemainder;
    private IntPtr backdrop;

    public LiveTextBox()
    {
        Multiline = true;
        AcceptsReturn = true;
        ScrollBars = ScrollBars.None;
        BorderStyle = BorderStyle.None;
        // 默认上限约 3 万字，长录音会写不进去
        MaxLength = 0;
    }

    /// <summary>
    /// 文字直接写在这张图上。系统编辑框的事一件不少（语音插入、光标、选区、输入法），
    /// 只是让它画进一块已经铺好背景的缓冲。传 null 回到纯色背景。
    /// 父窗口要在 WM_CTLCOLOREDIT 里返回空画刷，否则编辑框会先铺一遍底色。
    /// </summary>
    public void SetBackdrop(Bitmap? slice)
    {
        ReleaseBackdrop();
        if (slice is not null) backdrop = slice.GetHbitmap(Color.Black);
        if (IsHandleCreated) Invalidate();
    }

    public bool HasBackdrop => backdrop != IntPtr.Zero;

    /// <summary>文字变了且需要保存：用户改的，或延后合并的识别结果。</summary>
    public event EventHandler? Committed;

    /// <summary>并入一次识别结果，返回合并后的全文。</summary>
    public string Merge(string recognized, bool sentenceEnd)
    {
        // 输入法组字和拖选时不能动文字，否则候选词和选区都会被打断。识别结果是全文，只留最新一次。
        if (composing || selecting) deferred = (recognized, sentenceEnd);
        else Apply(recognized, sentenceEnd);
        return Text;
    }

    /// <summary>把组字或拖选时攒下的识别结果写进去。</summary>
    public void FlushDeferred()
    {
        if (deferred is not { } pending) return;
        deferred = null;
        Apply(pending.Text, pending.SentenceEnd);
    }

    /// <summary>整段换掉文字，用于重新转写的结果。</summary>
    public void ReplaceAll(string text)
    {
        applying = true;
        Text = text.ReplaceLineEndings("\r\n");
        applying = false;
        shadow = Text;
        SelectionStart = TextLength;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (applying) return;
        string current = Text;
        int limit = Math.Min(shadow.Length, current.Length);
        int prefix = 0;
        while (prefix < limit && shadow[prefix] == current[prefix]) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix && shadow[^(suffix + 1)] == current[^(suffix + 1)]) suffix++;
        live.NoteUserEdit(prefix, shadow.Length - prefix - suffix, current.Length - prefix - suffix);
        shadow = current;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        // Ctrl+退格在系统编辑框里会打出一个方块字符
        if (e.KeyChar == ControlBackspaceChar) e.Handled = true;
        base.OnKeyPress(e);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.A))
        {
            SelectAll();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) selecting = true;
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) EndSelecting();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        EndSelecting();
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseBackdrop();
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message m)
    {
        if (backdrop != IntPtr.Zero)
        {
            if (m.Msg == WmEraseBkgnd) { m.Result = new IntPtr(1); return; }
            if (m.Msg == WmPaint) { PaintOverBackdrop(); m.Result = IntPtr.Zero; return; }
        }

        if (m.Msg == WmMouseWheel)
        {
            // 隐藏了滚动条，滚轮自己处理。高精度滚轮和触控板的增量小于 120，要累计，否则整数除法直接归零。
            wheelRemainder += (short)((m.WParam.ToInt64() >> 16) & 0xffff);
            int lines = wheelRemainder / 40;
            wheelRemainder -= lines * 40;
            if (lines != 0) SendMessage(Handle, EmLineScroll, IntPtr.Zero, new IntPtr(-lines));
            m.Result = IntPtr.Zero;
            RepaintWhole();
            return;
        }

        if (m.Msg == WmImeStartComposition) composing = true;
        base.WndProc(ref m);
        if (m.Msg == WmImeEndComposition)
        {
            composing = false;
            FlushAndCommit();
        }

        // 编辑框这些时候直接往屏幕上画，滚动时连背景一起搬走，所以画完要整块重来
        if (DrawsDirectly(m.Msg)) RepaintWhole();
    }

    private bool DrawsDirectly(int message) => message switch
    {
        WmSetFocus or WmKillFocus or WmSetText or WmKeyDown or WmKeyUp or WmChar or WmImeComposition or WmImeEndComposition => true,
        WmVScroll or WmLButtonDown or WmLButtonUp or WmCut or WmPaste or WmClear or WmUndo => true,
        EmSetSel or EmLineScroll or EmScrollCaret or EmReplaceSel => true,
        // 拖选超出边界时，编辑框靠定时器一边滚动一边选
        WmMouseMove or WmTimer => selecting,
        _ => false,
    };

    private void RepaintWhole()
    {
        if (backdrop == IntPtr.Zero || redrawHeld || !IsHandleCreated) return;
        RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwNoErase | RdwUpdateNow);
    }

    private void PaintOverBackdrop()
    {
        var paint = new PaintStruct();
        IntPtr screen = BeginPaint(Handle, ref paint);
        int width = ClientSize.Width, height = ClientSize.Height;
        IntPtr buffer = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr oldBitmap = SelectObject(buffer, bitmap);
        IntPtr source = CreateCompatibleDC(screen);
        IntPtr oldSource = SelectObject(source, backdrop);
        BitBlt(buffer, 0, 0, width, height, source, 0, 0, SrcCopy);
        SelectObject(source, oldSource);
        DeleteDC(source);
        // wParam 里放了 DC，编辑框就画进这里，不再自己 BeginPaint
        var inner = Message.Create(Handle, WmPaint, buffer, IntPtr.Zero);
        DefWndProc(ref inner);
        BitBlt(screen, 0, 0, width, height, buffer, 0, 0, SrcCopy);
        SelectObject(buffer, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(buffer);
        EndPaint(Handle, ref paint);
    }

    private void ReleaseBackdrop()
    {
        if (backdrop == IntPtr.Zero) return;
        DeleteObject(backdrop);
        backdrop = IntPtr.Zero;
    }

    private void EndSelecting()
    {
        if (!selecting) return;
        selecting = false;
        FlushAndCommit();
    }

    private void FlushAndCommit()
    {
        if (deferred is null) return;
        FlushDeferred();
        Committed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(string recognized, bool sentenceEnd)
    {
        int caret = SelectionStart + SelectionLength;
        TextSplice? splice = live.Recognize(recognized, sentenceEnd, caret, TextLength);
        if (splice is { } change) Splice(change);
    }

    private void Splice(TextSplice change)
    {
        int selectionStart = SelectionStart, selectionLength = SelectionLength;
        int selectionEnd = selectionStart + selectionLength;
        int spliceEnd = change.Start + change.Length;
        // 光标停在这段末尾，说明用户没动过，就跟着文字走并滚动到底。否则保持用户看的位置。
        bool following = selectionLength == 0 && selectionEnd == spliceEnd;
        int firstVisibleLine = (int)SendMessage(Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);

        int Map(int position)
        {
            if (following) return change.Start + change.Text.Length;
            if (position <= change.Start) return position;
            if (position >= spliceEnd) return position + change.Text.Length - change.Length;
            return Math.Min(position, change.Start + change.Text.Length);
        }

        applying = true;
        redrawHeld = true;
        SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            SendMessage(Handle, EmSetSel, new IntPtr(change.Start), new IntPtr(spliceEnd));
            SendMessage(Handle, EmReplaceSel, IntPtr.Zero, change.Text);
            SendMessage(Handle, EmSetSel, new IntPtr(Map(selectionStart)), new IntPtr(Map(selectionEnd)));
            if (following)
            {
                SendMessage(Handle, EmScrollCaret, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                int moved = (int)SendMessage(Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero) - firstVisibleLine;
                if (moved != 0) SendMessage(Handle, EmLineScroll, IntPtr.Zero, new IntPtr(-moved));
            }
        }
        finally
        {
            SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            redrawHeld = false;
            Invalidate();
            RepaintWhole();
            applying = false;
        }

        shadow = Text;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public int Left, Top, Right, Bottom;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, int operation);
}
