using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Saldo360.Tests.Support;

/// <summary>
/// Guarda em memória tudo que a aplicação loga, para verificar que segredos não aparecem.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
