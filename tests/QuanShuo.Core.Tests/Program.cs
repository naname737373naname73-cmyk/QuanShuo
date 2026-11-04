using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;

if (args is ["verify-delivery"])
{
    try { DeliveryVerification.Run(); return 0; }
    catch (Exception exception) { Console.WriteLine($"FAIL delivery: {exception.GetType().Name}: {exception.Message}"); return 1; }
}

// All data stays under this checkout; no application startup or user configuration.
string root = Path.GetFullPath(Path.Combine("artifacts", "core-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var tests = new (string Name, Func<Task> Run)[]
{
    ("atomic-text-concurrent", ConcurrentText),
    ("atomic-bytes-concurrent", ConcurrentBytes),
    ("atomic-cancel-preserves-original", CancelledWrite),
    ("atomic-waits-for-brief-reader", BriefReader),
    ("missing-record-save-must-fail", MissingRecord),
    ("markdown-image-adjacent-paragraph", AdjacentParagraph),
};
int failures = 0;
foreach (var test in tests.Where(test => args.Length == 0 || args.Contains(test.Name)))
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message} @ {exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
}
Console.WriteLine($"Failures: {failures}; synthetic files: {root}");
return failures == 0 ? 0 : 1;

async Task ConcurrentText()
{
    string path = Path.Combine(root, "concurrent.txt");
    string first = new('A', 512 * 1024), second = new('B', 512 * 1024);
    await Task.WhenAll(AtomicFile.WriteAllTextAsync(path, first), AtomicFile.WriteAllTextAsync(path, second));
    string saved = await File.ReadAllTextAsync(path);
    Assert(saved == first || saved == second, "Concurrent writers must publish one complete payload.");
    Assert(!Directory.EnumerateFiles(root, "concurrent.txt*.tmp").Any(), "Temporary text files remain.");
}

async Task ConcurrentBytes()
{
    string path = Path.Combine(root, "concurrent.bin");
    byte[] first = Enumerable.Repeat((byte)17, 1024 * 1024).ToArray();
    byte[] second = Enumerable.Repeat((byte)29, 1024 * 1024).ToArray();
    await Task.WhenAll(AtomicFile.WriteAllBytesAsync(path, first), AtomicFile.WriteAllBytesAsync(path, second));
    byte[] saved = await File.ReadAllBytesAsync(path);
    Assert(saved.SequenceEqual(first) || saved.SequenceEqual(second), "Concurrent writers mixed or truncated bytes.");
    Assert(!Directory.EnumerateFiles(root, "concurrent.bin*.tmp").Any(), "Temporary binary files remain.");
}

async Task CancelledWrite()
{
    string path = Path.Combine(root, "cancelled.txt");
    await File.WriteAllTextAsync(path, "original");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await AtomicFile.WriteAllTextAsync(path, "replacement", cancelled.Token); throw new Exception("Cancellation was ignored."); }
    catch (OperationCanceledException) { }
    Assert(await File.ReadAllTextAsync(path) == "original", "Cancellation replaced the destination.");
    Assert(!Directory.EnumerateFiles(root, "cancelled.txt*.tmp").Any(), "Cancelled write left a temporary file.");
}

// The index sync reads record files with FileShare.Read, which blocks replacing the file.
async Task BriefReader()
{
    string path = Path.Combine(root, "reader.md");
    await File.WriteAllTextAsync(path, "original");
    var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    Task save = AtomicFile.WriteAllTextAsync(path, "replacement");
    await Task.Delay(100);
    await reader.DisposeAsync();
    await save;
    Assert(await File.ReadAllTextAsync(path) == "replacement", "A save failed because another reader held the file for a moment.");
    Assert(!Directory.EnumerateFiles(root, "reader.md*.tmp").Any(), "Temporary file remains after waiting for a reader.");
}

async Task MissingRecord()
{
    var store = new RecordStore(Path.Combine(root, "missing"));
    var record = await store.CreateTextAsync(new CaptureMetadata
    {
        Timestamp = new DateTimeOffset(2026, 11, 3, 12, 34, 56, TimeSpan.FromHours(8)),
        AppName = "Synthetic", WindowTitle = "Synthetic",
    }, TranscriptionStatus.Incomplete);
    File.Delete(record.MarkdownPath);
    Task save = store.SaveTextAsync(record, "must stay in draft");
    await save;
    Assert(save is Task<bool> result && !result.Result, "Missing record was acknowledged as saved, so the client would discard its draft.");
    Assert(!File.Exists(record.MarkdownPath), "A missing record must not be recreated with invented metadata.");
}

Task AdjacentParagraph()
{
    string path = Path.Combine(root, "123456.md");
    const string header = "---\ntime: 2026-11-03T12:34:56+08:00\nimage: \"123456.png\"\nstatus: completed\n---\n";
    const string body = "first paragraph\n\nsecond paragraph";
    foreach (string newline in new[] { "\n", "\r\n" })
    {
        var parsed = MarkdownRecordFile.Parse(path, (header + "![screenshot](123456.png)\n" + body + "\n").Replace("\n", newline));
        Assert(parsed.Text == body, "The first paragraph after an image was silently removed.");
        var roundtrip = MarkdownRecordFile.Parse(path, MarkdownRecordFile.Serialize(parsed));
        Assert(roundtrip.Text == body, "Lost text remained lost after saving.");
        Assert(MarkdownRecordFile.Parse(path, header + "![screenshot](123456.png)").Text == "", "Image-only legacy record leaked image syntax.");
        Assert(MarkdownRecordFile.Parse(path, header.Replace("123456.png", "") + "![user text]\n").Text == "![user text]", "Text-only record lost user Markdown.");
    }
    return Task.CompletedTask;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
