using Romd.Media.PlayStation;
using Romd.Media.TestSupport;

namespace Romd.Media.Tests.PlayStation;

// Native loading belongs to the reader tests; the shared synthetic reference builder remains independent.
internal static class SyntheticChdFixture
{
    private static readonly object NativeGate = new();
    private static ChdNativeIdentity? _loadedNative;
    internal static ChdHeaderPreflightLimits Limits { get; } = new(16L << 30, 16UL << 30, 1 << 20, 4 << 20);

    internal static Romd.Media.TestSupport.SyntheticChdFixture Create(int hunks = 3, int finalBytes = 997,
        byte seed = 7, int hunkBytes = 4096, bool repeatFirst = false)
    {
        lock (NativeGate)
            _loadedNative ??= ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory).VerifyAndLoad();
        return Romd.Media.TestSupport.SyntheticChdFixture.Create(hunks, finalBytes, seed, hunkBytes, repeatFirst);
    }
}
