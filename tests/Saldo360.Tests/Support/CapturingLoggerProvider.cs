using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Saldo360.Tests.Support;

/// <summary>
/// Guarda em memória tudo que a aplicação loga, para verificar que segredos não aparecem.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ConcurrentQueue<CapturedLogEntry> _structuredEntries = new();

    public IReadOnlyCollection<string> Entries => _entries;
    public IReadOnlyCollection<CapturedLogEntry> StructuredEntries => _structuredEntries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, _structuredEntries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string categoryName,
        ConcurrentQueue<string> entries,
        ConcurrentQueue<CapturedLogEntry> structuredEntries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(value => value.Key != "{OriginalFormat}").ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            entries.Enqueue(message);
            structuredEntries.Enqueue(new CapturedLogEntry(categoryName, logLevel, eventId, message, exception, properties));
        }
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties);
