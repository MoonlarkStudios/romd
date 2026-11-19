using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Source.Dat;

public sealed record DatReviewRomLink(string EntryName, string FileName, int RomId, Sha1 Sha1);
public sealed record DatReviewStoredRom(int RomId, Sha1 Sha1);
public sealed record DatReviewTitleLink(string EntryName, string TitleName, bool ReferencedByAnotherVersion);
