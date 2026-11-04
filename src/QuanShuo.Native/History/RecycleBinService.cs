using Microsoft.VisualBasic.FileIO;
using QuanShuo.Core.Models;

namespace QuanShuo.Native.History;

public sealed class RecycleBinService
{
    private readonly string undoRoot;
    private readonly Stack<UndoOperation> undoStack = new();

    public RecycleBinService(string dataDirectory)
    {
        undoRoot = Path.Combine(dataDirectory, ".quanshuo-trash");
    }

    public Task DeleteAsync(QuanShuoRecord record, CancellationToken cancellationToken = default) => DeleteAsync([record], cancellationToken);

    /// <summary>一次删多条，撤销时一起回来。</summary>
    public Task DeleteAsync(IReadOnlyList<QuanShuoRecord> records, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            string[] paths = records
                .SelectMany(record => new[] { record.ImagePath, record.MarkdownPath, record.AudioPath })
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0) return;
            string operationDirectory = Path.Combine(undoRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationDirectory);
            // 不同日期的记录文件名可能相同，备份不能只用文件名
            var entries = new UndoEntry[paths.Length];
            for (int index = 0; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string backup = Path.Combine(operationDirectory, index + "-" + Path.GetFileName(paths[index]));
                File.Copy(paths[index], backup, false);
                entries[index] = new UndoEntry(paths[index], backup);
            }

            var deleted = new List<UndoEntry>();
            try
            {
                foreach (UndoEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileSystem.DeleteFile(entry.Original, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                    deleted.Add(entry);
                }
            }
            finally
            {
                // 中途失败时，已经删掉的那些也要能撤销
                if (deleted.Count > 0) undoStack.Push(new UndoOperation(operationDirectory, deleted));
            }
        }, cancellationToken);
    }

    public Task<bool> UndoAsync(CancellationToken cancellationToken = default)
    {
        if (undoStack.Count == 0) return Task.FromResult(false);
        UndoOperation operation = undoStack.Pop();
        return Task.Run(() =>
        {
            try
            {
                foreach ((string original, string backup) in operation.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(original)) throw new IOException("原位置已经有同名文件，未覆盖：" + original);
                    string? directory = Path.GetDirectoryName(original);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    if (TryRestoreFromRecycleBin(original)) continue;
                    if (!File.Exists(backup)) throw new FileNotFoundException("回收站和撤销副本中都找不到这个文件：" + original, original);
                    File.Copy(backup, original, false);
                }

                Directory.Delete(operation.Directory, true);
                return true;
            }
            catch
            {
                undoStack.Push(operation);
                throw;
            }
        }, cancellationToken);
    }

    private static bool TryRestoreFromRecycleBin(string original)
    {
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic recycleBin = shell.NameSpace(10)!;
        string? source = null;
        string expectedName = Path.GetFileNameWithoutExtension(original);
        string expectedExtension = Path.GetExtension(original);
        string expectedDirectory = Path.GetDirectoryName(original) ?? string.Empty;
        foreach (dynamic item in recycleBin.Items())
        {
            string name = Convert.ToString(item.Name) ?? string.Empty;
            if (!string.Equals(Path.GetFileNameWithoutExtension(name), expectedName, StringComparison.OrdinalIgnoreCase)) continue;
            string itemPath = Convert.ToString(item.Path) ?? string.Empty;
            if (!string.Equals(Path.GetExtension(itemPath), expectedExtension, StringComparison.OrdinalIgnoreCase)) continue;
            string deletedFrom = Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom")) ?? string.Empty;
            if (deletedFrom.Length > 0 && !string.Equals(deletedFrom, expectedDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            if (source is not null) return false;
            source = itemPath;
        }

        if (source is null || !File.Exists(source)) return false;
        File.Move(source, original, false);
        return true;
    }

    private sealed record UndoEntry(string Original, string Backup);

    private sealed record UndoOperation(string Directory, IReadOnlyList<UndoEntry> Entries);
}
