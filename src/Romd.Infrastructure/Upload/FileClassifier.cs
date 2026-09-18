using System.Text;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;

namespace Romd.Infrastructure.Upload;

/// <summary>
///     Classifies files based on content signatures and file extensions.
/// </summary>
public sealed class FileClassifier : IFileClassifier
{
    private readonly IArchiveExtractorResolver _archiveResolver;

    // Magic bytes for common archive formats
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04]; // PK..
    private static readonly byte[] ZipEmptySignature = [0x50, 0x4B, 0x05, 0x06]; // Empty ZIP
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]; // 7z
    private static readonly byte[] RarSignature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]; // Rar!
    private static readonly byte[] GzipSignature = [0x1F, 0x8B]; // gzip

    // Known DAT file extensions
    private static readonly HashSet<string> DatExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dat", ".xml"
    };

    // Buffer size for reading file headers
    private const int HeaderBufferSize = 512;

    public FileClassifier(IArchiveExtractorResolver archiveResolver)
    {
        _archiveResolver = archiveResolver;
    }

    public async Task<FileClassification> ClassifyAsync(
        Stream content,
        string filename,
        CancellationToken cancellationToken = default)
    {
        if (!content.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable for classification", nameof(content));
        }

        var originalPosition = content.Position;

        try
        {
            content.Position = 0;

            // Read header bytes for magic byte detection
            var buffer = new byte[HeaderBufferSize];
            var bytesRead = await content.ReadAsync(buffer.AsMemory(0, HeaderBufferSize), cancellationToken);

            if (bytesRead == 0)
            {
                return new FileClassification(FileType.Unknown, null, 0f);
            }

            // Check for archive signatures first (highest priority - we need to extract these)
            var archiveResult = ClassifyAsArchive(buffer, bytesRead, filename);
            if (archiveResult.Type == FileType.Archive)
            {
                return archiveResult;
            }

            // Check for DAT content signatures
            var datResult = ClassifyAsDat(buffer, bytesRead, filename);
            if (datResult.Type == FileType.Dat)
            {
                return datResult;
            }

            // Check extension-based classification
            var extension = Path.GetExtension(filename);

            // Archives by extension (fallback for archives without magic bytes at start)
            if (_archiveResolver.IsArchive(filename))
            {
                return new FileClassification(FileType.Archive, extension, 0.6f);
            }

            // DAT by extension
            if (DatExtensions.Contains(extension))
            {
                return new FileClassification(FileType.Dat, "extension", 0.5f);
            }

            // Everything else that has a file extension is assumed to be a ROM
            if (!string.IsNullOrEmpty(extension))
            {
                return new FileClassification(FileType.Rom, "extension", 0.7f);
            }

            return new FileClassification(FileType.Unknown, null, 0f);
        }
        finally
        {
            // Always reset stream position
            content.Position = originalPosition;
        }
    }

    private FileClassification ClassifyAsArchive(byte[] buffer, int bytesRead, string filename)
    {
        if (bytesRead >= 4)
        {
            // ZIP format
            if (StartsWith(buffer, ZipSignature) || StartsWith(buffer, ZipEmptySignature))
            {
                return new FileClassification(FileType.Archive, "zip", 0.95f);
            }

            // 7z format
            if (bytesRead >= 6 && StartsWith(buffer, SevenZipSignature))
            {
                return new FileClassification(FileType.Archive, "7z", 0.95f);
            }

            // RAR format
            if (bytesRead >= 6 && StartsWith(buffer, RarSignature))
            {
                return new FileClassification(FileType.Archive, "rar", 0.95f);
            }

            // GZIP format
            if (StartsWith(buffer, GzipSignature))
            {
                return new FileClassification(FileType.Archive, "gzip", 0.95f);
            }
        }

        return new FileClassification(FileType.Unknown, null, 0f);
    }

    private static FileClassification ClassifyAsDat(byte[] buffer, int bytesRead, string filename)
    {
        // Convert to string for text pattern matching (handle BOM)
        var text = GetTextContent(buffer, bytesRead);
        var trimmed = text.TrimStart();

        // Check for XML DAT patterns
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            // Look for datafile or dat root element
            if (ContainsDatRootElement(trimmed))
            {
                return new FileClassification(FileType.Dat, "xml/logiqx", 0.95f);
            }

            // Generic XML - might be DAT, lower confidence
            return new FileClassification(FileType.Dat, "xml", 0.7f);
        }

        // DOCTYPE for DAT files
        if (trimmed.StartsWith("<!DOCTYPE datafile", StringComparison.OrdinalIgnoreCase))
        {
            return new FileClassification(FileType.Dat, "xml/logiqx", 0.95f);
        }

        // Direct root element (no XML declaration)
        if (trimmed.StartsWith("<datafile", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<dat", StringComparison.OrdinalIgnoreCase))
        {
            return new FileClassification(FileType.Dat, "xml/logiqx", 0.9f);
        }

        return new FileClassification(FileType.Unknown, null, 0f);
    }

    private static string GetTextContent(byte[] buffer, int bytesRead)
    {
        // Handle UTF-8 BOM
        if (bytesRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(buffer, 3, bytesRead - 3);
        }

        // Handle UTF-16 LE BOM
        if (bytesRead >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(buffer, 2, bytesRead - 2);
        }

        // Handle UTF-16 BE BOM
        if (bytesRead >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(buffer, 2, bytesRead - 2);
        }

        // Default to UTF-8
        return Encoding.UTF8.GetString(buffer, 0, bytesRead);
    }

    private static bool ContainsDatRootElement(string text)
    {
        // Look for datafile or dat element within the first part of the XML
        return text.Contains("<datafile", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<dat ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<dat>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWith(byte[] buffer, byte[] signature)
    {
        if (buffer.Length < signature.Length)
            return false;

        for (int i = 0; i < signature.Length; i++)
        {
            if (buffer[i] != signature[i])
                return false;
        }

        return true;
    }
}
