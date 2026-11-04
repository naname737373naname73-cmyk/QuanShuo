using System.Net.Http;
using System.Net.WebSockets;
using NAudio;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Transcription;

public static class TranscriptionErrors
{
    private const string Kept = "截图、已有文字和 WAV 都没有丢失。";

    public static string FriendlyMessage(Exception exception)
    {
        if (Find<TranscriptionServiceException>(exception) is { } service) return ForService(service);
        if (Find<MmException>(exception) is not null || Contains(exception, "麦克风"))
        {
            return "麦克风没有打开。请检查 Windows 隐私设置和输入设备；截图与已有文字仍在。";
        }

        if (Find<WebSocketException>(exception) is not null || Find<IOException>(exception) is not null ||
            Find<HttpRequestException>(exception) is not null || Find<TimeoutException>(exception) is not null)
        {
            return "识别连接中断了。请检查网络后点“重新转写”；已有文字和 WAV 都保留在本机。";
        }

        return "转写没有完成：" + exception.Message + "。已有文字和 WAV 都保留，可以稍后重试。";
    }

    private static string ForService(TranscriptionServiceException exception)
    {
        string code = exception.Code;
        if (code.StartsWith("Local", StringComparison.Ordinal) || code == "AudioBacklog")
            return exception.Detail + " 已有文字和 WAV 保留在本机，不会自动转到云端。";
        if (Matches(code, "UrlOnlyModel")) return exception.Detail;
        if (Matches(code, "InvalidApiKey", "Unauthorized", "401", "403"))
        {
            return "阿里云拒绝了 API Key。请在设置中重新填写并测试连接；" + Kept;
        }

        if (Matches(code, "ModelNotFound", "Model.NotFound", "AccessDenied", "Model.AccessDenied"))
        {
            return "这个模型现在用不了：账号没有开通，或者模型名不对。请在设置里换一个模型；" + Kept;
        }

        if (Matches(code, "Arrearage", "Overdue", "QuotaExceeded", "BillingDisabled", "AllocationQuota"))
        {
            return "阿里云账号欠费或额度用完了。充值后点“重新转写”；" + Kept;
        }

        if (Matches(code, "Throttling", "RateLimit", "429"))
        {
            return "阿里云说请求太频繁了。稍等一下再点“重新转写”；" + Kept;
        }

        // 服务端太久没收到音频时主动断开，通常是线路卡住了
        if (exception.Detail.Contains("request timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "网络太慢，阿里云有一段时间没收到录音，转写断了。网络好了点“重新转写”；" + Kept;
        }

        return $"转写没有完成（{code}：{exception.Detail}）。{Kept}";
    }

    private static bool Matches(string code, params string[] names) =>
        names.Any(name => code.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match) return match;
        }

        return null;
    }

    private static bool Contains(Exception exception, string text)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(text, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
