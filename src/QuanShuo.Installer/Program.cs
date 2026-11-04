namespace QuanShuo.Installer;

internal static class Program
{
    // 文件夹选择框是 Shell COM 对话框，主线程必须是 STA。
    // 顶层语句写法的主线程默认是 MTA，点“浏览”会让窗口永久失去响应。
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            InstallerLogic.Uninstall();
            return 0;
        }

        string? directory = ArgumentValue(args, "--dir");
        if (directory is not null)
        {
            InstallerLogic.InstallAsync(directory, false, false, null, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }

        Application.Run(new InstallerForm());
        return 0;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
