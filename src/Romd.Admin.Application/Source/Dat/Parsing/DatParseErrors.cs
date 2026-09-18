using ErrorOr;

namespace Romd.Admin.Application.Source.Dat.Parsing;

public static class DatParseErrors
{
    public static Error StreamNotSeekable =>
        Error.Validation("Dat.StreamNotSeekable", "Stream must be seekable for format detection");

    public static Error UnknownFormat =>
        Error.Validation("Dat.UnknownFormat", "Unable to detect DAT format");

    public static Error FileNotFound(string path) =>
        Error.NotFound("Dat.FileNotFound", $"DAT file not found: {path}");

    public static Error ParseFailed(string reason) =>
        Error.Failure("Dat.ParseFailed", $"Failed to parse DAT file: {reason}");

    public static Error InvalidFormat(string reason) =>
        Error.Validation("Dat.InvalidFormat", $"Invalid DAT format: {reason}");

    public static Error MissingRequiredElement(string elementName) =>
        Error.Validation("Dat.MissingRequiredElement", $"Missing required element: {elementName}");
}
