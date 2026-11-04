using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuanShuo.Core.Models;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Transcription;

/// <summary>同步 HTTP 转写：把整段 WAV 以 Base64 发给 qwen-audio-*-asr-flash。</summary>
public static class DashScopeFileTranscriber
{
    private const string GenerationPath = "/api/v1/services/aigc/multimodal-generation/generation";
    private const string NoSpeechDetail = "ASR_RESPONSE_HAVE_NO_WORDS";
    private const int WavHeaderBytes = 44;
    private const int PcmBytesPerSecond = 32000;
    private const double MinimumSeconds = .3;
    // 和流式一样先直连，开着代理时绕路太慢。连不上才走系统代理。
    private static readonly HttpClient Direct = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient ViaSystemProxy = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>沿用识别地址的域名，业务空间专属地址不用另外配置。</summary>
    public static Uri EndpointFor(string websocketEndpoint)
    {
        var endpoint = new Uri(websocketEndpoint);
        return new UriBuilder(Uri.UriSchemeHttps, endpoint.Host, endpoint.IsDefaultPort ? -1 : endpoint.Port, GenerationPath).Uri;
    }

    public static async Task<string> TranscribeAsync(byte[] wav, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        double seconds = Math.Max(0, wav.Length - WavHeaderBytes) / (double)PcmBytesPerSecond;
        // 几乎没有音频时服务端不返回也不报错，请求会一直挂着
        if (seconds < MinimumSeconds) return string.Empty;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30 + seconds));
        try
        {
            return await SendAsync(wav, options, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // 必须换成普通错误：调用方不处理取消，超时会让录音流程停在半路
            throw new TimeoutException("转写请求超时。");
        }
    }

    private static async Task<string> SendAsync(byte[] wav, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new
        {
            model = options.Model,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new
                            {
                                type = "input_audio",
                                input_audio = new { data = "data:audio/wav;base64," + Convert.ToBase64String(wav) },
                            },
                        },
                    },
                },
            },
            parameters = BuildParameters(options),
        });
        using HttpResponseMessage response = await PostAsync(body, options, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode) return ReadText(json);
        TranscriptionServiceException failure = ReadFailure(json, (int)response.StatusCode);
        // 音频里没有人声时同步接口报错，流式接口只是不返回文字。两边都按“没听到话”处理。
        if (failure.Detail.Contains(NoSpeechDetail, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        throw failure;
    }

    private static async Task<HttpResponseMessage> PostAsync(string body, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage first = NewRequest(body, options);
            return await Direct.SendAsync(first, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            using HttpRequestMessage second = NewRequest(body, options);
            return await ViaSystemProxy.SendAsync(second, cancellationToken);
        }
    }

    private static HttpRequestMessage NewRequest(string body, TranscriptionOptions options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointFor(options.Endpoint))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("X-DashScope-SSE", "disable");
        return request;
    }

    private static Dictionary<string, object> BuildParameters(TranscriptionOptions options)
    {
        var parameters = new Dictionary<string, object>
        {
            ["format"] = "wav",
            ["sample_rate"] = "16000",
            ["language_hints"] = new[] { options.Language },
        };
        if (!string.IsNullOrWhiteSpace(options.VocabularyId)) parameters["vocabulary_id"] = options.VocabularyId;
        return parameters;
    }

    private static string ReadText(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("output", out JsonElement output))
        {
            if (output.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String) return text.GetString() ?? string.Empty;
            if (output.TryGetProperty("sentence", out JsonElement sentence) && sentence.TryGetProperty("text", out JsonElement sentenceText))
            {
                return sentenceText.GetString() ?? string.Empty;
            }
        }

        throw new TranscriptionServiceException("UnexpectedResponse", "响应里没有识别文字");
    }

    private static TranscriptionServiceException ReadFailure(string json, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string code = root.TryGetProperty("code", out JsonElement codeElement) ? codeElement.GetString() ?? status.ToString() : status.ToString();
            string message = root.TryGetProperty("message", out JsonElement messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
            return new TranscriptionServiceException(code, message);
        }
        catch (JsonException)
        {
            return new TranscriptionServiceException(status.ToString(), "服务返回了无法解析的内容");
        }
    }
}
