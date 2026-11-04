namespace QuanShuo.Native.Transcription;

/// <summary>切换持有独占租约，覆盖验证、持久化和恢复；普通识别可以沿用既有并发行为。</summary>
internal sealed class ModelActivityGate
{
    private readonly object sync = new();
    private int users;
    private bool switching;

    public bool IsSwitching { get { lock (sync) return switching; } }

    public IDisposable EnterUse()
    {
        lock (sync)
        {
            if (switching) throw new InvalidOperationException("正在切换本地模型，请等切换结束后再录音或测试。");
            users++;
            return new Lease(() => { lock (sync) users--; });
        }
    }

    public IDisposable EnterSwitch()
    {
        lock (sync)
        {
            if (switching || users > 0) throw new InvalidOperationException("识别或连接测试尚未结束，请稍后再切换模型。");
            switching = true;
            return new Lease(() => { lock (sync) switching = false; });
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? onDispose = release;
        public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
    }
}
