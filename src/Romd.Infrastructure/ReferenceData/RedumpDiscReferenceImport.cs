using System.Security.Cryptography;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.ReferenceData;

/// <summary>One immutable prepared retrieval, replayable without fetching or changing its timestamp.</summary>
/// <remarks>Only the importer constructs this envelope. Its shape checks do not authenticate HTTPS.
/// Retain this envelope and its optional caller-supplied Guid across a transaction retry.</remarks>
public sealed class RedumpDiscReferenceImport
{
    private readonly byte[] _rawBody;

    internal RedumpDiscReferenceImport(RomDiscMediaReferenceRecord record, ReadOnlyMemory<byte> rawBody)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (rawBody.Length != record.RawReferenceBytes)
            throw new ArgumentException("Prepared raw length differs from the parsed reference.", nameof(rawBody));
        _rawBody = rawBody.ToArray();
        if (Sha256.FromSpan(SHA256.HashData(_rawBody)) != record.RawReferenceSha256)
            throw new ArgumentException("Prepared raw bytes differ from the parsed reference SHA256.", nameof(rawBody));
        Record = record;
    }

    public RomDiscMediaReferenceRecord Record { get; }
    /// <summary>Returns a copy, never an alias to the immutable prepared retrieval.</summary>
    public byte[] CopyRawBody() => _rawBody.ToArray();
}
