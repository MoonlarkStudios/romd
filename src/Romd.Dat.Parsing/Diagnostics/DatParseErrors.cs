using ErrorOr;

namespace Romd.Dat.Parsing.Diagnostics;

public static class DatParseErrors
{
    public static Error StreamNotSeekable =>
        Error.Validation("Dat.StreamNotSeekable", "Stream must be seekable for format detection");

    public static Error UnknownFormat =>
        Error.Validation("Dat.UnknownFormat", "Unable to detect DAT format");

    public static Error ParseFailed(string reason) =>
        Error.Failure("Dat.ParseFailed", $"Failed to parse DAT file: {reason}");

    public static Error MissingRequiredElement(string elementName) =>
        Error.Validation("Dat.MissingRequiredElement", $"Missing required element: {elementName}");
}
