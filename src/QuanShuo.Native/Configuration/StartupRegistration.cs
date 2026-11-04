using Microsoft.Win32;

namespace QuanShuo.Native.Configuration;

public static class StartupRegistration
{
    private const string ValueName = "QuanShuo";

    public static void Apply(bool enabled)
    {
        // 开发/测试构建（bin 输出目录）不注册开机自启：
        // 测试实例曾把 bin\Debug 路径写进 Run 项，重启后测试版随系统自启。
        if (AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        string process = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定圈说程序位置，未设置开机自启。");
        string[] arguments = Environment.GetCommandLineArgs();
        string command = string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase) && arguments.Length > 1
            ? $"\"{process}\" \"{Path.GetFullPath(arguments[1])}\""
            : $"\"{process}\"";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
