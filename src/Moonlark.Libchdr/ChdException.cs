namespace Moonlark.Libchdr;

/// <summary>A failed CHD operation reported by libchdr or structural validation.</summary>
public sealed class ChdException : IOException
{
    /// <summary>Creates an error whose message identifies the operation and native code.</summary>
    /// <param name="error">The native CHD error code.</param>
    /// <param name="operation">The operation, including its hunk or offset when relevant.</param>
    public ChdException(ChdError error, string operation)
        : base($"CHD operation '{operation}' failed: {error} ({(int)error}).")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Error = error;
    }

    /// <summary>The native CHD error code.</summary>
    public ChdError Error { get; }
}
