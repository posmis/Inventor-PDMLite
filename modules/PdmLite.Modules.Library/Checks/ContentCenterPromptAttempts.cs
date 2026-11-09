namespace PdmLite.Modules.Library.Checks;

/// <summary>Повторяем сбой проверки, но не уже показанный пользователю вопрос.</summary>
internal sealed class ContentCenterPromptAttempts
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(string key, Func<Action, Task> attempt)
    {
        lock (_gate)
            if (_shown.Contains(key) || !_pending.Add(key)) return;
        try
        {
            await attempt(() => { lock (_gate) _shown.Add(key); });
        }
        finally
        {
            lock (_gate) _pending.Remove(key);
        }
    }
}
