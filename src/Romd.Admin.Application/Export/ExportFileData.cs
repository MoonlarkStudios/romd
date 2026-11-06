namespace Romd.Admin.Application.Export;

public sealed record ExportFileData(
    int TitleId,
    string TitleName,
    int PlatformId,
    string PlatformName,
    int RomFileId,
    int FileId,
    string OriginalFilename);
