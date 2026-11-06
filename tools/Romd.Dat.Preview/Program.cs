using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.ClrMamePro;
using Romd.Dat.Parsing.Formats.Logiqx;

// Explicit inputs only. No database/configuration/CAS access and no catalog writes.
if (args.Length == 0 || args.Contains("--help"))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Romd.Dat.Preview -- <catalog.dat> [other.dat ...]");
    Console.Error.WriteLine("Writes JSON to stdout. Each file is a separate source scope. Raw DAT files only.");
    return args.Length == 0 ? 2 : 0;
}

var reports = new List<object>();
try
{
    foreach (string path in args)
    {
        var xmlLog = new InventoryLogger<LogiqxDatFormat>();
        var textLog = new InventoryLogger<ClrMameProDatFormat>();
        var parser = new DatParser([new LogiqxDatFormat(xmlLog), new ClrMameProDatFormat(textLog)]);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("DAT exceeds the 256 MiB inventory limit.");
        string sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        var header = await parser.ReadHeaderAsync(stream);
        if (header.IsError)
            throw new InvalidDataException(string.Join("; ", header.Errors.Select(e => e.Description)));
        var names = new List<string>();
        await foreach (var game in parser.StreamGamesAsync(stream))
        {
            if (game.IsError)
                throw new InvalidDataException(string.Join("; ", game.Errors.Select(e => e.Description)));
            if (names.Count == DiscGroupingPreview.MaxEntries)
                throw new InvalidDataException("DAT exceeds the inventory entry limit.");
            names.Add(game.Value.Name);
        }
        if (xmlLog.FirstWarning is not null || textLog.FirstWarning is not null)
            throw new InvalidDataException($"Reader skipped source data: {xmlLog.FirstWarning ?? textLog.FirstWarning}");
        if (names.Count == 0)
            throw new InvalidDataException("DAT contains no game entries.");
        var preview = DiscGroupingPreview.Create($"{Path.GetFileName(path)}:{sha256}", names);
        reports.Add(new
        {
            File = Path.GetFileName(path),
            Sha256 = sha256,
            Bytes = stream.Length,
            Catalog = header.Value.Name,
            header.Value.Version,
            Preview = preview
        });
    }
    var options = new JsonSerializerOptions { WriteIndented = true };
    options.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(JsonSerializer.Serialize(new { SchemaVersion = 1, Scope = "name-only; no catalog mutations or completeness/availability claims", Sources = reports }, options));
    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or XmlException or UnauthorizedAccessException or ArgumentException)
{
    // Never emit a successful partial report when any input fails.
    Console.Error.WriteLine($"Inventory failed: {exception.Message}");
    return 1;
}
