using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Tests;

/// <summary>
/// <see cref="ILogger{TCategoryName}"/> that records what was logged, so a test can pin a
/// diagnostic's text and level rather than only the behaviour beside it.
/// </summary>
/// <remarks>
/// <para>
/// Exists because message-quality fixes kept landing unguarded (#110): a fix that only
/// changes which message is emitted has no other observable effect, so with a
/// <c>NullLogger</c> the whole suite stays green when the fix is reverted. The nearest a
/// test could get was asserting the helper that produces the message, which leaves the
/// call site — the thing that actually chooses it — unpinned.
/// </para>
/// <para>
/// <see cref="IsEnabled"/> is unconditionally true so a level-guarded call site is still
/// captured; a capturing logger that reported Debug as disabled would silently make
/// "nothing was logged" the expected result.
/// </para>
/// </remarks>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    /// <summary>Everything logged so far. A copy, so a caller can enumerate it while the
    /// subject under test keeps logging on another thread.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>The messages logged at exactly <paramref name="level"/>.</summary>
    public IReadOnlyList<string> MessagesAt(LogLevel level) =>
        Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
