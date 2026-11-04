namespace QuanShuo.Native.History;

public sealed class RecordWatcher : IDisposable
{
    private readonly FileSystemWatcher watcher;
    // FileSystemWatcher 回调在线程池线程触发，WinForms Timer 跨线程 Start 会静默失效，
    // 手动修改 md 后索引因此从不刷新。System.Threading.Timer 可跨线程安全使用。
    private readonly System.Threading.Timer debounce;

    public RecordWatcher(string directory)
    {
        watcher = new FileSystemWatcher(directory, "*.*")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        debounce = new System.Threading.Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);
        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileChanged;
    }

    public event EventHandler? Changed;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        string extension = Path.GetExtension(e.FullPath);
        if (extension is not (".md" or ".png" or ".wav")) return;
        debounce.Change(450, Timeout.Infinite);
    }

    public void Dispose()
    {
        debounce.Dispose();
        watcher.Dispose();
    }
}
