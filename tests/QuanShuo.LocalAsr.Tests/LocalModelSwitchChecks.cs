using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.History;
using QuanShuo.Native.Transcription;

internal static class LocalModelSwitchChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string endpoint, string root, ConcurrentQueue<string> starts)
    {
        string router = endpoint + "?case=switch-router";
        AppSettings Original() => new()
        {
            SpeechProvider = "local", LocalSpeechEndpoint = router, LocalSpeechModel = "old-model",
            SpeechModel = "cloud-kept", SpeechEndpoint = "wss://example.invalid/cloud", Skin = "crabapple", Hotkey = "Ctrl+Alt+Q",
            DataDirectory = Path.Combine(root, Guid.NewGuid().ToString("N")), DeleteAudioAfterSuccess = false,
            MicrophoneDeviceId = int.MaxValue.ToString(), // A broken gate must never open a real microphone.
        };
        RecordingCoordinator Coordinator(AppSettings settings) => new(new RecordStore(settings.DataDirectory), new DpapiSecretStore(), settings);
        LocalAsrProfile Target(string model = "new-model") => new() { SchemaVersion = 1, Id = "qwen3-asr-0.6b", Endpoint = router, Model = model, ExpectedModelName = "New actual model" };

        yield return ("switch-validates-actual-identity-and-preserves-other-settings", async () =>
        {
            var original = Original();
            await using var coordinator = Coordinator(original);
            AppSettings? saved = null;
            var result = await coordinator.SwitchLocalModelAsync(Target(), original, value => { saved = value; return Task.CompletedTask; });
            Check(saved == original with { LocalSpeechModel = "new-model", SettingsRevision = 1 }, "switch changed unrelated settings");
            Check(result.ModelStatus.ModelInfo is { Name: "New actual model", CatalogId: "qwen3-asr-0.6b" }, "switch did not expose checked identity");
            Check((await coordinator.TestConnectionDetailsAsync()).LocalModel?.Name == "New actual model", "coordinator did not adopt saved model");
        });
        yield return ("switch-rejects-wrong-name-and-wrong-catalog-without-saving", async () =>
        {
            foreach (var profile in new[] { Target() with { ExpectedModelName = "Wrong" }, Target() with { Id = "qwen3-asr-1.7b" } })
            {
                var original = Original();
                await using var coordinator = Coordinator(original);
                bool saved = false;
                await Fails(() => coordinator.SwitchLocalModelAsync(profile, original, _ => { saved = true; return Task.CompletedTask; }), "身份");
                Check(!saved, "wrong model persisted");
                Check((await coordinator.TestConnectionDetailsAsync()).LocalModel?.Name == "Old actual model", "original config lost");
            }
        });
        yield return ("switch-load-failure-restores-original-model-on-shared-gateway", async () =>
        {
            starts.Clear();
            var original = Original();
            await using var coordinator = Coordinator(original);
            bool persisted = false;
            await Fails(() => coordinator.SwitchLocalModelAsync(Target("broken-model"), original, _ => { persisted = true; return Task.CompletedTask; }), "原配置已保留");
            Check(!persisted, "failed model load still persisted target");
            Check(starts.ToArray().SequenceEqual(new[] { "broken-model", "old-model" }), "did not request old model after failed switch");
        });
        yield return ("switch-persistence-failure-restores-old-choice", async () =>
        {
            starts.Clear();
            var original = Original();
            await using var coordinator = Coordinator(original);
            bool persistCalled = false;
            await Fails(() => coordinator.SwitchLocalModelAsync(Target(), original, _ => { persistCalled = true; throw new IOException("synthetic settings write failure"); }), "synthetic settings write failure");
            Check(persistCalled, "test never reached the simulated persistence failure");
            Check(starts.ToArray().SequenceEqual(new[] { "new-model", "old-model" }), "save failure did not reload old model");
            Check((await coordinator.TestConnectionDetailsAsync()).LocalModel?.Name == "Old actual model", "failed save changed coordinator configuration");
        });
        yield return ("switch-reserves-recording-retry-testing-and-settings-until-commit", async () =>
        {
            var original = Original();
            await using var coordinator = Coordinator(original);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<LocalModelSwitchResult> switching = coordinator.SwitchLocalModelAsync(Target(), original, async _ => { entered.SetResult(); await release.Task; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var record = await new RecordStore(original.DataDirectory).CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "Switch test", WindowTitle = "Synthetic" }, TranscriptionStatus.Incomplete);
                await Fails(() => coordinator.StartAsync(record), "正在切换");
                await Fails(() => coordinator.RetryAsync(record), "正在切换");
                await Fails(() => coordinator.TestConnectionAsync(), "正在切换");
                await Fails(() => coordinator.SwitchLocalModelAsync(Target(), original, _ => Task.CompletedTask), "尚未结束");
                await Fails(() => { coordinator.UpdateSettings(original); return Task.CompletedTask; }, "正在切换");
            }
            finally { release.SetResult(); await switching; }
        });
        yield return ("switch-rejects-stale-selection-before-contacting-server", async () =>
        {
            starts.Clear();
            var original = Original();
            await using var coordinator = Coordinator(original);
            await Fails(() => coordinator.SwitchLocalModelAsync(Target(), original with { LocalSpeechModel = "stale" }, _ => Task.CompletedTask), "配置已变化");
            await Fails(() => coordinator.SwitchLocalModelAsync(Target(), original with { SettingsRevision = 1 }, _ => Task.CompletedTask), "配置已变化");
            Check(starts.IsEmpty, "stale settings still loaded a model");
        });
        yield return ("new-native-window-reads-wait-for-shared-switch-commit", async () =>
        {
            var original = Original();
            await using var coordinator = Coordinator(original);
            var statusStore = new LocalAsrModelStatusStore(Path.Combine(root, "new-window-status"));
            // No WebView, window, user settings, hotkeys or UI automation: exercise only actual read handlers.
            object window = RuntimeHelpers.GetUninitializedObject(typeof(HistoryWindow));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(HistoryWindow).GetField("recording", flags)!.SetValue(window, coordinator);
            typeof(HistoryWindow).GetField("localModelStatus", flags)!.SetValue(window, statusStore);
            var handle = typeof(HistoryWindow).GetMethod("HandleAsync", flags)!;
            var shared = (SemaphoreSlim)typeof(HistoryWindow).GetField("settingsMutation", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check(shared.Wait(0), "settings gate leaked from another operation");
            Task<object?> settingsRead, modelRead;
            var committed = Target().Apply(original) with { SettingsRevision = 1 };
            var status = new LocalAsrModelStatus(committed.LocalSpeechEndpoint, committed.LocalSpeechModel, true, DateTimeOffset.UtcNow,
                new LocalModelInfo("New actual model", null, null, null, null, null, "qwen3-asr-0.6b"));
            try
            {
                settingsRead = (Task<object?>)handle.Invoke(window, ["settings.get", default(JsonElement)])!;
                modelRead = (Task<object?>)handle.Invoke(window, ["localAsr.modelInfo", default(JsonElement)])!;
                Check(!settingsRead.IsCompleted && !modelRead.IsCompleted, "new window read stale state during switch");
                coordinator.UpdateSettings(committed);
                Check(await statusStore.SaveAsync(status), "synthetic model status could not be saved");
            }
            finally { shared.Release(); }
            Check(await settingsRead.WaitAsync(TimeSpan.FromSeconds(5)) is AppSettings latest && latest == committed, "new window missed committed choice");
            Check(await modelRead.WaitAsync(TimeSpan.FromSeconds(5)) is LocalAsrModelStatus latestStatus && latestStatus == status, "new window missed verified model status");
        });
        yield return ("profile-list-refreshes-without-loading-or-creating-environment", async () =>
        {
            string directory = Path.Combine(root, "profiles", Guid.NewGuid().ToString("N"));
            var store = new LocalAsrProfileStore(directory);
            Check((await store.ReadAsync()).Length == 3 && !Directory.Exists(directory), "read created environment");
            Directory.CreateDirectory(directory);
            var profile = Target();
            string path = Path.Combine(directory, profile.Id + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, JsonOptions));
            Check(await store.ResolveAsync(profile.Id, profile) == profile, "valid registration not discovered");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile with { Model = "changed" }, JsonOptions));
            await Fails(() => store.ResolveAsync(profile.Id, profile), "已变化");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile with { Endpoint = "wss://remote.example/asr" }, JsonOptions));
            Check((await store.ReadAsync()).Single(e => e.Id == profile.Id) is { Profile: null, Error: not null }, "remote audio destination accepted");
            await File.WriteAllTextAsync(path, "bad json");
            Check((await store.ReadAsync()).Single(e => e.Id == profile.Id).Error is not null, "bad JSON masquerades as installed");
        });
        yield return ("duplicate-profile-routing-cannot-pretend-to-be-two-models", async () =>
        {
            string directory = Path.Combine(root, "duplicates");
            Directory.CreateDirectory(directory);
            foreach (string id in new[] { "qwen3-asr-0.6b", "qwen3-asr-1.7b" })
                await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(Target() with { Id = id }, JsonOptions));
            var entries = await new LocalAsrProfileStore(directory).ReadAsync();
            Check(entries.Count(e => e.Error is not null && e.Profile is null) == 2, "duplicate route accepted");
        });
        yield return ("targeted-prompts-keep-stage-boundaries-and-fixed-model-ids", () =>
        {
            foreach (var (id, name) in LocalAsrCatalog.Models)
            {
                string assessment = LocalAsrSetupPrompt.ReadAssessment(id), setup = LocalAsrSetupPrompt.Read(id);
                Check(assessment.StartsWith(LocalAsrSetupPrompt.ReadAssessment()) && assessment.Contains("登记 ID：" + id) && assessment.Contains(name), "assessment lost source or selected model");
                Check(assessment.Contains("只读检测，不下载、安装、启动或加载模型") && assessment.EndsWith("用简短的人话告诉我结论，不安装。\n"), "assessment lost read-only boundary or plain-language scope");
                Check(setup.StartsWith(LocalAsrSetupPrompt.Read()) && setup.Contains("local-asr-profiles") && setup.Contains("登记 ID：" + id), "setup lost registration or target");
                Check(setup.Contains("无需逐步询问") && setup.Contains("不要求方案摘要或逐步确认") && !setup.Contains("只请求缺失内容并结束本轮"), "setup still blocks routine installation on approval paperwork");
            }
            try { LocalAsrSetupPrompt.Read("../../evil"); throw new Exception("unknown ID accepted"); }
            catch (ArgumentException) { }
            return Task.CompletedTask;
        });
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Fails(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException error) { Check(error.Message.Contains(message), error.Message); return; }
        throw new Exception("Expected failure: " + message);
    }
}
