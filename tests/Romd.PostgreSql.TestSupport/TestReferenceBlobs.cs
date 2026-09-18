using System.Collections.Concurrent;
using System.Security.Cryptography;
using Romd.Application.Common.ReferenceCatalog;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Deterministic blob IO for persistence tests; CAS lifecycle tests use the real store.</summary>
public sealed class TestReferenceBlobs : IReferenceBlobStore
{
    public static TestReferenceBlobs Instance { get; } = new();
    private readonly ConcurrentDictionary<string, byte[]> contents = new();
    public Task<StoredReferenceBlob> StoreAsync(byte[] bytes, CancellationToken ct)
    {
        contents[Convert.ToHexStringLower(SHA256.HashData(bytes))] = bytes.ToArray();
        return Task.FromResult(new StoredReferenceBlob(bytes.Length, bytes.Length, false));
    }
    public Task<byte[]?> ReadAsync(string hash, CancellationToken ct) => Task.FromResult(contents.GetValueOrDefault(hash));
}
