namespace QuanShuo.Core.Transcription;

/// <summary>识别服务明确返回的错误。Code 是服务端错误码，界面据此给出大白话提示。</summary>
public sealed class TranscriptionServiceException(string code, string detail, Exception? innerException = null)
    : Exception($"识别服务返回 {code}：{detail}", innerException)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}
