using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Source.Dat;

public sealed record DatReviewRomLink(string EntryName, string FileName, int RomId, Sha1 Sha1);
public sealed record DatReviewStoredRom(int RomId, Sha1 Sha1);
/// <summary>An installed entry's title link, with the title's links through other source entries.</summary>
public sealed record DatReviewTitleLink(
    string EntryName, int TitleId, string TitleName, bool ReferencedByAnotherVersion, int OtherSourceLinks);
