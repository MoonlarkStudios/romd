using Microsoft.Extensions.Logging;

/// <summary>Do not silently inventory a partial source when an existing reader skips a row.</summary>
internal sealed class InventoryLogger<T> : ILogger<T>
{
    public string? FirstWarning { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || FirstWarning is not null)
            return;
        string message = formatter(state, exception);
        FirstWarning = message[..Math.Min(message.Length, 500)];
    }
}
