using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace QuanShuo.Installer;

public static class InstallerLogic
{
    private const string AppFolder = "QuanShuo";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuanShuo";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValue = "QuanShuo";
    private const int CopyBufferSize = 1024 * 1024;
    private const int ReplaceAttempts = 25;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly string[] LocalPromptFiles = ["local-asr-assessment-prompt.md", "local-asr-agent-prompt.md"];
    private static readonly string[] DistributionDocuments = ["README.md", "LICENSE", "THIRD_PARTY_NOTICES.md"];

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppFolder);

    /// <summary>用户选中已有目录时，装进里面的 QuanShuo 子目录，不把文件散落在所选目录里。</summary>
    public static string WithAppFolder(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.Equals(name, AppFolder, StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, AppFolder);
    }

    /// <summary>从目标路径往上找到第一个存在的目录，给文件夹选择框定位。</summary>
    public static string NearestExistingDirectory(string path)
    {
        try
        {
            string? current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 手填的路径无效时退回默认位置
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public static async Task InstallAsync(string directory, bool createShortcuts, bool desktopShortcut, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("安装位置不是有效路径。");
        // 圈说常驻托盘。旧版运行时覆盖不了；另一份副本运行时，新版启动也只会唤起旧的那份。
        if (IsAppRunning()) throw new InvalidOperationException("圈说正在运行。请在托盘图标上右键退出，再安装。");

        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "QuanShuo.exe");
            await using (Stream source = OpenResource("QuanShuo.exe"))
            {
                await InstallFileAsync(source, target, 0, .55, progress, cancellationToken);
            }

            foreach (string name in DistributionDocuments)
            {
                await using Stream document = OpenResource(name);
                await InstallFileAsync(document, Path.Combine(directory, name), .55, .56, null, cancellationToken);
            }

            string docsDirectory = Path.Combine(directory, "docs");
            Directory.CreateDirectory(docsDirectory);
            foreach (string name in LocalPromptFiles)
            {
                await using Stream prompt = OpenResource(name);
                await InstallFileAsync(prompt, Path.Combine(docsDirectory, name), .56, .56, null, cancellationToken);
            }

            string setupSource = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定安装器位置。");
            string setupTarget = Path.Combine(directory, "QuanShuo-Setup.exe");
            if (!string.Equals(setupSource, setupTarget, StringComparison.OrdinalIgnoreCase))
            {
                await using var self = new FileStream(setupSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await InstallFileAsync(self, setupTarget, .56, .98, progress, cancellationToken);
            }

            if (createShortcuts)
            {
                CreateShortcuts(directory, target, desktopShortcut);
                RegisterUninstall(directory);
            }

            progress?.Report(1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("这个位置写不进去。请确认磁盘空间，或换一个安装目录。", exception);
        }
    }

    /// <summary>界面只显示一句大白话，完整异常写进临时目录，排查时用。</summary>
    public static void LogFailure(Exception exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "QuanShuo-Setup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去不影响安装结果的提示
        }
    }

    public static void Launch(string directory)
    {
        // 圈说启动后只进托盘。安装完要打开窗口，否则看起来像什么都没发生。
        Process.Start(new ProcessStartInfo(Path.Combine(directory, "QuanShuo.exe"))
        {
            Arguments = "--show-history",
            WorkingDirectory = directory,
            UseShellExecute = true,
        });
    }

    public static void Uninstall()
    {
        const string title = "圈说卸载";
        string setup = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定卸载程序位置。");
        string directory = Path.GetDirectoryName(setup) ?? throw new InvalidOperationException("无法确定安装目录。");
        if (!IsRegisteredInstall(directory))
        {
            MessageBox.Show("这个位置没有安装圈说，不需要卸载。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (IsAppRunning())
        {
            MessageBox.Show("圈说正在运行。请在托盘图标上右键退出，再卸载。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // 用户可能把圈说装在已有目录里。只删自己写入的文件，绝不递归删除目录。
            foreach (string name in new[] { "QuanShuo.exe" }.Concat(DistributionDocuments))
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }

            string docsDirectory = Path.Combine(directory, "docs");
            // Leave redirected directories and any user-added documents untouched.
            if (Directory.Exists(docsDirectory) && (File.GetAttributes(docsDirectory) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string name in LocalPromptFiles)
                {
                    string prompt = Path.Combine(docsDirectory, name);
                    if (File.Exists(prompt)) File.Delete(prompt);
                }
                if (!Directory.EnumerateFileSystemEntries(docsDirectory).Any()) Directory.Delete(docsDirectory);
            }

            RemoveShortcuts();
            RemoveAutoStart(directory);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("有文件正在被占用。请关闭圈说后重试。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        MessageBox.Show("圈说已卸载。你的记录和设置还保留着，重新安装后可以继续使用。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        // 必须在提示框之后启动：延迟删除要等本进程退出，否则卸载程序自己删不掉。
        // rmdir 不带 /s，目录里还有别的文件时什么也不做。
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{setup}\" & rmdir \"{directory}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static bool IsAppRunning()
    {
        Process[] processes = Process.GetProcessesByName("QuanShuo");
        foreach (Process process in processes) process.Dispose();
        return processes.Length > 0;
    }

    private static bool IsRegisteredInstall(string directory)
    {
        using RegistryKey? entry = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return entry?.GetValue("InstallLocation") is string installed &&
               string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase);
    }

    private static Stream OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("安装包缺少资源：" + name);

    /// <summary>先写临时文件再替换。写到一半失败时，已安装的版本不受影响。</summary>
    private static async Task InstallFileAsync(Stream source, string target, double progressFrom, double progressTo, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string temporary = target + ".tmp";
        try
        {
            await CopyAsync(source, temporary, progressFrom, progressTo, progress, cancellationToken);
            await ReplaceAsync(temporary, target, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task ReplaceAsync(string temporary, string target, CancellationToken cancellationToken)
    {
        // 目录里的 exe 一有变动，系统或杀毒软件就会读取一下，这时替换会报“被占用”。
        // 这种占用只持续一瞬间，Restart Manager 也查不到占用者，等一下就好。
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, target, true);
                return;
            }
            catch (Exception exception) when (attempt < ReplaceAttempts && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(ReplaceRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task CopyAsync(Stream source, string path, double progressFrom, double progressTo, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = new byte[CopyBufferSize];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(progressFrom + (progressTo - progressFrom) * copied / Math.Max(1, source.Length));
        }

        await target.FlushAsync(cancellationToken);
    }

    private static void CreateShortcuts(string directory, string target, bool desktopShortcut)
    {
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppFolder);
        Directory.CreateDirectory(programs);
        CreateShortcut(Path.Combine(programs, "圈说.lnk"), target, directory);
        if (desktopShortcut)
        {
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "圈说.lnk"), target, directory);
        }
    }

    private static void CreateShortcut(string path, string target, string workingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows 无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = target + ",0";
        shortcut.Save();
    }

    private static void RegisterUninstall(string directory)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        string setup = Path.Combine(directory, "QuanShuo-Setup.exe");
        key.SetValue("DisplayName", "圈说 QuanShuo");
        key.SetValue("DisplayVersion", typeof(InstallerLogic).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        key.SetValue("Publisher", "QuanShuo");
        key.SetValue("InstallLocation", directory);
        key.SetValue("DisplayIcon", Path.Combine(directory, "QuanShuo.exe"));
        key.SetValue("UninstallString", $"\"{setup}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Ceiling(new FileInfo(Path.Combine(directory, "QuanShuo.exe")).Length / 1024.0), RegistryValueKind.DWord);
    }

    private static void RemoveShortcuts()
    {
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppFolder);
        foreach (string path in new[]
                 {
                     Path.Combine(programs, "圈说.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "圈说.lnk"),
                 })
        {
            if (File.Exists(path)) File.Delete(path);
        }

        if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs);
    }

    private static void RemoveAutoStart(string directory)
    {
        // 圈说默认开启开机自启。只清指向这个目录的那一条，便携版的自启不归卸载程序管。
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (run?.GetValue(AutoStartValue) is string command && command.Contains(directory, StringComparison.OrdinalIgnoreCase))
        {
            run.DeleteValue(AutoStartValue, false);
        }
    }
}
