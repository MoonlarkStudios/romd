using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Queries;

/// <summary>Composable logical/output/file joins. Policy and captured container interpretation use the complete fact reader.</summary>
internal static class RomContentQuery
{
    internal static IQueryable<OriginalRomContentRow> OriginalRomContent(this RomdDbContext context) =>
        from rom in context.RomFiles
        join output in context.RepresentationOutputs on rom.Id equals output.RomId
        join representation in context.RomRepresentations on output.RepresentationId equals representation.Id
        join file in context.Files on representation.FileId equals file.Id
        where representation.Encoding == RomRepresentationEncoding.Identity && file.Sha256 == rom.ContentSha256 && file.Size == rom.ContentSize
            && context.Verifications.Any(proof => proof.RepresentationId == representation.Id)
        select new OriginalRomContentRow { RepresentationId = representation.Id, OutputId = output.Id, RomId = rom.Id, FileId = file.Id, Sha1 = rom.Sha1, Sha256 = file.Sha256, Size = file.Size };

    /// <summary>Active custody retains catalog attribution even after that exact output's logical owner moves to a different representation.</summary>
    internal static IQueryable<int> CatalogMatchedPinnedFileIds(this RomdDbContext context) =>
        from pin in context.Set<MediaReconciliationPinEntity>()
        join output in context.RepresentationOutputs on pin.OutputId equals (int?)output.Id
        join logical in context.RomFiles
            on new { output.ContentSha256, output.ContentSize, output.Sha1, output.Md5, output.Crc32 }
            equals new { logical.ContentSha256, logical.ContentSize, logical.Sha1, logical.Md5, logical.Crc32 }
        where context.DatRoms.Any(dat => dat.RomFileId == logical.Id)
        select pin.FileId;

    internal static IQueryable<RomArtifactRow> RomArtifacts(this RomdDbContext context) =>
        from output in context.RepresentationOutputs
        join representation in context.RomRepresentations on output.RepresentationId equals representation.Id
        join artifact in context.Files on representation.FileId equals artifact.Id
        join rom in context.RomFiles on output.RomId equals rom.Id
        select new RomArtifactRow
        {
            RomId = rom.Id,
            OutputId = output.Id,
            MemberOrdinal = output.MemberOrdinal,
            RepresentationId = representation.Id,
            FileId = artifact.Id,
            ArtifactSha256 = artifact.Sha256,
            ArtifactSize = artifact.Size,
            Encoding = representation.Encoding,
            ArtifactMd5 = artifact.ArtifactMd5,
            ArtifactSha1 = artifact.ArtifactSha1,
            IsPreferred = rom.PreferredOutputId == output.Id,
            HasMeasuredEvidence = context.Verifications.Any(proof => proof.RepresentationId == representation.Id)
        };

    internal static IQueryable<RomContentRow> RomContent(this RomdDbContext context) => context.RomContent(context.RomFiles);

    internal static IQueryable<RomContentRow> RomContent(this RomdDbContext context, IQueryable<RomFileEntity> roms) =>
        from rom in roms
        join output in context.RepresentationOutputs on rom.PreferredOutputId equals output.Id
        join representation in context.RomRepresentations on output.RepresentationId equals representation.Id
        join artifact in context.Files on representation.FileId equals artifact.Id
        where output.RomId == rom.Id
        select new RomContentRow
        {
            RomId = rom.Id,
            RepresentationId = representation.Id,
            OutputId = output.Id,
            MemberOrdinal = output.MemberOrdinal,
            FileId = artifact.Id,
            ContentSha256 = rom.ContentSha256,
            ContentSize = rom.ContentSize,
            Sha1 = rom.Sha1,
            Md5 = rom.Md5,
            Crc32 = rom.Crc32,
            ArtifactSha256 = artifact.Sha256,
            ArtifactSize = artifact.Size,
            ArtifactSizeOnDisk = artifact.SizeOnDisk,
            Encoding = representation.Encoding,
            OriginalFilename = rom.OriginalFilename,
            CreatedAt = rom.CreatedAt
        };
}

internal sealed class RomContentRow
{
    public int RomId { get; init; }
    public int RepresentationId { get; init; }
    public int OutputId { get; init; }
    public int? MemberOrdinal { get; init; }
    public int FileId { get; init; }
    public Sha256 ContentSha256 { get; init; }
    public long ContentSize { get; init; }
    public Sha1 Sha1 { get; init; }
    public Md5 Md5 { get; init; }
    public Crc32 Crc32 { get; init; }
    public Sha256 ArtifactSha256 { get; init; }
    public long ArtifactSize { get; init; }
    public long ArtifactSizeOnDisk { get; init; }
    public RomRepresentationEncoding Encoding { get; init; }
    public string OriginalFilename { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class RomArtifactRow
{
    public int OutputId { get; init; }
    public int? MemberOrdinal { get; init; }
    public int RomId { get; init; }
    public int RepresentationId { get; init; }
    public int FileId { get; init; }
    public Sha256 ArtifactSha256 { get; init; }
    public long ArtifactSize { get; init; }
    public RomRepresentationEncoding Encoding { get; init; }
    public bool IsPreferred { get; init; }
    public bool HasMeasuredEvidence { get; init; }
    public Md5? ArtifactMd5 { get; init; }
    public Sha1? ArtifactSha1 { get; init; }
}

internal sealed class OriginalRomContentRow
{
    public int RepresentationId { get; init; }
    public int OutputId { get; init; }
    public int RomId { get; init; }
    public int FileId { get; init; }
    public Sha1 Sha1 { get; init; }
    public Sha256 Sha256 { get; init; }
    public long Size { get; init; }
}
