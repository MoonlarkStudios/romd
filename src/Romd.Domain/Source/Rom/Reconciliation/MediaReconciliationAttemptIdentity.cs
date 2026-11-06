using System.Security.Cryptography;
using System.Text;
using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

public enum MediaReconciliationOperation { Inspect = 1, Verify = 2, Encode = 3, Release = 4, Adopt = 5 }

/// <summary>Only an identical observed source, operation, recipe, capabilities and limits may reuse a deterministic failure.</summary>
public sealed record MediaReconciliationAttemptIdentity
{
    public MediaReconciliationAttemptIdentity(MediaReconciliationOperation operation, RecipeId? recipe,
        Sha256 sourceFingerprint, Sha256 capabilityFingerprint, Sha256 limitsFingerprint)
    {
        if (!Enum.IsDefined(operation) || operation is (MediaReconciliationOperation.Encode or MediaReconciliationOperation.Adopt) && recipe is null ||
            sourceFingerprint.IsEmpty || capabilityFingerprint.IsEmpty || limitsFingerprint.IsEmpty)
            throw new ArgumentException("A complete bounded attempt identity is required.");
        Operation = operation;
        Recipe = recipe;
        SourceFingerprint = sourceFingerprint;
        CapabilityFingerprint = capabilityFingerprint;
        LimitsFingerprint = limitsFingerprint;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("media-reconciliation-attempt/v1");
            writer.Write((int)operation);
            writer.Write(recipe is not null);
            if (recipe is not null)
                writer.Write(recipe.Value);
            writer.Write(sourceFingerprint.AsSpan());
            writer.Write(capabilityFingerprint.AsSpan());
            writer.Write(limitsFingerprint.AsSpan());
        }
        Fingerprint = Sha256.FromSpan(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
    public MediaReconciliationOperation Operation { get; }
    public RecipeId? Recipe { get; }
    public Sha256 SourceFingerprint { get; }
    public Sha256 CapabilityFingerprint { get; }
    public Sha256 LimitsFingerprint { get; }
    public Sha256 Fingerprint { get; }
}
