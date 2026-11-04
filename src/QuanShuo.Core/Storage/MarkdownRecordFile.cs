using System.Globalization;
using System.Text;
using QuanShuo.Core.Models;

namespace QuanShuo.Core.Storage;

public static class MarkdownRecordFile
{
    public static string Serialize(QuanShuoRecord record)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.Append("time: ").AppendLine(record.Timestamp.ToString("o", CultureInfo.InvariantCulture));
        builder.Append("app: ").AppendLine(Quote(record.AppName));
        builder.Append("window: ").AppendLine(Quote(record.WindowTitle));
        builder.Append("image: ").AppendLine(Quote(Path.GetFileName(record.ImagePath)));
        builder.Append("status: ").AppendLine(StatusToString(record.Status));
        builder.AppendLine("---");
        if (record.HasImage)
        {
            builder.Append("![截图](").Append(Path.GetFileName(record.ImagePath)).AppendLine(")");
            builder.AppendLine();
        }

        builder.AppendLine(record.Text);
        return builder.ToString();
    }

    public static QuanShuoRecord Parse(string markdownPath, string content)
    {
        string id = Path.GetFileNameWithoutExtension(markdownPath);
        string directory = Path.GetDirectoryName(markdownPath) ?? string.Empty;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text;

        if (content.StartsWith("---\n", StringComparison.Ordinal) ||
            content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
            int end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end >= 0)
            {
                string frontmatter = normalized[4..end];
                foreach (string line in frontmatter.Split('\n'))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        metadata[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
                    }
                }

                text = normalized[(end + 5)..];
                // 纯语音记录没有图片行，用户的话以 ![ 开头时不能被当成图片语法吃掉
                bool hasImageLine = metadata.GetValueOrDefault("image", "x").Length > 0;
                if (hasImageLine && text.StartsWith("![", StringComparison.Ordinal))
                {
                    // 只截图没说话的记录，图片行后面没有空行也没有正文。
                    // 不能要求一定有空行，否则图片语法会被当成说明文字，还会复制给 AI。
                    int firstLineEnd = text.IndexOf('\n');
                    text = firstLineEnd >= 0 ? text[(firstLineEnd + 1)..]
                        : string.Empty;
                    if (text.StartsWith('\n')) text = text[1..];
                }
            }
            else
            {
                text = content;
            }
        }
        else
        {
            text = content;
        }

        string imageName = metadata.GetValueOrDefault("image", id + ".png");
        DateTimeOffset timestamp = DateTimeOffset.TryParse(
            metadata.GetValueOrDefault("time"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : File.GetCreationTime(markdownPath);

        string audioPath = Path.ChangeExtension(markdownPath, ".wav");
        return new QuanShuoRecord
        {
            Id = id,
            Timestamp = timestamp,
            AppName = metadata.GetValueOrDefault("app", "未知程序"),
            WindowTitle = metadata.GetValueOrDefault("window", string.Empty),
            ImagePath = imageName.Length == 0 ? string.Empty : Path.Combine(directory, imageName),
            MarkdownPath = markdownPath,
            AudioPath = File.Exists(audioPath) ? audioPath : null,
            Text = text.TrimEnd(),
            Status = ParseStatus(metadata.GetValueOrDefault("status")),
        };
    }

    public static async Task<bool> UpdateTextAsync(
        string markdownPath,
        string text,
        TranscriptionStatus? status,
        CancellationToken cancellationToken = default)
    {
        // 记录已被删除时不能写：解析空内容得到的是一条没有时间和来源的假记录
        if (!File.Exists(markdownPath)) return false;
        string existing = await File.ReadAllTextAsync(markdownPath, cancellationToken);
        var record = Parse(markdownPath, existing);
        var updated = new QuanShuoRecord
        {
            Id = record.Id,
            Timestamp = record.Timestamp,
            AppName = record.AppName,
            WindowTitle = record.WindowTitle,
            ImagePath = record.ImagePath,
            MarkdownPath = record.MarkdownPath,
            AudioPath = record.AudioPath,
            Text = text,
            Status = status ?? record.Status,
        };
        await AtomicFile.WriteAllTextAsync(markdownPath, Serialize(updated), cancellationToken);
        return true;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return value;
    }

    private static string StatusToString(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.NotRequested => "not-requested",
        TranscriptionStatus.Recording => "recording",
        TranscriptionStatus.Completed => "completed",
        TranscriptionStatus.Incomplete => "incomplete",
        _ => "not-requested",
    };

    private static TranscriptionStatus ParseStatus(string? value) => value switch
    {
        "recording" => TranscriptionStatus.Recording,
        "completed" => TranscriptionStatus.Completed,
        "incomplete" => TranscriptionStatus.Incomplete,
        _ => TranscriptionStatus.NotRequested,
    };
}
