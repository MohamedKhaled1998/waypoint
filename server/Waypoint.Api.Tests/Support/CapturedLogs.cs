using Microsoft.Extensions.Logging;

namespace Waypoint.Api.Tests.Support;

/// <summary>Collects every formatted log line, with exception text, so tests can assert what never appears.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public string[] Lines { get { lock (_lines) return [.. _lines]; } }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose() { }

    private sealed class Capture(CapturedLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception) + (exception is null ? "" : " " + exception);
            lock (owner._lines) owner._lines.Add(line);
        }
    }
}
