using System.Text;
using QuanShuo.Core.Models;

namespace QuanShuo.Core.Copying;

public static class AiCopyFormatter
{
    public static string Format(QuanShuoRecord record)
    {
        var builder = new StringBuilder();
        AppendRecord(builder, record);
        return builder.ToString();
    }

    public static string FormatMany(IReadOnlyList<QuanShuoRecord> records)
    {
        if (records.Count == 1)
        {
            return Format(records[0]);
        }

        var builder = new StringBuilder();
        for (int index = 0; index < records.Count; index++)
        {
            if (index > 0)
            {
                builder.AppendLine();
            }

            builder.Append('[').Append(index + 1).AppendLine("]");
            AppendRecord(builder, records[index]);
        }

        return builder.ToString();
    }

    /// <summary>只要原话：每条之间空一行，没有文字的记录跳过。</summary>
    public static string JoinTexts(IReadOnlyList<QuanShuoRecord> records) =>
        string.Join("\n\n", records.Select(record => record.Text.Trim()).Where(text => text.Length > 0));

    private static void AppendRecord(StringBuilder builder, QuanShuoRecord record)
    {
        if (record.HasImage) builder.Append("[截图] ").AppendLine(QuotePath(record.ImagePath));
        builder.Append("[说明] ").AppendLine(record.Text);
        builder.Append("[时间] ").Append(record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("，来自 ").AppendLine(record.AppName);
    }

    private static string QuotePath(string path) => path.Contains(' ', StringComparison.Ordinal)
        ? "\"" + path + "\""
        : path;
}
