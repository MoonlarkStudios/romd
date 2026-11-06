using ErrorOr;

namespace Romd.Admin.Application.Source.Rom.Compressed;

public static class RomCdImportErrors
{
    public static Error InvalidMeasurement => Error.Validation("Rom.CdImport.InvalidMeasurement", "Complete bounded consecutive raw-track measurements are required.");
    public static Error NoCatalogMatch => Error.NotFound("Rom.CdImport.NoCatalogMatch", "No complete catalog BIN set matches every measured identity in the registered scope.");
    public static Error AmbiguousCatalog => Error.Conflict("Rom.CdImport.AmbiguousCatalog", "The measured tracks do not identify one unambiguous catalog entry and file assignment.");
    public static Error UnsupportedLayout => Error.Validation("Rom.CdImport.UnsupportedLayout", "Compressed CD import requires exact portable track-file names, complete BIN identities and one complete CUE requirement without extra files.");
}
