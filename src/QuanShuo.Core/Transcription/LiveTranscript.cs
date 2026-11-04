namespace QuanShuo.Core.Transcription;

/// <summary>把文档里 [Start, Start+Length) 换成 Text。</summary>
public readonly record struct TextSplice(int Start, int Length, string Text);

/// <summary>
/// 让识别结果和用户的修改共用一份文字。
/// 识别服务每次给的是从头到现在的全文，最后一句还会被修订；用户又随时可能改任何地方。
/// 这里只记住“正在修订的那一段”在文档里的位置，别的文字一律不碰：
/// 改了别处，那段跟着平移；改到那段本身，到此为止的文字归用户，之后只追加新说的部分。
/// 一句说完后，下一句写到光标所在的位置，和输入法语音输入一样。
/// </summary>
public sealed class LiveTranscript
{
    private const int MaxRealignWindow = 6;
    private string handedOver = string.Empty;
    private string lastRecognized = string.Empty;
    private string liveText = string.Empty;
    private int liveStart;

    /// <summary>用户动过文字之后为 true。</summary>
    public bool UserEdited { get; private set; }

    /// <summary>
    /// 应用一次识别结果。没有变化时返回 null。
    /// caret 只在这一段刚开始时用来定位，之后位置由 <see cref="NoteUserEdit"/> 维护。
    /// </summary>
    public TextSplice? Recognize(string recognized, bool sentenceEnd, int caret, int documentLength)
    {
        lastRecognized = recognized;
        string tail = recognized[AlignedLength(handedOver, recognized)..];
        if (liveText.Length == 0) liveStart = Math.Clamp(caret, 0, documentLength);
        TextSplice? splice = tail == liveText ? null : new TextSplice(liveStart, liveText.Length, tail);
        liveText = tail;
        if (sentenceEnd) HandOver();
        return splice;
    }

    /// <summary>用户把 [start, start+removed) 换成了 inserted 个字符。</summary>
    public void NoteUserEdit(int start, int removed, int inserted)
    {
        UserEdited = true;
        if (liveText.Length == 0) return;
        int end = start + removed, liveEnd = liveStart + liveText.Length;
        if (end <= liveStart) liveStart += inserted - removed;
        else if (start < liveEnd) HandOver();
    }

    private void HandOver()
    {
        handedOver = lastRecognized;
        liveStart += liveText.Length;
        liveText = string.Empty;
    }

    /// <summary>
    /// 找出 next 里已经交给用户的部分有多长。
    /// 冻结的如果是半句话，服务端之后可能又修订了它，这时用末尾几个字重新定位。
    /// 定位不到时宁可重复一两个字，也不能吞掉新说的话。
    /// </summary>
    private static int AlignedLength(string frozen, string next)
    {
        if (next.StartsWith(frozen, StringComparison.Ordinal)) return frozen.Length;
        for (int window = Math.Min(MaxRealignWindow, frozen.Length); window >= 2; window--)
        {
            int found = next.LastIndexOf(frozen[^window..], StringComparison.Ordinal);
            if (found >= 0) return found + window;
        }

        return Math.Min(frozen.Length, next.Length);
    }
}
