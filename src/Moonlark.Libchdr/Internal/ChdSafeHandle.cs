using System.Buffers;
using System.Runtime.InteropServices;
using Moonlark.Libchdr.Interop;

namespace Moonlark.Libchdr.Internal;

/// <summary>Adopts a successful native open and releases its callback context after native close returns.</summary>
/// <remarks>The handle also owns the pooled hunk cache, so it goes back to the pool only once no native call can still be
/// writing into it: after the last reference is released.</remarks>
internal sealed unsafe class ChdSafeHandle : SafeHandle
{
    private byte[]? _hunkCache;

    internal ChdSafeHandle(chd_file* file, ChdSourceContext context) : base(nint.Zero, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (file == null)
            throw new ArgumentException("A CHD handle must own a successful native open.", nameof(file));
        Context = context;
        SetHandle((nint)file);
    }
    internal ChdSourceContext Context { get; }
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>The pooled cache of at least <paramref name="hunkBytes"/> bytes; callers hold a reference while using it.</summary>
    internal byte[] HunkCache(int hunkBytes) => _hunkCache ??= ArrayPool<byte>.Shared.Rent(hunkBytes);

    protected override bool ReleaseHandle()
    {
        try
        { NativeMethods.chd_close((chd_file*)handle); return true; }
        catch (Exception exception) { Context.CaptureFault(exception); return false; }
        finally
        {
            Context.Dispose();
            if (_hunkCache is { } cache)
            {
                _hunkCache = null;
                ArrayPool<byte>.Shared.Return(cache, clearArray: true);
            }
        }
    }
}
