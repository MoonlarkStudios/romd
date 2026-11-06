namespace Moonlark.Libchdr.Interop;

// Hand-written, unlike Libchdr.g.cs beside it: the generator never writes or deletes this file.
internal static partial class NativeMethods
{
    /// <summary>Registers the verifying resolver before any import in this class can bind, whichever code calls first.</summary>
    static NativeMethods() => LibchdrLibrary.RegisterResolver();
}
