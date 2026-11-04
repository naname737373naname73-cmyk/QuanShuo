using QuanShuo.Core.Copying;
using QuanShuo.Core.Models;

namespace QuanShuo.Native.Shell;

public static class ClipboardService
{
    public static void CopyForAI(IReadOnlyList<QuanShuoRecord> records) => CopyText(AiCopyFormatter.FormatMany(records));

    public static void CopyText(string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        Clipboard.SetDataObject(data, true, 5, 100);
    }

    public static void CopyImage(string path)
    {
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, bitmap);
        data.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath(path) });
        Clipboard.SetDataObject(data, true, 5, 100);
    }

    public static void CopyFilePath(string path) => CopyText(Path.GetFullPath(path));
}
