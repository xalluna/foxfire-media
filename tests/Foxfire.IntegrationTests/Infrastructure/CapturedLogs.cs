using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>Collects every log line (all categories, Trace and up) the test host writes.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public string All => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel} {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += "\n" + exception;
            }

            // Structured state too: a secret could hide in a property that the message template omits.
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                line += " | " + string.Join(", ", properties.Select(p => $"{p.Key}={p.Value}"));
            }

            lines.Enqueue(line);
        }
    }
}
