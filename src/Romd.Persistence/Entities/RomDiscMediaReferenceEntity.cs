using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Converters;
using Crc32Hash = Romd.Domain.Hashing.Crc32;

namespace Romd.Persistence.Entities;

/// <summary>An immutable local reference and the exact retained body behind its published assertions.</summary>
/// <remarks>Shape and body checks do not authenticate retrieval or the assertions. Trusted admission
/// belongs to the importer. This row is independent of the eligibility observation cache.</remarks>
public sealed class RomDiscMediaReferenceEntity
{
    private RomDiscMediaReferenceEntity() { }

    public Guid Id { get; private set; }
    public RomDiscReferenceSystem System { get; private set; }
    public RomDiscReferenceMedia Media { get; private set; }
    public int FileCount { get; private set; }
    public long LogicalBytes { get; private set; }
    public Sha1 Sha1 { get; private set; }
    public Md5 Md5 { get; private set; }
    public uint Crc32 { get; private set; }
    public long RawReferenceBytes { get; private set; }
    public Sha256 RawReferenceSha256 { get; private set; }
    public string ReferenceSource { get; private set; } = null!;
    public string ParserProfile { get; private set; } = null!;
    public string AcceptedSourceProvenance { get; private set; } = null!;
    public long RetrievedAtUtcTicks { get; private set; }
    private byte[] RawBody { get; set; } = [];

    public static RomDiscMediaReferenceEntity FromDomain(RomDiscMediaReferenceRecord record, ReadOnlySpan<byte> rawBody)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (rawBody.Length != record.RawReferenceBytes)
            throw new ArgumentException("The retained body length differs from the admitted reference.", nameof(rawBody));
        // Copy before hashing so callers retain no mutable alias to the validated custody bytes.
        var entity = new RomDiscMediaReferenceEntity
        {
            Id = record.Id,
            System = record.System,
            Media = record.Media,
            FileCount = record.FileCount,
            LogicalBytes = record.LogicalBytes,
            Sha1 = record.Sha1,
            Md5 = record.Md5,
            Crc32 = record.Crc32.ToUInt32(),
            RawReferenceBytes = record.RawReferenceBytes,
            RawReferenceSha256 = record.RawReferenceSha256,
            ReferenceSource = record.ReferenceSource,
            ParserProfile = record.ParserProfile,
            AcceptedSourceProvenance = record.AcceptedSourceProvenance,
            RetrievedAtUtcTicks = record.RetrievedAt.UtcTicks,
            RawBody = rawBody.ToArray()
        };
        entity.ValidateRawBody();
        return entity;
    }

    public RomDiscMediaReferenceRecord ToDomain()
    {
        ValidateRawBody();
        return new RomDiscMediaReferenceRecord(Id, System, Media, FileCount, LogicalBytes, Sha1, Md5,
            Crc32Hash.FromUInt32(Crc32), RawReferenceBytes, RawReferenceSha256, ReferenceSource, ParserProfile,
            AcceptedSourceProvenance, new DateTimeOffset(RetrievedAtUtcTicks, TimeSpan.Zero));
    }

    /// <summary>Returns a copy, never the EF-owned retained body.</summary>
    public byte[] GetRawBodySnapshot()
    {
        ValidateRawBody();
        return RawBody.ToArray();
    }

    private void ValidateRawBody()
    {
        if (RawReferenceBytes is <= 0 or > RomDiscMediaReferenceRecord.MaximumRawReferenceBytes ||
            RawBody is null || RawBody.LongLength != RawReferenceBytes ||
            RawReferenceSha256.IsEmpty || Sha256.FromSpan(SHA256.HashData(RawBody)) != RawReferenceSha256)
            throw new InvalidOperationException("The retained reference body is missing or differs from its immutable length or SHA256.");
    }

    public static void Configure(EntityTypeBuilder<RomDiscMediaReferenceEntity> builder)
    {
        builder.ToTable("RomDiscMediaReferences");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.System).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(row => row.Media).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(row => row.Sha1).HasConversion<HashValueConverter<Sha1>>().HasMaxLength(20).IsRequired();
        builder.Property(row => row.Md5).HasConversion<HashValueConverter<Md5>>().HasMaxLength(16).IsRequired();
        builder.Property(row => row.Crc32).HasConversion<long>();
        builder.Property(row => row.RawReferenceSha256).HasConversion<HashValueConverter<Sha256>>().HasMaxLength(32).IsRequired();
        builder.Property<byte[]>(nameof(RawBody)).HasColumnType("bytea").HasMaxLength(1048576).IsRequired();
        builder.Property(row => row.ReferenceSource).HasMaxLength(256).IsRequired();
        builder.Property(row => row.ParserProfile).HasMaxLength(128).IsRequired();
        builder.Property(row => row.AcceptedSourceProvenance).HasMaxLength(256).IsRequired();
        // Do not make the published quartet unique: contradictory trusted references must remain observable.
        builder.HasIndex(row => new { row.LogicalBytes, row.Sha1, row.Md5, row.Crc32 });
        builder.ToTable("RomDiscMediaReferences", table => table.HasCheckConstraint("CK_RomDiscMediaReferences_Observation",
            "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
            "\"System\" IN ('PlayStation1', 'PlayStation2') AND \"Media\" IN ('Cd', 'Dvd5', 'Dvd9') AND " +
            "\"FileCount\" BETWEEN 1 AND 128 AND \"LogicalBytes\" BETWEEN 1 AND 10737418240 AND " +
            "octet_length(\"Sha1\") = 20 AND \"Sha1\" <> decode(repeat('00', 20), 'hex') AND " +
            "octet_length(\"Md5\") = 16 AND \"Md5\" <> decode(repeat('00', 16), 'hex') AND " +
            "\"Crc32\" BETWEEN 0 AND 4294967295 AND \"RawReferenceBytes\" BETWEEN 1 AND 1048576 AND " +
            "octet_length(\"RawBody\") = \"RawReferenceBytes\" AND " +
            "octet_length(\"RawReferenceSha256\") = 32 AND \"RawReferenceSha256\" = sha256(\"RawBody\") AND " +
            "\"RawReferenceSha256\" <> decode(repeat('00', 32), 'hex') AND " +
            "char_length(\"ReferenceSource\") BETWEEN 1 AND 256 AND btrim(\"ReferenceSource\") <> '' AND \"ReferenceSource\" !~ '[[:cntrl:]]' AND " +
            "char_length(\"ParserProfile\") BETWEEN 1 AND 128 AND btrim(\"ParserProfile\") <> '' AND \"ParserProfile\" !~ '[[:cntrl:]]' AND " +
            "char_length(\"AcceptedSourceProvenance\") BETWEEN 1 AND 256 AND btrim(\"AcceptedSourceProvenance\") <> '' AND \"AcceptedSourceProvenance\" !~ '[[:cntrl:]]' AND " +
            "\"RetrievedAtUtcTicks\" BETWEEN 0 AND 3155378975999999999"));
        foreach (var property in builder.Metadata.GetProperties())
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
