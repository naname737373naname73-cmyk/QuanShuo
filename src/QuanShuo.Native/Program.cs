using QuanShuo.Native.Shell;

namespace QuanShuo.Native;

internal static class Program
{
    private const string SingleInstanceName = "Local\\QuanShuo.SingleInstance";
    private const string ActivateEventName = "Local\\QuanShuo.Activate";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        string output = args.SkipWhile(argument => argument != "--smoke-output").Skip(1).FirstOrDefault()
            ?? Path.Combine(AppContext.BaseDirectory, "smoke-output");
        // 本地验收由自己的参数解析器完整处理，错误参数不能落到其他诊断模式。
        if (args.Contains("--smoke-local-asr", StringComparer.OrdinalIgnoreCase))
        {
            return Diagnostics.SmokeLocalAsr.RunCommandLineAsync(args, output).GetAwaiter().GetResult();
        }
        // 冒烟测试默认是星湖，带这个参数时在海棠下跑
        if (args.Contains("--smoke-skin", StringComparer.OrdinalIgnoreCase)) Shell.NativeTheme.UseSkin(args.SkipWhile(argument => argument != "--smoke-skin").Skip(1).FirstOrDefault());
        if (args.Contains("--smoke-capture", StringComparer.OrdinalIgnoreCase))
        {
            return Diagnostics.SmokeCapture.RunAsync(output).GetAwaiter().GetResult();
        }

        if (args.Contains("--smoke-audio", StringComparer.OrdinalIgnoreCase))
        {
            return Diagnostics.SmokeAudio.RunAsync(output).GetAwaiter().GetResult();
        }

        if (args.Contains("--smoke-live", StringComparer.OrdinalIgnoreCase))
        {
            return Diagnostics.SmokeLive.Run(output);
        }

        if (args.Contains("--smoke-skins", StringComparer.OrdinalIgnoreCase))
        {
            string shots = args.SkipWhile(argument => argument != "--smoke-shots").Skip(1).First();
            return Diagnostics.SmokeSkins.Run(output, shots);
        }

        if (args.Contains("--smoke-speech", StringComparer.OrdinalIgnoreCase))
        {
            IntPtr target = new(long.Parse(args.SkipWhile(argument => argument != "--smoke-target").Skip(1).First()));
            string targetFile = args.SkipWhile(argument => argument != "--smoke-target-file").Skip(1).First();
            string shots = args.SkipWhile(argument => argument != "--smoke-shots").Skip(1).First();
            string? wav = args.SkipWhile(argument => argument != "--smoke-input").Skip(1).FirstOrDefault();
            return Diagnostics.SmokeSpeech.Run(output, target, targetFile, shots, wav);
        }

        if (args.Contains("--smoke-coordinator", StringComparer.OrdinalIgnoreCase))
        {
            string input = args.SkipWhile(argument => argument != "--smoke-input").Skip(1).First();
            string longInput = args.SkipWhile(argument => argument != "--smoke-long").Skip(1).First();
            int pause = int.Parse(args.SkipWhile(argument => argument != "--smoke-gap").Skip(1).FirstOrDefault() ?? "30");
            return Diagnostics.SmokeAsr.RunCoordinatorAsync(input, longInput, output, pause).GetAwaiter().GetResult();
        }

        if (args.Contains("--smoke-asr", StringComparer.OrdinalIgnoreCase))
        {
            string input = args.SkipWhile(argument => argument != "--smoke-input").Skip(1).First();
            string model = args.SkipWhile(argument => argument != "--smoke-model").Skip(1).First();
            string? endpoint = args.SkipWhile(argument => argument != "--smoke-endpoint").Skip(1).FirstOrDefault();
            int pacing = int.Parse(args.SkipWhile(argument => argument != "--smoke-pacing").Skip(1).FirstOrDefault() ?? "20");
            int gap = int.Parse(args.SkipWhile(argument => argument != "--smoke-gap").Skip(1).FirstOrDefault() ?? "0");
            bool fakeKey = args.Contains("--smoke-fake-key", StringComparer.OrdinalIgnoreCase);
            return Diagnostics.SmokeAsr.RunAsync(input, model, output, endpoint, pacing, gap, fakeKey, args.Contains("--smoke-no-direct", StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();
        }

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var mutex = new Mutex(true, SingleInstanceName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // 单实例：通知已在运行的实例打开历史窗口，然后退出。
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // 通知失败时静默退出，不弹窗打扰
            }

            return 0;
        }

        using var context = new TrayApplicationContext();
        Diagnostics.AppTrace.Log("tray-created");
        using var activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var uiContext = SynchronizationContext.Current;
        var activator = new Thread(() =>
        {
            while (activateSignal.WaitOne())
            {
                uiContext?.Post(_ => context.ShowHistory(), null);
            }
        })
        {
            IsBackground = true,
            Name = "QuanShuo activate",
        };
        activator.Start();
        if (args.Contains("--show-history", StringComparer.OrdinalIgnoreCase))
        {
            Diagnostics.AppTrace.Log("show-history-posted");
            SynchronizationContext.Current?.Post(_ => context.ShowHistory(), null);
        }

        Application.Run(context);
        return 0;
    }
}
