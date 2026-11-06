namespace Moonlark.Native.Engineering.Core;

/// <summary>A failed engineering check. Failures are values; only the command boundary maps them to exit codes.</summary>
internal sealed record Failure(string Message);

/// <summary>Either a value or the first failure that prevented producing it.</summary>
internal readonly struct Result<T>
{
    private readonly T? _value;
    private readonly Failure? _failure;

    private Result(T? value, Failure? failure)
    {
        _value = value;
        _failure = failure;
    }

    internal bool Succeeded => _failure is null;

    internal T Value => _failure is null ? _value! : throw new InvalidOperationException("A failed result has no value: " + _failure.Message);

    internal Failure Failure => _failure ?? throw new InvalidOperationException("A successful result has no failure.");

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(Failure failure) => new(default, failure);

    internal Result<TNext> Then<TNext>(Func<T, Result<TNext>> next) => Succeeded ? next(Value) : Failure;
}

internal static class Check
{
    /// <summary>Returns a failure carrying <paramref name="message"/> when <paramref name="condition"/> is false.</summary>
    internal static Failure? That(bool condition, string message) => condition ? null : new Failure(message);
}
