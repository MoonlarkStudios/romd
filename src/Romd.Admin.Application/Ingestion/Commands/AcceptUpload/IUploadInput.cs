namespace Romd.Admin.Application.Ingestion.Commands.AcceptUpload;

/// <summary>
///     Captured multipart metadata and a fresh read stream for each purpose. The handler owns
///     streams it opens; the request retains the multipart input itself.
/// </summary>
public interface IUploadInput
{
    string FileName { get; }
    long Length { get; }
    Stream OpenRead();
}
