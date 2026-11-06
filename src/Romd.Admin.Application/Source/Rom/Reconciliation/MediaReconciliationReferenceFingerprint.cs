using System.Security.Cryptography;
using System.Text;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Current published reference assertions, including an empty result. No derived eligibility is persisted.</summary>
public static class MediaReconciliationReferenceFingerprint
{
    public static Sha256 Compute(IReadOnlyList<RomDiscMediaReferenceRecord> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("media-reconciliation-references/v1");
            writer.Write(references.Count);
            foreach (var reference in references.OrderBy(row => row.Id))
            {
                writer.Write(reference.Id.ToByteArray());
                writer.Write((int)reference.System);
                writer.Write((int)reference.Media);
                writer.Write(reference.FileCount);
                writer.Write(reference.LogicalBytes);
                writer.Write(reference.Sha1.AsSpan());
                writer.Write(reference.Md5.AsSpan());
                writer.Write(reference.Crc32.AsSpan());
                writer.Write(reference.RawReferenceBytes);
                writer.Write(reference.RawReferenceSha256.AsSpan());
                writer.Write(reference.ReferenceSource);
                writer.Write(reference.ParserProfile);
                writer.Write(reference.AcceptedSourceProvenance);
                writer.Write(reference.RetrievedAt.UtcTicks);
            }
        }
        return Sha256.FromSpan(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
}
