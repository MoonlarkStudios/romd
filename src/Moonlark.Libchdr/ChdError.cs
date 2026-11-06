namespace Moonlark.Libchdr;

/// <summary>An error code from the pinned libchdr API.</summary>
public enum ChdError
{
    /// <summary>The operation succeeded.</summary>
    None = 0,
    /// <summary>No codec interface is available.</summary>
    NoInterface = 1,
    /// <summary>A native allocation failed.</summary>
    OutOfMemory = 2,
    /// <summary>The input is not a valid CHD file.</summary>
    InvalidFile = 3,
    /// <summary>An argument is invalid.</summary>
    InvalidParameter = 4,
    /// <summary>The input contains invalid data.</summary>
    InvalidData = 5,
    /// <summary>The source file was not found.</summary>
    FileNotFound = 6,
    /// <summary>The image requires a parent.</summary>
    RequiresParent = 7,
    /// <summary>The file cannot be written.</summary>
    FileNotWritable = 8,
    /// <summary>A source read failed.</summary>
    ReadError = 9,
    /// <summary>A destination write failed.</summary>
    WriteError = 10,
    /// <summary>A codec failed.</summary>
    CodecError = 11,
    /// <summary>The parent does not match the child.</summary>
    InvalidParent = 12,
    /// <summary>The hunk index is outside the image.</summary>
    HunkOutOfRange = 13,
    /// <summary>Decompression or its block checksum failed.</summary>
    DecompressionError = 14,
    /// <summary>Compression failed.</summary>
    CompressionError = 15,
    /// <summary>A file could not be created.</summary>
    CannotCreateFile = 16,
    /// <summary>The image cannot be verified.</summary>
    CannotVerify = 17,
    /// <summary>The operation is unsupported.</summary>
    NotSupported = 18,
    /// <summary>The requested metadata entry does not exist.</summary>
    MetadataNotFound = 19,
    /// <summary>The metadata length is invalid.</summary>
    InvalidMetadataSize = 20,
    /// <summary>The CHD version is unsupported.</summary>
    UnsupportedVersion = 21,
    /// <summary>Verification is incomplete.</summary>
    VerifyIncomplete = 22,
    /// <summary>The metadata is invalid.</summary>
    InvalidMetadata = 23,
    /// <summary>The operation is invalid in the current state.</summary>
    InvalidState = 24,
    /// <summary>An operation is already pending.</summary>
    OperationPending = 25,
    /// <summary>No asynchronous operation is pending.</summary>
    NoAsyncOperation = 26,
    /// <summary>The media format is unsupported.</summary>
    UnsupportedFormat = 27,
}
