using System.Security.Cryptography;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies chd Integrity Tests.</summary>
public sealed class ChdIntegrityTests
{
    /// <summary>Qualifies matches Independently Verified Chdman Hashes.</summary>
    [Theory]
    [InlineData("dvd-lzma.chd")]
    [InlineData("dvd-zlib.chd")]
    [InlineData("dvd-huff.chd")]
    [InlineData("dvd-flac.chd")]
    [InlineData("dvd-zstd.chd")]
    [InlineData("cd-cdlz.chd")]
    [InlineData("cd-cdzl.chd")]
    [InlineData("cd-cdfl.chd")]
    [InlineData("cd-cdzs.chd")]
    [InlineData("cd-subcode.chd")]
    public void MatchesIndependentlyVerifiedChdmanHashes(string name)
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture(name);
        ChdIntegrityResult result = ChdIntegrity.Verify(file);
        Assert.True(result.IsVerified);
        Assert.Equal(file.Header.RawSha1, result.ComputedRawSha1);
        Assert.Equal(file.Header.OverallSha1, result.ComputedOverallSha1);
    }

    /// <summary>Qualifies missing Stored Hashes Are Explicitly Unverified.</summary>
    [Fact]
    public void MissingStoredHashesAreExplicitlyUnverified()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture("dvd-none.chd");
        ChdIntegrityResult result = ChdIntegrity.Verify(file);
        Assert.False(result.IsVerified);
        Assert.False(result.HasStoredRawSha1);
        Assert.False(result.HasStoredOverallSha1);
        Assert.False(result.RawMatches);
        Assert.False(result.OverallMatches);
        Assert.Equal("5b5cb6873a0de0e512547d8d6815d0be92708ec6", result.ComputedRawSha1.ToString());
        Assert.Equal("52a5c08c5618bc49328a6c769ca7bea735d4e5e1", result.ComputedOverallSha1.ToString());
    }

    /// <summary>Qualifies decoding Success Does Not Hide Missing Or Mismatching Hash.</summary>
    [Theory]
    [InlineData("negative-raw-mismatch.chd", false, true)]
    [InlineData("negative-overall-mismatch.chd", true, false)]
    [InlineData("negative-raw-missing.chd", false, true)]
    public void DecodingSuccessDoesNotHideMissingOrMismatchingHash(string name, bool raw, bool overall)
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture(name);
        ChdIntegrityResult result = ChdIntegrity.Verify(file);
        Assert.False(result.IsVerified);
        Assert.Equal(raw, result.RawMatches);
        Assert.Equal(overall, result.OverallMatches);
    }

    /// <summary>Qualifies checks Cancellation And Lifetime.</summary>
    [Fact]
    public void ChecksCancellationAndLifetime()
    {
        ChdFile file = NativeTestEnvironment.OpenFixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ChdIntegrity.Verify(file, cancel.Token));
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ChdIntegrity.Verify(file));
        Assert.Throws<ArgumentNullException>(() => ChdIntegrity.Verify(null!));
    }

    /// <summary>Qualifies verify Overall From An Independent Decode Matches Verify.</summary>
    [Theory]
    [InlineData("dvd-lzma.chd")]
    [InlineData("dvd-zlib.chd")]
    [InlineData("dvd-huff.chd")]
    [InlineData("dvd-flac.chd")]
    [InlineData("dvd-zstd.chd")]
    [InlineData("cd-cdlz.chd")]
    [InlineData("cd-cdzl.chd")]
    [InlineData("cd-cdfl.chd")]
    [InlineData("cd-cdzs.chd")]
    [InlineData("cd-subcode.chd")]
    [InlineData("dvd-none.chd")]
    [InlineData("negative-raw-mismatch.chd")]
    [InlineData("negative-overall-mismatch.chd")]
    [InlineData("negative-raw-missing.chd")]
    public void VerifyOverallFromAnIndependentDecodeMatchesVerify(string name)
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture(name);
        ChdSha1 raw = HashLogicalBytes(file);
        Assert.Equal(ChdIntegrity.Verify(file), ChdIntegrity.VerifyOverall(file, raw, CancellationToken.None));
    }

    /// <summary>Qualifies a wrong supplied raw hash fails both stored comparisons.</summary>
    [Fact]
    public void VerifyOverallWithWrongRawHashMatchesNeitherStoredHash()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture();
        Span<byte> wrong = stackalloc byte[ChdSha1.ByteLength];
        Assert.True(HashLogicalBytes(file).TryWriteBytes(wrong));
        wrong[0] ^= 1;
        ChdIntegrityResult result = ChdIntegrity.VerifyOverall(file, ChdSha1.FromBytes(wrong), CancellationToken.None);
        Assert.Equal(ChdSha1.FromBytes(wrong), result.ComputedRawSha1);
        Assert.Equal(file.Header.RawSha1, result.StoredRawSha1);
        Assert.Equal(file.Header.OverallSha1, result.StoredOverallSha1);
        Assert.False(result.RawMatches);
        Assert.False(result.OverallMatches);
        Assert.False(result.IsVerified);
    }

    /// <summary>Qualifies verify Overall Checks Cancellation And Lifetime.</summary>
    [Fact]
    public void VerifyOverallChecksCancellationAndLifetime()
    {
        ChdFile file = NativeTestEnvironment.OpenFixture();
        ChdSha1 raw = file.Header.RawSha1;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ChdIntegrity.VerifyOverall(file, raw, cancel.Token));
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ChdIntegrity.VerifyOverall(file, raw, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => ChdIntegrity.VerifyOverall(null!, raw, CancellationToken.None));
    }

    // Decodes through the cached logical reader rather than the hunk loop that Verify uses.
    private static ChdSha1 HashLogicalBytes(ChdFile file)
    {
        byte[] buffer = new byte[64 << 10];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long offset = 0;
        for (int read; (read = file.ReadAt(offset, buffer)) > 0; offset += read)
            hash.AppendData(buffer, 0, read);
        Assert.Equal((long)file.Header.LogicalBytes, offset);
        return ChdSha1.FromBytes(hash.GetHashAndReset());
    }
}
