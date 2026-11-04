namespace QuanShuo.Native.Diagnostics;

public static class AppTrace
{
    private static readonly object Sync = new();
    private static readonly string? Path = Environment.GetEnvironmentVariable("QUANSHUO_TRACE_FILE");

    public static void Log(string stage)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        lock (Sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"{DateTimeOffset.Now:O} {stage}{Environment.NewLine}");
        }
    }
}
