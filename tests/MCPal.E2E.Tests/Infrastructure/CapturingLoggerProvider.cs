using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MCPal.E2E.Tests;

internal sealed record LogEntry(LogLevel Level, string Category, string Message);

/// <summary>Keeps what a host logs so a test can assert on it.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> entries = new();

    public IReadOnlyCollection<LogEntry> Entries => entries;

    /// <summary>Waits until at least <paramref name="count"/> entries match and returns the matching ones.</summary>
    public async Task<IReadOnlyList<LogEntry>> WaitForAsync(Func<LogEntry, bool> match, int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var matching = entries.Where(match).ToList();
            if (matching.Count >= count)
            {
                return matching;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception)));
    }
}
