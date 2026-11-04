using QuanShuo.Core.Configuration;

namespace QuanShuo.Core.Transcription;

public static class SpeechProviders
{
    public const string DashScope = "dashscope";
    public const string Local = "local";

    public static bool IsLocal(AppSettings settings) => settings.SpeechProvider == Local;

    public static void Validate(AppSettings settings)
    {
        if (settings.SpeechProvider is not (DashScope or Local))
            throw new ArgumentException("请选择阿里云或本地服务作为识别来源。");
        if (IsLocal(settings))
        {
            LocalEndpoint(settings.LocalSpeechEndpoint);
            if (string.IsNullOrWhiteSpace(settings.LocalSpeechModel))
                throw new ArgumentException("请填写本地服务的模型或配置名称；使用服务默认配置时填 default。");
        }
        else if (!Uri.TryCreate(settings.SpeechEndpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme != "wss")
            throw new ArgumentException("阿里云识别地址必须是有效的 wss:// 地址。");
    }

    public static Uri LocalEndpoint(string endpoint)
    {
        // 本地模式明确只访问回环地址，避免代理或配置错误将音频发往公网。
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("ws" or "wss") || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("本地识别地址需使用 ws:// 或 wss://，主机仅支持 localhost、127.0.0.1 或 [::1]。");
        return uri;
    }
}
