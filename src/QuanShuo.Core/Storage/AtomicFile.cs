using System.Collections.Concurrent;
using System.Text;

namespace QuanShuo.Core.Storage;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublishGates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync();
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            await PublishAsync(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            await PublishAsync(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    // 两次保存同时替换同一个文件时，Windows 会拒绝后到的那次；别的线程、索引或杀毒软件
    // 正在读这个文件时也一样。同一路径的替换排队，短暂占用就稍等再试，一直占用才报错。
    private static async Task PublishAsync(string temporaryPath, string path)
    {
        SemaphoreSlim gate = PublishGates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, true);
                    return;
                }
                catch (Exception exception) when (attempt < 5 && exception is UnauthorizedAccessException or IOException)
                {
                    await Task.Delay(20 << attempt);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // 替换成功后临时文件已经不在；失败时清理不掉也不能盖住真正的错误
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
