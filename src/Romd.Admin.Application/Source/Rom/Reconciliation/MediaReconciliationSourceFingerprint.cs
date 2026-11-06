using System.Security.Cryptography;
using System.Text;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Versioned, length-delimited identities. Ownership authority and unrelated decoder history are checked separately.</summary>
internal static class MediaReconciliationSourceFingerprint
{
    internal static Sha256 Compute(MediaReconciliationSources sources)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("media-reconciliation-sources/v3");
            writer.Write(sources.Layout is not null);
            if (sources.Layout is { } layout)
            {
                writer.Write((int)layout.Subject.Kind);
                writer.Write(layout.Subject.Id);
                writer.Write(layout.Revision.ToByteArray());
                writer.Write(layout.PlatformId);
                writer.Write(layout.ReleaseId);
                writer.Write(layout.CatalogFingerprint);
                writer.Write(layout.EntryPoint);
                if (layout is MediaReconciliationLayout.Catalog catalog)
                {
                    writer.Write(catalog.BindingId);
                    writer.Write(catalog.Requirement.FileFingerprint);
                    writer.Write(catalog.Requirement.Size);
                    writer.Write(catalog.Requirement.Sha1.AsSpan());
                    writer.Write(catalog.Requirement.Md5.AsSpan());
                    writer.Write(catalog.Requirement.Crc32.AsSpan());
                    writer.Write(catalog.GeneratorProfile);
                }
            }
            writer.Write(sources.ReferenceFingerprint is not null);
            if (sources.ReferenceFingerprint is { } reference)
                writer.Write(reference.AsSpan());
            writer.Write(sources.LogicalInspectionIds.Count);
            foreach (var id in sources.LogicalInspectionIds)
                writer.Write(id.ToByteArray());
            writer.Write(sources.Inputs.Count);
            foreach (var input in sources.Inputs)
            {
                writer.Write((int)input.Role);
                writer.Write(input.Path is not null);
                if (input.Path is { } path)
                    writer.Write(path);
                switch (input)
                {
                    case MediaReconciliationInput.Logical logical:
                        Graph(writer, logical.Graph);
                        writer.Write(logical.Output.Id);
                        writer.Write(logical.Output.RomId!.Value);
                        writer.Write(logical.VerificationId.ToByteArray());
                        break;
                    case MediaReconciliationInput.Stored stored:
                        writer.Write(stored.FileId);
                        CdIdentity(writer, stored.Identity);
                        break;
                    case MediaReconciliationInput.Artifact artifact:
                        Graph(writer, artifact.Graph);
                        writer.Write(artifact.VerificationId is not null);
                        if (artifact.VerificationId is { } proof)
                            writer.Write(proof.ToByteArray());
                        writer.Write(artifact.CatalogWitness is not null);
                        if (artifact.CatalogWitness is { } witness)
                        {
                            writer.Write(witness.BindingId);
                            writer.Write(witness.Revision.ToByteArray());
                            writer.Write(witness.PlatformId);
                            writer.Write(witness.Requirement.CatalogFileId);
                            writer.Write(witness.ReleaseId);
                            writer.Write(witness.CatalogFingerprint);
                            writer.Write(witness.Requirement.FileFingerprint);
                            writer.Write(witness.Requirement.Path);
                            writer.Write(witness.Requirement.Size);
                            writer.Write(witness.Requirement.Sha1.AsSpan());
                            writer.Write(witness.Requirement.Md5.AsSpan());
                            writer.Write(witness.Requirement.Crc32.AsSpan());
                            writer.Write(witness.GeneratorProfile);
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unknown captured source type.");
                }
            }
        }
        return Sha256.FromSpan(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private static void Graph(BinaryWriter writer, RomRepresentationFacts graph)
    {
        writer.Write(graph.Id);
        writer.Write(graph.FileId);
        writer.Write(graph.ArtifactSha256.AsSpan());
        writer.Write(graph.ArtifactSize);
        writer.Write((int)graph.Container.Encoding);
        writer.Write(graph.Container.Fingerprint is not null);
        if (graph.Container.Fingerprint is { } parameters)
            writer.Write(parameters.AsSpan());
        writer.Write(graph.Manifest is RomReconstructionManifest.CdTracks);
        if (graph.Manifest is RomReconstructionManifest.CdTracks cd)
        {
            writer.Write(cd.LogicalFrames);
            writer.Write(cd.Tracks.Count);
            foreach (var track in cd.Tracks)
            { writer.Write(track.Number); writer.Write(track.FirstFrame); writer.Write(track.Frames); writer.Write((int)track.Kind); }
            writer.Write(cd.Members.Count);
            foreach (var member in cd.Members)
            {
                writer.Write(member.Path);
                CdIdentity(writer, member.Identity);
                writer.Write(member is RomCdBinMember);
                if (member is RomCdBinMember bin)
                { writer.Write(bin.Tracks.Count); foreach (int track in bin.Tracks) writer.Write(track); }
                else
                    writer.Write((int)((RomCdStoredMember)member).Role);
            }
        }
        writer.Write(graph.Outputs.Count);
        foreach (var output in graph.Outputs.OrderBy(output => output.Id))
        {
            writer.Write(output.Id);
            writer.Write(output.Selector is RomOutputSelector.Member);
            if (output.Selector is RomOutputSelector.Member member)
                writer.Write(member.Ordinal);
            Identity(writer, output.Identity);
        }
        writer.Write(graph.Descriptors.Count);
        foreach (var descriptor in graph.Descriptors.OrderBy(descriptor => descriptor.Ordinal))
        { writer.Write(descriptor.Ordinal); writer.Write(descriptor.FileId); writer.Write(descriptor.Sha256.AsSpan()); writer.Write(descriptor.Size); }
        var boot = graph.CdBootInspections.OrderBy(inspection => inspection.ObservedAt).ThenBy(inspection => inspection.Id).FirstOrDefault();
        writer.Write(boot is not null);
        if (boot is not null)
        {
            writer.Write(boot.Id.ToByteArray());
            writer.Write(boot.VerificationId.ToByteArray());
            writer.Write(boot.InspectorBuild);
            writer.Write(boot.ObservedAt.UtcTicks);
            var facts = boot.Boot;
            writer.Write(facts.Profile);
            writer.Write(facts.FirstTrackIndex01);
            writer.Write(facts.VolumeBlocks);
            writer.Write(facts.RootBlock);
            writer.Write(facts.RootBytes);
            writer.Write(facts.BootPath);
            writer.Write(facts.BootBlock);
            writer.Write(facts.BootBytes);
            writer.Write(facts.EntryPoint);
            writer.Write(facts.LoadAddress);
            writer.Write(facts.LoadBytes);
        }
    }
    private static void Identity(BinaryWriter writer, RomLogicalIdentity identity)
    {
        writer.Write(identity.Size);
        writer.Write(identity.Sha256.AsSpan());
        writer.Write(identity.Sha1.AsSpan());
        writer.Write(identity.Md5.AsSpan());
        writer.Write(identity.Crc32.ToString());
    }
    private static void CdIdentity(BinaryWriter writer, RomCdFileIdentity identity)
    {
        writer.Write(identity.Size);
        writer.Write(identity.Sha256.AsSpan());
        writer.Write(identity.Sha1.AsSpan());
        writer.Write(identity.Md5.AsSpan());
        writer.Write(identity.Crc32.ToString());
    }
}
