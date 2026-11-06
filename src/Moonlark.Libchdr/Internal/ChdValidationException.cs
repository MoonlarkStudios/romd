namespace Moonlark.Libchdr.Internal;

internal sealed class ChdValidationException(ChdError error, string operation) : Exception(operation)
{
    internal ChdError Error { get; } = error;
}
