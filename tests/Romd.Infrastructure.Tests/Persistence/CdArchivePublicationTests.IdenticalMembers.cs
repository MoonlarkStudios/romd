using Romd.PostgreSql.TestSupport;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Concurrency;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Catalog;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Fact]
    public async Task Publication_IdenticalBins_PreservesDistinctOutputsAndMapsEveryLayoutResponse()
    {
        // Both paths deliberately own the same logical ROM; their archive selectors must remain distinct.
        originals["audio.bin"] = originals["track.bin"];
        var audio = Identity(originals["track.bin"]);
        await using (var setup = database.CreateContext())
        {
            await setup.OriginalDiscLayoutFiles.Where(row => row.Path == "audio.bin")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, 1).SetProperty(row => row.FileId, 1));
            // This decision covers a BIN-only catalog; the general fixture also seeds an unmatched CUE template.
            await setup.CatalogReleaseFiles.Where(row => row.Name == "catalog.cue").ExecuteDeleteAsync();
            await setup.CatalogReleaseFiles.Where(row => row.Name == "catalog-audio.bin")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Sha1, audio.Sha1).SetProperty(row => row.Size, audio.Size));
        }
        var measured = Measure(128, [new(1, 0, 64, RomCdTrackKind.Mode2Raw), new(2, 64, 64, RomCdTrackKind.Audio)],
            [new RomCdBinMember("track.bin",audio,audio,[1]), new RomCdBinMember("audio.bin",audio,audio,[2]),
                new RomCdStoredMember("disc.cue",Identity(originals["disc.cue"]),RomCdStoredRole.Cue)],
            Identity(Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray()),
            Romd.Domain.Hashing.Sha1.Parse(new string('b', 40)), Decoder);
        await PublishLayout(1, measured, 9, new() { ["disc.cue"] = 3 }, new() { ["track.bin"] = 1, ["audio.bin"] = 1 });

        await using var db = database.CreateContext();
        var graph = await ArchiveAsync(db);
        graph.Outputs.Count.ShouldBe(2);
        graph.Outputs.Select(output => output.Id).Distinct().Count().ShouldBe(2);
        graph.Outputs.Select(output => output.RomId).ShouldBe([1, 1]);
        graph.Outputs.Select(output => ((RomOutputSelector.Member)output.Selector).Ordinal).Order().ShouldBe([0, 1]);
        graph.Creations.Single().Sources.Select(source => source.TargetOutputId).Distinct().Count().ShouldBe(2);
        var layouts = new OriginalDiscLayoutService(db, new FileMutationLock(db));
        var get = (await layouts.GetAsync(1, default)).Value;
        get.Dependencies.Count.ShouldBe(2);
        get.Dependencies.Select(file => file.RomId).ShouldBe([IdCoder.Encode(1), IdCoder.Encode(1)]);
        get.Dependencies.Select(file => file.Sha256).ShouldBe([audio.Sha256.ToString(), audio.Sha256.ToString()]);
        (await layouts.ListAsync(null, null, 100, default)).Value.Items.Single().Dependencies.ShouldBe(get.Dependencies);
        var decision = await new DecideOriginalDiscLayoutCommandHandler(layouts, new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance)
            .HandleAsync(new(1, new("confirm", IdCoder.Encode(1),
                [new("track.bin", IdCoder.Encode(1)), new("audio.bin", IdCoder.Encode(1))], "Reviewed identical members"), RevisionPrecondition.For(get.Revision.ToString()), actor));
        decision.IsError.ShouldBeFalse(decision.IsError ? string.Join(';', decision.Errors) : "");
        decision.Value.Dependencies.ShouldBe(get.Dependencies);
    }

}
