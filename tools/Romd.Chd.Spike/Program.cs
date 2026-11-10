using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moonlark.Libchdr;
using Romd.Media;
using Romd.Media.Chd;
using Romd.Storage;

// This opt-in experiment verifies one libchdr build directory against the record its build wrote.
// It does not register a media policy or establish worker containment.
try
{
    if (args is ["resident-control", var firstDirectory, var secondDirectory])
    {
        ChdNativeCandidate.FromBuildRecord(Path.GetFullPath(firstDirectory)).VerifyAndLoad();
        ChdNativeCandidate.FromBuildRecord(Path.GetFullPath(secondDirectory)).VerifyAndLoad();
        Console.Error.WriteLine("Unexpected acceptance of a different resident path.");
        return 2;
    }
    if (args is ["read-ranges", var rangeStorageKey, var rangeRoot, var rangeOriginalIso, var rangeDirectory])
    {
        var rangeNative = ChdNativeCandidate.FromBuildRecord(Path.GetFullPath(rangeDirectory));
        StorageKey rangeKey = StorageKey.FromHash(Romd.Domain.Hashing.Sha256.Parse(rangeStorageKey));
        var rangeServices = new ServiceCollection();
        rangeServices.AddContentAddressableStorage(options =>
        {
            options.RootPath = Path.GetFullPath(rangeRoot);
            options.WritePolicy = ContentWritePolicy.Raw;
        });
        await using ServiceProvider rangeProvider = rangeServices.BuildServiceProvider();
        IContentAddressableStore rangeStore = rangeProvider.GetRequiredService<IContentAddressableStore>();
        using var originalHandle = File.OpenHandle(Path.GetFullPath(rangeOriginalIso), FileMode.Open,
            FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        long rangeOriginalLength = RandomAccess.GetLength(originalHandle);
        var rangeLimits = ChdDvdLimits.Preflight;
        var rangeCache = new MediaBlockCache(2L << 20);
        await using var rangePool = new ChdReadPool(rangeCache, maxConcurrent: 2, maxIdle: 1);
        ChdNativeIdentity rangeIdentity = rangeNative.VerifyAndLoad();
        await using var rangeFirst = await ChdBlockDevice.OpenAsync(
            rangeStore.OpenSynchronousRaw(rangeKey) ?? throw new FileNotFoundException("The existing raw CAS object is missing."),
            rangePool, rangeLimits, rangeKey.ToString());
        await using var rangeSecond = await ChdBlockDevice.OpenAsync(
            rangeStore.OpenSynchronousRaw(rangeKey) ?? throw new FileNotFoundException("The existing raw CAS object is missing."),
            rangePool, rangeLimits, rangeKey.ToString());
        if (rangeFirst.Length != rangeOriginalLength || rangeSecond.Length != rangeOriginalLength)
            throw new InvalidDataException($"CHD logical length {rangeFirst.Length} does not match original ISO length {rangeOriginalLength}.");

        (string Name, long Offset, int Bytes)[] rangeQueries =
        [
            ("beginning", 0, 97),
            ("hunk-boundary", 65536 - 7, 31),
            ("cross-four-gib", (1L << 32) - 17, 97),
            ("above-four-gib", (1L << 32) + 31, 97),
            ("multiple-hunks", 65536 - 11, 256 << 10),
            ("near-eof", Math.Max(0, rangeOriginalLength - 97), 257),
            ("eof", rangeOriginalLength, 97),
            ("maximum-offset", long.MaxValue, 97)
        ];
        var rangeResults = new List<object>();
        long rangeComparedBytes = 0;
        foreach (var rangeQuery in rangeQueries)
        {
            int rangeExpectedCount = rangeQuery.Offset >= rangeOriginalLength ? 0 :
                (int)Math.Min(rangeQuery.Bytes, rangeOriginalLength - rangeQuery.Offset);
            byte[] rangeExpected = new byte[rangeExpectedCount];
            int rangeOriginalRead = 0;
            while (rangeOriginalRead < rangeExpected.Length)
            {
                int rangeRead = RandomAccess.Read(originalHandle, rangeExpected.AsSpan(rangeOriginalRead),
                    rangeQuery.Offset + rangeOriginalRead);
                if (rangeRead <= 0)
                    throw new EndOfStreamException("Original ISO ended before its observed length.");
                rangeOriginalRead += rangeRead;
            }
            byte[] rangeFirstBytes = new byte[rangeQuery.Bytes], rangeSecondBytes = new byte[rangeQuery.Bytes];
            rangeFirstBytes.AsSpan().Fill(0xFE);
            rangeSecondBytes.AsSpan().Fill(0xFE);
            int[] rangeCounts = await Task.WhenAll(
                rangeFirst.ReadAtAsync(rangeQuery.Offset, rangeFirstBytes).AsTask(),
                rangeSecond.ReadAtAsync(rangeQuery.Offset, rangeSecondBytes).AsTask());
            if (rangeCounts[0] != rangeExpectedCount || rangeCounts[1] != rangeExpectedCount ||
                !rangeFirstBytes.AsSpan(0, rangeExpectedCount).SequenceEqual(rangeExpected) ||
                !rangeSecondBytes.AsSpan(0, rangeExpectedCount).SequenceEqual(rangeExpected))
                throw new InvalidDataException($"CHD logical range '{rangeQuery.Name}' differs from the original ISO.");
            if (rangeFirstBytes.AsSpan(rangeExpectedCount).IndexOfAnyExcept((byte)0xFE) != -1 ||
                rangeSecondBytes.AsSpan(rangeExpectedCount).IndexOfAnyExcept((byte)0xFE) != -1)
                throw new InvalidDataException($"CHD logical range '{rangeQuery.Name}' changed its destination tail.");
            rangeComparedBytes += rangeExpectedCount;
            rangeResults.Add(new
            {
                rangeQuery.Name,
                rangeQuery.Offset,
                RequestedBytes = rangeQuery.Bytes,
                ExpectedBytes = rangeExpectedCount,
                FirstReadBytes = rangeCounts[0],
                SecondReadBytes = rangeCounts[1],
                OriginalSha256 = Convert.ToHexStringLower(SHA256.HashData(rangeExpected)),
                FirstSha256 = Convert.ToHexStringLower(SHA256.HashData(rangeFirstBytes.AsSpan(0, rangeExpectedCount))),
                SecondSha256 = Convert.ToHexStringLower(SHA256.HashData(rangeSecondBytes.AsSpan(0, rangeExpectedCount))),
                DestinationTailUntouched = true
            });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            rangeIdentity.Rid,
            rangeIdentity.UpstreamCommit,
            rangeIdentity.RecipeVersion,
            rangeIdentity.BinarySha256,
            rangeIdentity.BuildId,
            RawStorageKey = rangeKey.ToString(),
            LogicalLength = rangeFirst.Length,
            OriginalLength = rangeOriginalLength,
            ReadIntegrity = rangeFirst.Integrity.ToString(),
            CacheBudgetBytes = rangeCache.BudgetBytes,
            MaximumTotalHandles = 2,
            MaximumIdleHandles = 1,
            QueryCount = rangeQueries.Length,
            DeviceReadCount = rangeQueries.Length * 2,
            OriginalQueryBytes = rangeComparedBytes,
            ComparedDeviceBytes = rangeComparedBytes * 2,
            Queries = rangeResults
        }));
        return 0;
    }
    if (args is not ["inspect", var fixture, var root, var directory])
        throw new ArgumentException("Usage: inspect <synthetic-chd> <owned-cas-root> <libchdr-directory>; read-ranges <raw-storage-key> <owned-cas-root> <original-iso> <libchdr-directory>; or resident-control <libchdr-directory> <other-libchdr-directory>");

    var native = ChdNativeCandidate.FromBuildRecord(Path.GetFullPath(directory));

    var services = new ServiceCollection();
    services.AddContentAddressableStorage(options =>
    {
        options.RootPath = Path.GetFullPath(root);
        options.WritePolicy = ContentWritePolicy.Raw;
    });
    await using ServiceProvider provider = services.BuildServiceProvider();
    IContentAddressableStore store = provider.GetRequiredService<IContentAddressableStore>();
    await using FileStream input = File.OpenRead(fixture);
    var stored = await store.StoreAsync(input);
    using var source = new ChdRawContentSource(store.OpenSynchronousRaw(stored.Key)
        ?? throw new FileNotFoundException("The imported raw object is missing."));
    byte[] fixedHeader = new byte[124];
    int headerBytesRead = 0;
    while (headerBytesRead < fixedHeader.Length)
    {
        int count = source.Read(headerBytesRead, fixedHeader.AsSpan(headerBytesRead));
        if (count <= 0)
            break;
        headerBytesRead += count;
    }
    var admissionLimits = ChdDvdLimits.Preflight;
    ChdHeaderPreflightResult admission = ChdHeaderPreflight.Inspect(
        fixedHeader.AsSpan(0, headerBytesRead), source.Length, admissionLimits);
    if (admission is ChdHeaderPreflightResult.Rejected rejected)
        throw new InvalidDataException($"ROMD CHD header rejected: {rejected.Cause}; slot={rejected.CodecSlot}; codec={rejected.Codec:X8}");

    ChdNativeIdentity identity = native.VerifyAndLoad();
    using ChdFile chd = ChdFile.Open(source, leaveOpen: true, options: new ChdOpenOptions { ReadAheadBytes = 0 });
    ChdIntegrityResult integrity = ChdIntegrity.Verify(chd);
    if (!integrity.IsVerified)
        throw new InvalidDataException("Both stored CHD hashes must exist and match.");

    using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    var crc32 = new Crc32();
    byte[] buffer = new byte[64 * 1024];
    long length = checked((long)chd.Header.LogicalBytes);
    long offset = 0;
    while (offset < length)
    {
        int count = chd.ReadAt(offset, buffer);
        if (count <= 0)
            throw new EndOfStreamException("CHD logical bytes ended before the declared length.");
        sha256.AppendData(buffer.AsSpan(0, count));
        sha1.AppendData(buffer.AsSpan(0, count));
        md5.AppendData(buffer.AsSpan(0, count));
        crc32.Append(buffer.AsSpan(0, count));
        offset = checked(offset + count);
    }
    if (chd.ReadAt(length, buffer) != 0)
        throw new InvalidDataException("The logical read exceeded EOF.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        identity.Rid,
        identity.UpstreamCommit,
        identity.RecipeVersion,
        identity.BinarySha256,
        identity.BuildId,
        RawStorageKey = stored.Key.ToString(),
        ContainerLength = source.Length,
        LogicalLength = length,
        Sha256 = Convert.ToHexStringLower(sha256.GetHashAndReset()),
        Sha1 = Convert.ToHexStringLower(sha1.GetHashAndReset()),
        Md5 = Convert.ToHexStringLower(md5.GetHashAndReset()),
        Crc32 = crc32.GetCurrentHashAsUInt32().ToString("x8"),
        integrity.IsVerified,
        ComputedRawSha1 = integrity.ComputedRawSha1.ToString(),
        ComputedOverallSha1 = integrity.ComputedOverallSha1.ToString()
    }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
    return 1;
}
