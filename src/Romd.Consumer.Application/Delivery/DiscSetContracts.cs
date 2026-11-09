using System.Security.Cryptography;
using System.Text.Json;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Delivery;

public static class DiscSetContracts
{
    public static IReadOnlyList<int> MissingExpected(ConsumerDiscSetPlan plan) => plan.ExpectedDiscCount is { } expected
        ? Enumerable.Range(1, expected).Except(plan.Discs.Select(x => x.Ordinal)).ToArray() : [];

    public static ConsumerDiscSetStatus Status(ConsumerDiscSetPlan plan, Guid server)
    {
        string observation = Hash(new
        {
            Server = server,
            plan.Id,
            plan.Revision,
            plan.ExpectedDiscCount,
            Discs = plan.Discs.Select(x => new
            {
                x.Ordinal,
                x.Release.ReleaseId,
                x.Readiness,
                x.EntryPoint,
                LayoutId = x.OriginalLayout?.Id,
                LayoutRevision = x.OriginalLayout?.Revision,
                Files = x.OriginalLayout?.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { f.Path, Hash = f.Sha256.ToString(), f.SizeBytes }).ToArray(),
                Catalog = x.Release.Items.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => new
                {
                    f.RelativePath,
                    Hash = f.Sha256?.ToString(),
                    f.SizeBytes,
                    f.IsAvailable,
                    Derived = f.Source is ConsumerReleaseManifestSource.Derived derived ? new
                    {
                        derived.Descriptor.Id,
                        derived.Descriptor.Revision,
                        derived.Descriptor.CatalogFileId,
                        derived.Descriptor.RepresentationId,
                        derived.Descriptor.MemberOrdinal,
                        derived.Descriptor.FileId,
                        derived.Descriptor.GeneratorProfile
                    } : null
                })
            })
        });
        return new(1, server.ToString("D"), IdCoder.Encode(plan.Id), plan.Revision, IdCoder.Encode(plan.Discs[0].Release.TitleId),
            plan.Discs[0].Release.PlatformShortName, plan.Name, observation, plan.ExpectedDiscCount, MissingExpected(plan),
            plan.Discs.Select(x => new ConsumerDiscSetStatusDisc(IdCoder.Encode(x.Release.ReleaseId), x.Ordinal,
                x.Release.ReleaseName, x.OriginalLayout is { } layout ? IdCoder.Encode(layout.Id) : null,
                x.OriginalLayout?.Revision.ToString("D"), x.Readiness)).ToArray())
        { ArchiveFormats = ConsumerDiscSetArchiveFormats.Choices };
    }
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
