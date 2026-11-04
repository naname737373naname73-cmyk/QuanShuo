using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Transcription;

public static class LocalAsrSetupPrompt
{
    public static string Read(string? modelId = null) => Target(ReadResource("QuanShuo.LocalAsrAgentPrompt"), modelId, false);

    public static string ReadAssessment(string? modelId = null) => Target(ReadResource("QuanShuo.LocalAsrAssessmentPrompt"), modelId, true);

    private static string Target(string prompt, string? id, bool assessment)
    {
        if (id is null) return prompt;
        string name = LocalAsrCatalog.Name(id);
        string scope = assessment
            ? "本次只检查这个模型是否适合我的电脑，用简短的人话告诉我结论，不安装。"
            : "本次只安装并连接这个模型，已有文件优先复用，保留其他模型与当前选择。常规配置和排错自主完成，不要求方案摘要或逐步确认。";
        return prompt + "\n\n## 本次界面选择的模型\n\n" + "模型：" + name + "\n登记 ID：" + id + "\n\n" + scope + "\n";
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(LocalAsrSetupPrompt).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("本地识别提示词未打包，请更新圈说。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
