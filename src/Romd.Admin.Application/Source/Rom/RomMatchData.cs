namespace Romd.Admin.Application.Source.Rom;

public sealed record RomMatchData(int DatId, string DatName, int DatGameId, string GameName,
    string RomName, int? TitleId, string? TitleName, int? PlatformId, string? PlatformName);
