using QuanShuo.Core.Models;

namespace QuanShuo.Core.Storage;

public sealed class RecordStore
{
    private readonly SemaphoreSlim createLock = new(1, 1);

    public RecordStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    public async Task<QuanShuoRecord> CreateAsync(
        CaptureMetadata metadata,
        Func<string, CancellationToken, Task> writeImageAsync,
        string initialText,
        TranscriptionStatus status,
        CancellationToken cancellationToken = default)
    {
        await createLock.WaitAsync(cancellationToken);
        try
        {
            string directory = Path.Combine(DataDirectory, metadata.Timestamp.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);
            string id = AllocateId(directory, metadata.Timestamp);
            string imagePath = Path.Combine(directory, id + ".png");
            string markdownPath = Path.Combine(directory, id + ".md");
            var record = new QuanShuoRecord
            {
                Id = id,
                Timestamp = metadata.Timestamp,
                AppName = metadata.AppName,
                WindowTitle = metadata.WindowTitle,
                ImagePath = imagePath,
                MarkdownPath = markdownPath,
                Text = initialText,
                Status = status,
            };

            string temporaryImage = imagePath + ".tmp";
            await writeImageAsync(temporaryImage, cancellationToken);
            File.Move(temporaryImage, imagePath, true);
            await AtomicFile.WriteAllTextAsync(markdownPath, MarkdownRecordFile.Serialize(record), cancellationToken);
            return record;
        }
        finally
        {
            createLock.Release();
        }
    }

    /// <summary>新建一条没有截图的记录，用于单独说话。</summary>
    public async Task<QuanShuoRecord> CreateTextAsync(
        CaptureMetadata metadata,
        TranscriptionStatus status,
        CancellationToken cancellationToken = default)
    {
        await createLock.WaitAsync(cancellationToken);
        try
        {
            string directory = Path.Combine(DataDirectory, metadata.Timestamp.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);
            string id = AllocateId(directory, metadata.Timestamp);
            var record = new QuanShuoRecord
            {
                Id = id,
                Timestamp = metadata.Timestamp,
                AppName = metadata.AppName,
                WindowTitle = metadata.WindowTitle,
                ImagePath = string.Empty,
                MarkdownPath = Path.Combine(directory, id + ".md"),
                Status = status,
            };
            await AtomicFile.WriteAllTextAsync(record.MarkdownPath, MarkdownRecordFile.Serialize(record), cancellationToken);
            return record;
        }
        finally
        {
            createLock.Release();
        }
    }

    /// <summary>删掉一条什么都没说的记录，不进回收站。</summary>
    public void Discard(QuanShuoRecord record)
    {
        File.Delete(record.MarkdownPath);
        File.Delete(GetAudioPath(record));
    }

    public string GetAudioPath(QuanShuoRecord record) => Path.ChangeExtension(record.MarkdownPath, ".wav");

    public async Task<IReadOnlyList<QuanShuoRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DataDirectory))
        {
            return [];
        }

        var records = new List<QuanShuoRecord>();
        foreach (string path in Directory.EnumerateFiles(DataDirectory, "*.md", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(DataDirectory, path).StartsWith(".", StringComparison.Ordinal) &&
                                  !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string content = await File.ReadAllTextAsync(path, cancellationToken);
                var record = MarkdownRecordFile.Parse(path, content);
                if (!record.HasImage || File.Exists(record.ImagePath))
                {
                    records.Add(record);
                }
            }
            catch (IOException)
            {
                // A concurrent writer may still own this file; the next index pass will retry it.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible record must not prevent the remaining history from opening.
            }
        }

        return records.OrderByDescending(record => record.Timestamp).ThenByDescending(record => record.Id).ToArray();
    }

    /// <returns>False if the record was deleted; callers that acknowledge a save must check this result.</returns>
    public Task<bool> SaveTextAsync(
        QuanShuoRecord record,
        string text,
        TranscriptionStatus? status = null,
        CancellationToken cancellationToken = default) =>
        MarkdownRecordFile.UpdateTextAsync(record.MarkdownPath, text, status, cancellationToken);

    private static string AllocateId(string directory, DateTimeOffset timestamp)
    {
        string baseId = timestamp.ToString("HHmmss");
        string id = baseId;
        for (int suffix = 2; File.Exists(Path.Combine(directory, id + ".md")) ||
                             File.Exists(Path.Combine(directory, id + ".png")) ||
                             File.Exists(Path.Combine(directory, id + ".wav")); suffix++)
        {
            id = baseId + "-" + suffix;
        }

        return id;
    }
}
