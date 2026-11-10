using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Search;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Pins what every delivery surface offers for each kind of stored media, so media policy can move into its
///     registrations without changing delivery. Admin surfaces run through the real admin host and consumer surfaces
///     through the real consumer host, both over one PostgreSQL catalog and content store; each surface derives its
///     own system context from that catalog. CHD admission is the production class over synthetic resident decoder
///     facts, so it qualifies no native build. Admin downloads and exports capture their bound targets; consumer
///     grants stream real bytes for every format stored as-is, while originals that would need a decoder are not read.
/// </summary>
public sealed class MediaDeliveryCharacterizationTests(MediaDeliveryCharacterizationTests.MediaHost host)
    : IClassFixture<MediaDeliveryCharacterizationTests.MediaHost>
{
    private static readonly string[] Formats = ["original", "rvz", "chd"];

    /// <summary>Today's delivery, one line per media case and surface. A changed line is a delivery change.</summary>
    private static readonly string[] Expected =
    [
        "exports | all-catalog titles | original=[GC Fallback, GC Game, GC RVZ, PS2 CHD, PS2 Game, PS2 Old CHD, SNES Game, Shared CHD, Shared CHD (Other), Wii RVZ] rvz=[GC Fallback, GC Game, GC RVZ, PS2 CHD, PS2 Game, PS2 Old CHD, SNES Game, Shared CHD, Shared CHD (Other), Wii RVZ] chd=[GC Fallback, GC Game, GC RVZ, PS2 CHD, PS2 Game, PS2 Old CHD, SNES Game, Shared CHD, Shared CHD (Other), Wii RVZ]",
        "exports | library titles | original=[GC Fallback, GC Game, GC RVZ, PS2 CHD, PS2 Game, PS2 Old CHD, SNES Game, Shared CHD, Wii RVZ] rvz=[GC Fallback, GC Game, GC RVZ, PS2 CHD, PS2 Game, PS2 Old CHD, SNES Game, Shared CHD, Wii RVZ] chd=[GC Fallback, GC Game, GC RVZ, PS2 CHD, PS2 Game, PS2 Old CHD, SNES Game, Shared CHD, Wii RVZ]",
        "exports | library formats | [original:Original, rvz:RVZ, chd:CHD]",
        "gc iso | admin rom formats | [original:available:GC Game.iso]",
        "gc iso | admin direct download | original=GC Game.iso:original rvz=409 chd=409",
        "gc iso | admin file ticket | original=[GC Game.iso:original] rvz=409 chd=409",
        "gc iso | admin title zip | original=[GC Game.iso:original] rvz=[GC Game.iso:original] chd=[GC Game.iso:original]",
        "gc iso | admin release zip | original=[GC Game.iso:original] rvz=[GC Game.iso:original] chd=[GC Game.iso:original]",
        "gc iso | admin archive formats | title=[original:Original] release=[original:Original]",
        "gc iso | all-catalog export | original=[GC Game.iso:original] rvz=[GC Game.iso:original] chd=[GC Game.iso:original]",
        "gc iso | library export | original=[GC Game.iso:original] rvz=[GC Game.iso:original] chd=[GC Game.iso:original]",
        "gc iso | consumer manifest | launch=GC Game.iso [original:available:GC Game.iso runtime=single_rom:GC Game.iso grant=bytes]",
        "gc iso | vfs stored | [GC Game.iso:original]",
        "gc iso | vfs original | [GC Game.iso:original]",
        "gc rvz canonical | admin rom formats | [original:available:GC RVZ.iso, rvz:available:GC RVZ.rvz[dolphin]]",
        "gc rvz canonical | admin direct download | original=GC RVZ.iso:original rvz=GC RVZ.rvz:rvz chd=409",
        "gc rvz canonical | admin file ticket | original=[GC RVZ.iso:original] rvz=[GC RVZ.rvz:rvz] chd=409",
        "gc rvz canonical | admin title zip | original=[GC RVZ.iso:original] rvz=[GC RVZ.rvz:rvz] chd=[GC RVZ.iso:original]",
        "gc rvz canonical | admin release zip | original=[GC RVZ.iso:original] rvz=[GC RVZ.rvz:rvz] chd=[GC RVZ.iso:original]",
        "gc rvz canonical | admin archive formats | title=[original:Original, rvz:RVZ] release=[original:Original, rvz:RVZ]",
        "gc rvz canonical | all-catalog export | original=[GC RVZ.iso:original] rvz=[GC RVZ.rvz:rvz] chd=[GC RVZ.iso:original]",
        "gc rvz canonical | library export | original=[GC RVZ.iso:original] rvz=[GC RVZ.rvz:rvz] chd=[GC RVZ.iso:original]",
        "gc rvz canonical | consumer manifest | launch=GC RVZ.iso [original:available:GC RVZ.iso runtime=single_rom:GC RVZ.iso grant=decode not read, rvz:available:GC RVZ.rvz[dolphin] runtime=single_rom:GC RVZ.rvz grant=bytes]",
        "gc rvz canonical | vfs stored | [GC RVZ.rvz:rvz]",
        "gc rvz canonical | vfs original | [GC RVZ.iso:original]",
        "wii rvz non-canonical | admin rom formats | [original:available:Wii RVZ.iso, rvz:available:Wii RVZ.rvz[dolphin]]",
        "wii rvz non-canonical | admin direct download | original=Wii RVZ.iso:original rvz=Wii RVZ.rvz:rvz chd=409",
        "wii rvz non-canonical | admin file ticket | original=[Wii RVZ.iso:original] rvz=[Wii RVZ.rvz:rvz] chd=409",
        "wii rvz non-canonical | admin title zip | original=[Wii RVZ.iso:original] rvz=[Wii RVZ.rvz:rvz] chd=[Wii RVZ.iso:original]",
        "wii rvz non-canonical | admin release zip | original=[Wii RVZ.iso:original] rvz=[Wii RVZ.rvz:rvz] chd=[Wii RVZ.iso:original]",
        "wii rvz non-canonical | admin archive formats | title=[original:Original, rvz:RVZ] release=[original:Original, rvz:RVZ]",
        "wii rvz non-canonical | all-catalog export | original=[Wii RVZ.iso:original] rvz=[Wii RVZ.rvz:rvz] chd=[Wii RVZ.iso:original]",
        "wii rvz non-canonical | library export | original=[Wii RVZ.iso:original] rvz=[Wii RVZ.rvz:rvz] chd=[Wii RVZ.iso:original]",
        "wii rvz non-canonical | consumer manifest | launch=Wii RVZ.iso [original:available:Wii RVZ.iso runtime=single_rom:Wii RVZ.iso grant=decode not read, rvz:available:Wii RVZ.rvz[dolphin] runtime=single_rom:Wii RVZ.rvz grant=bytes]",
        "wii rvz non-canonical | vfs stored | [Wii RVZ.rvz:rvz]",
        "wii rvz non-canonical | vfs original | [Wii RVZ.iso:original]",
        "ps2 iso | admin rom formats | [original:available:PS2 Game.iso]",
        "ps2 iso | admin direct download | original=PS2 Game.iso:original rvz=409 chd=409",
        "ps2 iso | admin file ticket | original=[PS2 Game.iso:original] rvz=409 chd=409",
        "ps2 iso | admin title zip | original=[PS2 Game.iso:original] rvz=[PS2 Game.iso:original] chd=[PS2 Game.iso:original]",
        "ps2 iso | admin release zip | original=[PS2 Game.iso:original] rvz=[PS2 Game.iso:original] chd=[PS2 Game.iso:original]",
        "ps2 iso | admin archive formats | title=[original:Original] release=[original:Original]",
        "ps2 iso | all-catalog export | original=[PS2 Game.iso:original] rvz=[PS2 Game.iso:original] chd=[PS2 Game.iso:original]",
        "ps2 iso | library export | original=[PS2 Game.iso:original] rvz=[PS2 Game.iso:original] chd=[PS2 Game.iso:original]",
        "ps2 iso | consumer manifest | launch=PS2 Game.iso [original:available:PS2 Game.iso runtime=single_rom:PS2 Game.iso grant=bytes]",
        "ps2 iso | vfs stored | [PS2 Game.iso:original]",
        "ps2 iso | vfs original | [PS2 Game.iso:original]",
        "ps2 chd current recipe | admin rom formats | [original:available:PS2 CHD.iso, chd:available:PS2 CHD.chd[pcsx2]]",
        "ps2 chd current recipe | admin direct download | original=PS2 CHD.iso:original rvz=409 chd=PS2 CHD.chd:chd",
        "ps2 chd current recipe | admin file ticket | original=[PS2 CHD.iso:original] rvz=409 chd=[PS2 CHD.chd:chd]",
        "ps2 chd current recipe | admin title zip | original=[PS2 CHD.iso:original] rvz=[PS2 CHD.iso:original] chd=[PS2 CHD.chd:chd]",
        "ps2 chd current recipe | admin release zip | original=[PS2 CHD.iso:original] rvz=[PS2 CHD.iso:original] chd=[PS2 CHD.chd:chd]",
        "ps2 chd current recipe | admin archive formats | title=[original:Original, chd:CHD] release=[original:Original, chd:CHD]",
        "ps2 chd current recipe | all-catalog export | original=[PS2 CHD.iso:original] rvz=[PS2 CHD.iso:original] chd=[PS2 CHD.chd:chd]",
        "ps2 chd current recipe | library export | original=[PS2 CHD.iso:original] rvz=[PS2 CHD.iso:original] chd=[PS2 CHD.chd:chd]",
        "ps2 chd current recipe | consumer manifest | launch=PS2 CHD.iso [original:available:PS2 CHD.iso runtime=single_rom:PS2 CHD.iso grant=decode not read, chd:available:PS2 CHD.chd[pcsx2] runtime=single_rom:PS2 CHD.chd grant=bytes]",
        "ps2 chd current recipe | vfs stored | [PS2 CHD.chd:chd]",
        "ps2 chd current recipe | vfs original | [PS2 CHD.iso:original]",
        "ps2 chd earlier recipe | admin rom formats | [original:available:PS2 Old CHD.iso, chd:available:PS2 Old CHD.chd[pcsx2]]",
        "ps2 chd earlier recipe | admin direct download | original=PS2 Old CHD.iso:original rvz=409 chd=PS2 Old CHD.chd:chd",
        "ps2 chd earlier recipe | admin file ticket | original=[PS2 Old CHD.iso:original] rvz=409 chd=[PS2 Old CHD.chd:chd]",
        "ps2 chd earlier recipe | admin title zip | original=[PS2 Old CHD.iso:original] rvz=[PS2 Old CHD.iso:original] chd=[PS2 Old CHD.chd:chd]",
        "ps2 chd earlier recipe | admin release zip | original=[PS2 Old CHD.iso:original] rvz=[PS2 Old CHD.iso:original] chd=[PS2 Old CHD.chd:chd]",
        "ps2 chd earlier recipe | admin archive formats | title=[original:Original, chd:CHD] release=[original:Original, chd:CHD]",
        "ps2 chd earlier recipe | all-catalog export | original=[PS2 Old CHD.iso:original] rvz=[PS2 Old CHD.iso:original] chd=[PS2 Old CHD.chd:chd]",
        "ps2 chd earlier recipe | library export | original=[PS2 Old CHD.iso:original] rvz=[PS2 Old CHD.iso:original] chd=[PS2 Old CHD.chd:chd]",
        "ps2 chd earlier recipe | consumer manifest | launch=PS2 Old CHD.iso [original:available:PS2 Old CHD.iso runtime=single_rom:PS2 Old CHD.iso grant=decode not read, chd:available:PS2 Old CHD.chd[pcsx2] runtime=single_rom:PS2 Old CHD.chd grant=bytes]",
        "ps2 chd earlier recipe | vfs stored | [PS2 Old CHD.iso:original]",
        "ps2 chd earlier recipe | vfs original | [PS2 Old CHD.iso:original]",
        "snes cartridge | admin rom formats | [original:available:SNES Game.sfc]",
        "snes cartridge | admin direct download | original=SNES Game.sfc:original rvz=409 chd=409",
        "snes cartridge | admin file ticket | original=[SNES Game.sfc:original] rvz=409 chd=409",
        "snes cartridge | admin title zip | original=[SNES Game.sfc:original] rvz=[SNES Game.sfc:original] chd=[SNES Game.sfc:original]",
        "snes cartridge | admin release zip | original=[SNES Game.sfc:original] rvz=[SNES Game.sfc:original] chd=[SNES Game.sfc:original]",
        "snes cartridge | admin archive formats | title=[original:Original] release=[original:Original]",
        "snes cartridge | all-catalog export | original=[SNES Game.sfc:original] rvz=[SNES Game.sfc:original] chd=[SNES Game.sfc:original]",
        "snes cartridge | library export | original=[SNES Game.sfc:original] rvz=[SNES Game.sfc:original] chd=[SNES Game.sfc:original]",
        "snes cartridge | consumer manifest | launch=SNES Game.sfc [original:available:SNES Game.sfc grant=bytes]",
        "snes cartridge | vfs stored | [SNES Game.sfc:original]",
        "snes cartridge | vfs original | [SNES Game.sfc:original]",
        "ps2 chd matched on two platforms | admin rom formats | [original:available:Shared CHD.iso, chd:available:Shared CHD.chd]",
        "ps2 chd matched on two platforms | admin direct download | original=Shared CHD.iso:original rvz=409 chd=Shared CHD.chd:chd",
        "ps2 chd matched on two platforms | admin file ticket | original=[Shared CHD.iso:original] rvz=409 chd=[Shared CHD.chd:chd]",
        "ps2 chd matched on two platforms | admin title zip | original=[Shared CHD.iso:original] rvz=[Shared CHD.iso:original] chd=[Shared CHD.chd:chd]",
        "ps2 chd matched on two platforms | admin release zip | original=[Shared CHD.iso:original] rvz=[Shared CHD.iso:original] chd=[Shared CHD.chd:chd]",
        "ps2 chd matched on two platforms | admin archive formats | title=[original:Original, chd:CHD] release=[original:Original, chd:CHD]",
        "ps2 chd matched on two platforms | all-catalog export | original=[Shared CHD.iso:original] rvz=[Shared CHD.iso:original] chd=[Shared CHD.chd:chd]",
        "ps2 chd matched on two platforms | library export | original=[Shared CHD.iso:original] rvz=[Shared CHD.iso:original] chd=[Shared CHD.chd:chd]",
        "ps2 chd matched on two platforms | consumer manifest | launch=Shared CHD.iso [original:available:Shared CHD.iso runtime=single_rom:Shared CHD.iso grant=decode not read, chd:available:Shared CHD.chd[pcsx2] runtime=single_rom:Shared CHD.chd grant=bytes]",
        "ps2 chd matched on two platforms | vfs stored | [Shared CHD.chd:chd]",
        "ps2 chd matched on two platforms | vfs original | [Shared CHD.iso:original]",
        "fallback with encoded holding | admin rom formats | [original:available:GC Fallback.iso, rvz:unsupported]",
        "fallback with encoded holding | admin direct download | original=GC Fallback.iso:original rvz=409 chd=409",
        "fallback with encoded holding | admin file ticket | original=[GC Fallback.iso:original] rvz=409 chd=409",
        "fallback with encoded holding | admin title zip | original=[GC Fallback.iso:original] rvz=[GC Fallback.iso:original] chd=[GC Fallback.iso:original]",
        "fallback with encoded holding | admin release zip | original=[GC Fallback.iso:original] rvz=[GC Fallback.iso:original] chd=[GC Fallback.iso:original]",
        "fallback with encoded holding | admin archive formats | title=[original:Original] release=[original:Original]",
        "fallback with encoded holding | all-catalog export | original=[GC Fallback.iso:original] rvz=[GC Fallback.iso:original] chd=[GC Fallback.iso:original]",
        "fallback with encoded holding | library export | original=[GC Fallback.iso:original] rvz=[GC Fallback.iso:original] chd=[GC Fallback.iso:original]",
        "fallback with encoded holding | consumer manifest | launch=GC Fallback.iso [original:available:GC Fallback.iso grant=bytes, rvz:unsupported:GC Fallback.rvz grant=none]",
        "fallback with encoded holding | vfs stored | [GC Fallback.iso:original]",
        "fallback with encoded holding | vfs original | [GC Fallback.iso:original]"
    ];

    [Fact]
    public async Task DeliverySurfaces_EachStoredMediaCase_OfferTodaysFormats()
    {
        var catalogExports = await ExportEntriesAsync(format => ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, MediaHost.UserId, format));
        var libraryExports = await ExportEntriesAsync(format =>
            ExportJob.CreateLibrary(MediaHost.LibraryId, authorizedMaterializationGeneration: 0, createdAt: DateTimeOffset.UtcNow, MediaHost.UserId, format));
        var observed = new List<string>
        {
            $"exports | all-catalog titles | {ExportTitles(catalogExports)}",
            $"exports | library titles | {ExportTitles(libraryExports)}",
            $"exports | library formats | {Describe(await LibraryExportFormatsAsync())}"
        };
        foreach (var media in MediaHost.Cases)
        {
            observed.Add(Line(media, "admin rom formats", await RomFormatsAsync(media)));
            observed.Add(Line(media, "admin direct download", await PerFormatAsync(format => DirectDownloadAsync(media, format))));
            observed.Add(Line(media, "admin file ticket", await PerFormatAsync(format => TicketAsync("Rom", media.RomId, format))));
            observed.Add(Line(media, "admin title zip", await PerFormatAsync(format => TicketAsync("Title", media.TitleId, format))));
            observed.Add(Line(media, "admin release zip", await PerFormatAsync(format => TicketAsync("Release", media.ReleaseId, format))));
            observed.Add(Line(media, "admin archive formats", $"title={Describe(await TitleArchiveFormatsAsync(media.TitleId))} " +
                $"release={Describe(await ReleaseArchiveFormatsAsync(media.TitleId, media.ReleaseId))}"));
            observed.Add(Line(media, "all-catalog export", ExportedTitle(catalogExports, media.TitleName)));
            observed.Add(Line(media, "library export", ExportedTitle(libraryExports, media.TitleName)));
            observed.Add(Line(media, "consumer manifest", await ManifestAsync(media)));
            observed.Add(Line(media, "vfs stored", await VfsAsync(media, VfsFormat.Stored)));
            observed.Add(Line(media, "vfs original", await VfsAsync(media, VfsFormat.Original)));
        }

        observed.ShouldBe(Expected);
    }

    [Fact]
    public async Task VfsStored_EarlierRecipeChdAndNonCanonicalRvz_KeepTodaysAsymmetry()
    {
        (await VfsAsync(MediaHost.Case("ps2 chd earlier recipe"), VfsFormat.Stored)).ShouldBe("[PS2 Old CHD.iso:original]");
        (await VfsAsync(MediaHost.Case("ps2 chd current recipe"), VfsFormat.Stored)).ShouldBe("[PS2 CHD.chd:chd]");
        (await VfsAsync(MediaHost.Case("wii rvz non-canonical"), VfsFormat.Stored)).ShouldBe("[Wii RVZ.rvz:rvz]");
    }

    [Fact]
    public async Task DeliverySurfaces_VerifiedDvdChdOnAContradictingPlatform_OfferBytesWithoutRuntimeClaims()
    {
        var isolated = new MediaHost();
        try
        {
            await isolated.InitializeAsync();
            var media = await isolated.PublishContradictedTwinAsync();
            var surfaces = new MediaDeliveryCharacterizationTests(isolated);

            string[] observed =
            [
                Line(media, "consumer manifest", await surfaces.ManifestAsync(media)),
                Line(media, "vfs stored", await surfaces.VfsAsync(media, VfsFormat.Stored)),
                Line(media, "vfs original", await surfaces.VfsAsync(media, VfsFormat.Original))
            ];

            observed.ShouldBe([
                "dvd chd on contradicted platform | consumer manifest | launch=Shared CHD.iso [original:available:Shared CHD.iso grant=decode not read, chd:available:Shared CHD.chd grant=bytes]",
                "dvd chd on contradicted platform | vfs stored | [Shared CHD.iso:original]",
                "dvd chd on contradicted platform | vfs original | [Shared CHD.iso:original]"
            ]);
        }
        finally { await isolated.DisposeAsync(); }
    }

    [Fact]
    public async Task ArchiveFormats_EachStoredMediaCase_EqualTheFormatsItsArchivesBind()
    {
        foreach (var media in MediaHost.Cases)
        {
            var title = (await TitleArchiveFormatsAsync(media.TitleId)).Select(Wire).ToArray();
            title.ShouldBe(await BoundFormatsAsync("Title", media.TitleId), media.Name);
            await AssertFallsBackToOriginalsAsync("Title", media.TitleId, title);
            var release = (await ReleaseArchiveFormatsAsync(media.TitleId, media.ReleaseId)).Select(Wire).ToArray();
            release.ShouldBe(await BoundFormatsAsync("Release", media.ReleaseId), media.Name);
            await AssertFallsBackToOriginalsAsync("Release", media.ReleaseId, release);
        }
    }

    /// <summary>A library user's title ZIP holds their library's default release, and only its releases are downloadable.</summary>
    [Fact]
    public async Task ArchiveFormats_LibraryUser_EqualTheFormatsTheirArchivesBind()
    {
        foreach (var media in MediaHost.Cases)
        {
            var title = (await TitleArchiveFormatsAsync(media.TitleId, libraryUser: true)).Select(Wire).ToArray();
            title.ShouldBe(await BoundFormatsAsync("Title", media.TitleId, libraryUser: true), media.Name);
            var release = (await ReleaseArchiveFormatsAsync(media.TitleId, media.ReleaseId, libraryUser: true)).Select(Wire).ToArray();
            release.ShouldBe(await BoundFormatsAsync("Release", media.ReleaseId, libraryUser: true), media.Name);
            // The library holds each case's only release, so its user is offered what the catalog offers.
            title.ShouldBe((await TitleArchiveFormatsAsync(media.TitleId)).Select(Wire), media.Name);
            release.ShouldBe((await ReleaseArchiveFormatsAsync(media.TitleId, media.ReleaseId)).Select(Wire), media.Name);
        }

        (await ReleaseArchiveFormatsAsync(MediaHost.MixedTitleId, MediaHost.MixedReleaseId, libraryUser: true))
            .ShouldBe(["original:Original"]);
        foreach (string format in Formats)
            (await TicketPlanAsync("Release", MediaHost.MixedReleaseId, format, libraryUser: true)).ShouldBeNull(format);
    }

    /// <summary>
    ///     The CHD's PlayStation twin title is a platform its DVD evidence contradicts, so it resolves no media kind, yet its
    ///     ZIP binds the verified CHD and the decoded original: bytes are admitted from evidence alone. Its DAT entry
    ///     asserts no catalog release, so that release has no ZIP.
    /// </summary>
    [Fact]
    public async Task ArchiveFormats_TitleOnAPlatformTheEvidenceContradicts_BindsTheVerifiedChdAndItsOriginal()
    {
        int twin = MediaHost.Case("ps2 chd matched on two platforms").TitleId + MediaHost.OtherPlatformTitleOffset;

        using var title = await GetJsonAsync($"/api/titles/{IdCoder.Encode(twin)}");
        Choices(title.RootElement).ShouldBe(["original:Original", "chd:CHD"]);
        var release = title.RootElement.GetProperty("releases").GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        release.GetProperty("catalogReleaseId").ValueKind.ShouldBe(JsonValueKind.Null);
        Choices(release).ShouldBe(["original:Original"]);
        (await PerFormatAsync(format => TicketAsync("Title", twin, format)))
            .ShouldBe("original=[Shared CHD.iso:original] rvz=[Shared CHD.iso:original] chd=[Shared CHD.chd:chd]");
    }

    [Fact]
    public async Task ArchiveFormats_ReleaseOfMixedMembers_ListTheUnionItsArchivesBind()
    {
        string[] union = ["original:Original", "rvz:RVZ", "chd:CHD"];

        (await ReleaseArchiveFormatsAsync(MediaHost.MixedTitleId, MediaHost.MixedReleaseId)).ShouldBe(union);
        (await BoundFormatsAsync("Release", MediaHost.MixedReleaseId)).ShouldBe(union.Select(Wire));
        using var page = await GetJsonAsync($"/api/titles/{IdCoder.Encode(MediaHost.MixedTitleId)}/releases");
        Choices(page.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem()).ShouldBe(union);
    }

    [Fact]
    public async Task ArchiveFormats_TitleWriteResponse_ListsWhatTheReadLists()
    {
        string path = $"/api/titles/{IdCoder.Encode(MediaHost.Case("gc rvz canonical").TitleId)}";
        using var client = host.CreateAdminClient();
        using var read = await client.GetAsync(path);
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(new { description = "A GameCube disc stored as RVZ" })
        };
        request.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag).ShouldBeTrue();

        using var written = await client.SendAsync(request);

        written.StatusCode.ShouldBe(HttpStatusCode.OK, await written.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await written.Content.ReadAsStringAsync());
        Choices(readBody.RootElement).ShouldBe(["original:Original", "rvz:RVZ"]);
        Choices(body.RootElement).ShouldBe(Choices(readBody.RootElement));
        Choices(body.RootElement.GetProperty("releases").GetProperty("items").EnumerateArray().ShouldHaveSingleItem())
            .ShouldBe(Choices(readBody.RootElement.GetProperty("releases").GetProperty("items").EnumerateArray().ShouldHaveSingleItem()));
    }

    private static string Line(MediaCase media, string surface, string offer) => $"{media.Name} | {surface} | {offer}";

    private static string Describe(IEnumerable<string> choices) => $"[{string.Join(", ", choices)}]";

    private static string Wire(string choice) => choice[..choice.IndexOf(':')];

    private static string[] Choices(JsonElement owner, string property = "archiveFormats") =>
        owner.GetProperty(property).EnumerateArray()
            .Select(choice => $"{choice.GetProperty("format").GetString()}:{choice.GetProperty("label").GetString()}").ToArray();

    private HttpClient Client(bool libraryUser) => libraryUser ? host.CreateLibraryUserClient() : host.CreateAdminClient();

    private async Task<JsonDocument> GetJsonAsync(string path, bool libraryUser = false)
    {
        using var client = Client(libraryUser);
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<string[]> TitleArchiveFormatsAsync(int titleId, bool libraryUser = false)
    {
        using var title = await GetJsonAsync($"/api/titles/{IdCoder.Encode(titleId)}", libraryUser);
        return Choices(title.RootElement);
    }

    private async Task<string[]> ReleaseArchiveFormatsAsync(int titleId, int releaseId, bool libraryUser = false)
    {
        using var title = await GetJsonAsync($"/api/titles/{IdCoder.Encode(titleId)}", libraryUser);
        return Choices(title.RootElement.GetProperty("releases").GetProperty("items").EnumerateArray()
            .Single(release => release.GetProperty("catalogReleaseId").GetString() == IdCoder.Encode(releaseId)));
    }

    private async Task<string[]> LibraryExportFormatsAsync()
    {
        using var library = await GetJsonAsync($"/api/libraries/{IdCoder.Encode(MediaHost.LibraryId)}");
        return Choices(library.RootElement, "exportFormats");
    }

    /// <summary>Original, then every format some member of the target's archive is bound in, across all requested formats.</summary>
    private async Task<string[]> BoundFormatsAsync(string target, int id, bool libraryUser = false)
    {
        var bound = new HashSet<string> { "original" };
        foreach (string format in Formats)
            if (await TicketPlanAsync(target, id, format, libraryUser) is { } plan)
                bound.UnionWith(plan.Entries.Select(entry => RomDeliveryFormats.ToWire(entry.Target.ShouldNotBeNull().Format)));
        return Formats.Where(bound.Contains).ToArray();
    }

    /// <summary>A format the target does not offer is still accepted and binds every member as its original.</summary>
    private async Task AssertFallsBackToOriginalsAsync(string target, int id, IReadOnlyCollection<string> offered)
    {
        foreach (string format in Formats.Except(offered))
            (await TicketPlanAsync(target, id, format)).ShouldNotBeNull($"{target} {id} {format}").Entries
                .ShouldAllBe(entry => entry.Target!.Format == RomDeliveryFormat.Original);
    }

    private async Task<AdminDownloadPlan?> TicketPlanAsync(string target, int id, string format, bool libraryUser = false)
    {
        host.Tickets.Plans.Clear();
        using var client = Client(libraryUser);
        using var response = await client.PostAsJsonAsync("/api/download-tickets",
            new { target, id = IdCoder.Encode(id), format });
        return response.StatusCode == HttpStatusCode.Created ? host.Tickets.Plans.ShouldHaveSingleItem() : null;
    }

    private static async Task<string> PerFormatAsync(Func<string, Task<string>> deliver)
    {
        var offers = new List<string>();
        foreach (string format in Formats)
            offers.Add($"{format}={await deliver(format)}");
        return string.Join(" ", offers);
    }

    private async Task<string> RomFormatsAsync(MediaCase media)
    {
        using var client = host.CreateAdminClient();
        using var response = await client.GetAsync($"/api/roms/{IdCoder.Encode(media.RomId)}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return "[" + string.Join(", ", body.RootElement.GetProperty("formats").EnumerateArray().Select(format =>
            Option(format, format.TryGetProperty("filename", out var name) ? name.GetString() : null))) + "]";
    }

    private async Task<string> DirectDownloadAsync(MediaCase media, string format)
    {
        host.Opened.Clear();
        using var client = host.CreateAdminClient();
        using var response = await client.GetAsync($"/api/roms/{IdCoder.Encode(media.RomId)}/download?format={format}");
        if (response.StatusCode != HttpStatusCode.OK)
            return ((int)response.StatusCode).ToString();
        var opened = host.Opened.ShouldHaveSingleItem();
        return $"{opened.Filename}:{RomDeliveryFormats.ToWire(opened.Format)}";
    }

    private async Task<string> TicketAsync(string target, int id, string format)
    {
        host.Tickets.Plans.Clear();
        using var client = host.CreateAdminClient();
        using var response = await client.PostAsJsonAsync("/api/download-tickets",
            new { target, id = IdCoder.Encode(id), format });
        if (response.StatusCode != HttpStatusCode.Created)
            return ((int)response.StatusCode).ToString();
        var plan = host.Tickets.Plans.ShouldHaveSingleItem();
        return "[" + string.Join(", ", plan.Entries.Select(Entry)) + "]";
    }

    /// <summary>Export entries per format, grouped by title. Archive paths are "&lt;platform&gt;/&lt;title&gt;/&lt;file&gt;".</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>>> ExportEntriesAsync(
        Func<RomDeliveryFormat, ExportJob> create)
    {
        var byFormat = new Dictionary<string, IReadOnlyDictionary<string, string[]>>();
        foreach (string format in Formats)
        {
            var writer = new CapturingArchiveWriter();
            await using var scope = host.Admin.Services.CreateAsyncScope();
            var executor = ActivatorUtilities.CreateInstance<ExportJobExecutor>(scope.ServiceProvider, writer);
            var job = create(RomDeliveryFormats.Parse(format).Value);
            job.Start("characterization", startedAt: DateTimeOffset.UtcNow);
            await executor.ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None,
                ownedMutationDelegate: (mutation, token) => mutation(token)));
            byFormat[format] = writer.Entries.GroupBy(entry => entry.Path.Split('/')[1])
                .ToDictionary(group => group.Key, group => group.Select(Entry).ToArray());
        }
        return byFormat;
    }

    private static string ExportTitles(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> exports) =>
        string.Join(" ", Formats.Select(format =>
            $"{format}=[{string.Join(", ", exports[format].Keys.Order(StringComparer.Ordinal))}]"));

    private static string ExportedTitle(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> exports, string title) =>
        string.Join(" ", Formats.Select(format =>
            $"{format}=" + (exports[format].TryGetValue(title, out var entries) ? $"[{string.Join(", ", entries)}]" : "absent")));

    private async Task<string> ManifestAsync(MediaCase media)
    {
        using var client = host.CreateConsumerClient();
        using var response = await client.PostAsync($"/api/releases/{IdCoder.Encode(media.ReleaseId)}/manifest", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var manifest = body.RootElement;
        var item = manifest.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        var offers = new List<string>();
        foreach (var format in item.GetProperty("formats").EnumerateArray())
        {
            string offer = Option(format, format.GetProperty("relativePath").GetString());
            if (format.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object)
                offer += $" runtime={runtime.GetProperty("contentType").GetString()}:{Launch(runtime)}";
            offers.Add($"{offer} grant={await RedeemAsync(media, format)}");
        }
        return $"launch={Launch(manifest.GetProperty("runtime"))} [{string.Join(", ", offers)}]";
    }

    /// <summary>Streams a granted format whose bytes are stored as-is and checks them against the advertised SHA-256.</summary>
    private async Task<string> RedeemAsync(MediaCase media, JsonElement format)
    {
        if (!format.TryGetProperty("contentGrant", out var grant) || grant.ValueKind != JsonValueKind.Object)
            return "none";
        if (format.GetProperty("format").GetString() == "original" && media.Stored != StoredAs.Original)
            return "decode not read";
        using var client = host.Consumer.CreateClient();
        using var response = await client.GetAsync(grant.GetProperty("downloadUrl").GetString());
        if (response.StatusCode != HttpStatusCode.OK)
            return ((int)response.StatusCode).ToString();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        return Sha256.FromSpan(SHA256.HashData(bytes)).ToString() == format.GetProperty("sha256").GetString()
            ? "bytes" : "mismatch";
    }

    private async Task<string> VfsAsync(MediaCase media, VfsFormat format)
    {
        await using var scope = host.Consumer.Services.CreateAsyncScope();
        var vfs = scope.ServiceProvider.GetRequiredService<IConsumerVfsRepository>();
        var release = (await vfs.GetReleaseAsync(new ConsumerLibraryScope(MediaHost.UserId), media.ReleaseId, default, format))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;
        return "[" + string.Join(", ", (release.Tree.List("") ?? []).Select(entry =>
            $"{entry.RelativePath}:{(entry.File?.Target is { } target ? RomDeliveryFormats.ToWire(target.Format) : "none")}")) + "]";
    }

    private static string Option(JsonElement format, string? filename)
    {
        string[] runtimes = format.GetProperty("runtimeApplicability").EnumerateArray().Select(runtime => runtime.GetString()!).ToArray();
        return $"{format.GetProperty("format").GetString()}:{format.GetProperty("state").GetString()}" +
               (filename is null ? "" : $":{filename}") + (runtimes.Length == 0 ? "" : $"[{string.Join(",", runtimes)}]");
    }

    private static string? Launch(JsonElement runtime) =>
        runtime.TryGetProperty("launch", out var launch) && launch.ValueKind == JsonValueKind.Object
            ? launch.GetProperty("relativePath").GetString() : null;

    private static string Entry(ExportArchiveEntry entry) =>
        $"{entry.Path[(entry.Path.LastIndexOf('/') + 1)..]}:" +
        (entry.Target is { } target ? RomDeliveryFormats.ToWire(target.Format) : "unbound");

    public sealed record MediaCase(string Name, string Platform, string Filename, StoredAs Stored, int RomId, int TitleId,
        int ReleaseId, string TitleName, string? AlsoMatchedOn = null, bool IsFallbackWithHolding = false);

    public enum StoredAs { Original, CanonicalRvz, NonCanonicalRvz, CurrentChd, EarlierChd }

    /// <summary>Admin and consumer hosts over one seeded PostgreSQL library and one content store.</summary>
    public sealed class MediaHost : IAsyncLifetime
    {
        public const int LibraryId = 1;
        public const int MixedTitleId = 9;
        public const int MixedReleaseId = 9;

        /// <summary>The title id of a case's match on its other platform, relative to the case's own title id.</summary>
        public const int OtherPlatformTitleOffset = 100;
        public static readonly Guid UserId = Guid.Parse("5eed0000-0000-4000-8000-000000000001");
        private const string JwtSecret = "media-delivery-characterization-secret-0123456789";
        private const string JwtIssuer = "RomdMediaDeliveryCharacterization";
        private const int LogicalSize = 131072;
        private const int ArtifactSize = 4096;

        private static readonly (int Id, string Key, string Name)[] Platforms =
        [
            (1, "gc", "Nintendo GameCube"), (2, "wii", "Nintendo Wii"), (3, "ps2", "Sony PlayStation 2"),
            (4, "psx", "Sony PlayStation"), (5, "snes", "Super Nintendo Entertainment System")
        ];

        public static readonly IReadOnlyList<MediaCase> Cases =
        [
            new("gc iso", "gc", "GC Game.iso", StoredAs.Original, 1, 1, 1, "GC Game"),
            new("gc rvz canonical", "gc", "GC RVZ.iso", StoredAs.CanonicalRvz, 2, 2, 2, "GC RVZ"),
            new("wii rvz non-canonical", "wii", "Wii RVZ.iso", StoredAs.NonCanonicalRvz, 3, 3, 3, "Wii RVZ"),
            new("ps2 iso", "ps2", "PS2 Game.iso", StoredAs.Original, 4, 4, 4, "PS2 Game"),
            new("ps2 chd current recipe", "ps2", "PS2 CHD.iso", StoredAs.CurrentChd, 5, 5, 5, "PS2 CHD"),
            new("ps2 chd earlier recipe", "ps2", "PS2 Old CHD.iso", StoredAs.EarlierChd, 6, 6, 6, "PS2 Old CHD"),
            new("snes cartridge", "snes", "SNES Game.sfc", StoredAs.Original, 7, 7, 7, "SNES Game"),
            new("ps2 chd matched on two platforms", "ps2", "Shared CHD.iso", StoredAs.CurrentChd, 8, 8, 8, "Shared CHD",
                AlsoMatchedOn: "psx"),
            new("fallback with encoded holding", "gc", "GC Fallback.iso", StoredAs.Original, 10, 10, 10, "GC Fallback",
                IsFallbackWithHolding: true)
        ];

        private static readonly RomChdDecoderBuild Decoder = new("linux-x64", Sha1.Parse(new string('7', 40)), 1);
        private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
        private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-media-delivery-{Guid.NewGuid():N}");
        private WebApplicationFactory<Romd.Admin.Host.Program>? _admin;
        private WebApplicationFactory<Romd.Consumer.Host.Program>? _consumer;

        public CapturingTickets Tickets { get; } = new();
        public List<RomDeliveryTarget> Opened { get; } = [];
        public WebApplicationFactory<Romd.Admin.Host.Program> Admin => _admin.ShouldNotBeNull();
        public WebApplicationFactory<Romd.Consumer.Host.Program> Consumer => _consumer.ShouldNotBeNull();

        public static MediaCase Case(string name) => Cases.Single(media => media.Name == name);

        public async Task InitializeAsync()
        {
            await _database.ProvisionAsync();
            await SeedAsync();
            var native = new ChdNativeIdentity(Decoder.Rid, "libmoonlark_chdr.so", 100, new string('c', 64),
                Decoder.UpstreamCommit.ToString(), "test-only-upstream", Decoder.RecipeVersion, "test-only-family",
                new string('e', 64), true, true, true);
            var candidate = new ChdNativeCandidate("/synthetic/media-delivery", native);
            var admission = new ChdDeliveryAdmission(() => true, () => candidate, () => { }, _ => native);
            var reader = Substitute.For<IRomContentReader>();
            reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
                .Returns(call =>
                {
                    var target = call.Arg<RomDeliveryTarget>();
                    Opened.Add(target);
                    return Task.FromResult<Stream?>(new MemoryStream(new byte[target.SizeBytes]));
                });
            var storage = Substitute.For<IFileStorageService>();
            storage.ExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
            var settings = new Dictionary<string, string>
            {
                [$"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}"] = _database.ConnectionString,
                ["Romd:JwtIssuer"] = JwtIssuer,
                ["Romd:ConsumerHost:JwtAudience"] = JwtIssuer,
                ["Romd:ConsumerDelivery:SigningKeyId"] = "media-delivery",
                ["Romd:ConsumerDelivery:SigningSecret"] = "media-delivery-characterization-signing-secret"
            };

            _admin = RomdHostTestFactory.CreateAdmin(_dataDirectory, JwtSecret, services =>
            {
                UseDatabase(services);
                services.ReplaceWith<IRomChdDeliveryAdmission>(admission);
                services.ReplaceWith<IAdminDownloadTickets>(Tickets);
                services.ReplaceWith(storage);
                services.RemoveAll<IRomContentReader>();
                services.AddScoped(_ => reader);
            }, settings);
            _consumer = RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(_dataDirectory, "Testing", JwtSecret, services =>
            {
                UseDatabase(services);
                // Manifests carry the server instance identity this initializer provisions.
                services.AddHostedService<ServerInstanceIdentityInitializer>();
                services.ReplaceWith<IRomChdDeliveryAdmission>(admission);
            }, settings);
            await StoreContentAsync();
        }

        public HttpClient CreateAdminClient() =>
            Admin.CreateClient().WithTestUser(UserId, "media@example.test", [RomdRoleType.Admin], LibraryId);

        /// <summary>An admin portal user without a catalog role, limited to their assigned library.</summary>
        public HttpClient CreateLibraryUserClient() =>
            Admin.CreateClient().WithTestUser(UserId, "media@example.test", [RomdRoleType.User], LibraryId);

        public HttpClient CreateConsumerClient() =>
            Consumer.CreateClient().WithTestUser(UserId, "media@example.test", [RomdRoleType.User], LibraryId);

        /// <summary>Publishes the shared DVD CHD's other-platform release in an isolated fixture only.</summary>
        public async Task<MediaCase> PublishContradictedTwinAsync()
        {
            var shared = Case("ps2 chd matched on two platforms");
            int id = shared.TitleId + OtherPlatformTitleOffset;
            var twin = shared with
            {
                Name = "dvd chd on contradicted platform",
                Platform = shared.AlsoMatchedOn!,
                TitleId = id,
                ReleaseId = id,
                TitleName = shared.TitleName + " (Other)",
                AlsoMatchedOn = null
            };
            int platformId = PlatformId(twin.Platform);
            await using var db = _database.CreateContext();
            var now = DateTimeOffset.UtcNow;
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = id,
                PlatformId = platformId,
                CatalogTitleId = id,
                Fingerprint = $"release-{id}",
                Name = twin.TitleName,
                CreatedAt = now,
                UpdatedAt = now
            });
            SeedReleaseSource(db, id, id);
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                CatalogReleaseId = id,
                FileFingerprint = "contradicted-platform",
                Name = twin.Filename,
                Size = LogicalSize,
                Sha1 = Sha1.FromSpan(SHA1.HashData(Logical(shared)))
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = LibraryId,
                TitleId = id,
                CatalogReleaseId = id,
                DatGameId = id,
                DatFileId = platformId,
                PlatformId = platformId,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            });
            await db.SaveChangesAsync();
            return twin;
        }

        public async Task DisposeAsync()
        {
            if (_consumer is not null)
                await _consumer.DisposeAsync();
            if (_admin is not null)
                await _admin.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }

        private void UseDatabase(IServiceCollection services)
        {
            services.RemoveAll<DbContextOptions<RomdDbContext>>();
            services.AddDbContext<RomdDbContext>((provider, options) =>
            {
                PostgreSqlConfiguration.Configure(options, _database.ConnectionString);
                options.UseOpenIddict();
                options.AddInterceptors(provider.GetRequiredService<AuditInterceptor>(), new SearchDocumentInterceptor());
            });
            services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        }

        /// <summary>Publishes every preferred artifact; encoded discs stay raw, as the media policies store them.</summary>
        private async Task StoreContentAsync()
        {
            var store = Admin.Services.GetRequiredService<IContentAddressableStore>();
            foreach (var media in Cases)
            {
                await using var content = new MemoryStream(Preferred(media));
                await using var staged = media.Stored == StoredAs.Original
                    ? await store.StageAsync(content) : await store.StageRawAsync(content);
                await staged.PublishAsync();
                if (media.IsFallbackWithHolding)
                {
                    await using var heldContent = new MemoryStream(Holding(media));
                    await using var holding = await store.StageRawAsync(heldContent);
                    await holding.PublishAsync();
                }
            }
        }

        private async Task SeedAsync()
        {
            await using var db = _database.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var now = DateTimeOffset.UtcNow;
            db.Libraries.Add(new LibraryEntity
            {
                Id = LibraryId,
                Name = "Media library",
                ConfigurationState = LibraryConfigurationState.Valid.ToString(),
                NeedsMaterialization = false,
                CreatedAt = now
            });
            db.Users.Add(new RomdUser
            {
                Id = UserId,
                UserName = "media-user",
                NormalizedUserName = "MEDIA-USER",
                Email = "media@example.test",
                NormalizedEmail = "MEDIA@EXAMPLE.TEST",
                SecurityStamp = Guid.NewGuid().ToString(),
                LibraryId = LibraryId,
                CreatedAt = now
            });
            foreach (var (id, key, name) in Platforms)
            {
                db.Platforms.Add(new PlatformEntity
                {
                    Id = id,
                    Name = name,
                    Ownership = ReferenceOwnership.Romd,
                    BuiltInVersion = 1,
                    BaseName = name,
                    BaseCompactLabel = name,
                    CanonicalKey = key,
                    ShortName = key,
                    CreatedAt = now
                });
                byte[] dat = Encoding.UTF8.GetBytes($"{key}.dat");
                db.Files.Add(new FileEntityPersistence
                {
                    Id = 1000 + id,
                    Sha256 = Sha256.FromSpan(SHA256.HashData(dat)),
                    Size = dat.Length,
                    SizeOnDisk = dat.Length,
                    CreatedAt = now
                });
                db.DatFiles.Add(new DatFileEntity
                {
                    Source = new DatSourceEntity { CatalogSource = new CatalogSourceEntity { Id = id, Kind = "Dat", Status = "Active" } },
                    Id = id,
                    Name = $"{name} DAT",
                    Description = $"{name} DAT",
                    Type = "Unknown",
                    PlatformId = id,
                    OriginalFilename = $"{key}.dat",
                    FileId = 1000 + id,
                    GameCount = 1,
                    RomCount = 1,
                    CreatedAt = now
                });
            }
            foreach (var media in Cases)
                await SeedAsync(db, media, now);
            SeedMixedRelease(db, now);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        private static async Task SeedAsync(RomdDbContext db, MediaCase media, DateTimeOffset now)
        {
            int platformId = PlatformId(media.Platform);
            byte[] logical = Logical(media);
            var sha256 = Sha256.FromSpan(SHA256.HashData(logical));
            var sha1 = Sha1.FromSpan(SHA1.HashData(logical));
            var md5 = Md5.FromSpan(MD5.HashData(logical));
            var crc = Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(logical));
            var identity = new RomLogicalIdentity(LogicalSize, sha256, sha1, md5, crc);
            db.RomFiles.Add(new RomFileEntity
            {
                Id = media.RomId,
                OriginalFilename = media.Filename,
                ContentSha256 = sha256,
                ContentSize = LogicalSize,
                Sha1 = sha1,
                Md5 = md5,
                Crc32 = crc
            });
            byte[] artifact = Preferred(media);
            var artifactHash = Sha256.FromSpan(SHA256.HashData(artifact));
            db.Files.Add(new FileEntityPersistence
            {
                Id = media.RomId,
                Sha256 = artifactHash,
                Size = artifact.Length,
                SizeOnDisk = artifact.Length,
                ArtifactMd5 = Md5.FromSpan(MD5.HashData(artifact)),
                ArtifactSha1 = Sha1.FromSpan(SHA1.HashData(artifact)),
                CreatedAt = now
            });
            await db.SaveChangesAsync();
            RomContainerFacts container;
            RomDecoderIdentity decoder;
            Sha1? raw = null, overall = null;
            if (media.Stored == StoredAs.Original)
            {
                container = RomContainerFacts.Identity.Instance;
                decoder = new RomDecoderIdentity.Managed(MediaInspectionService.DecoderBuild);
            }
            else if (media.Stored is StoredAs.CanonicalRvz or StoredAs.NonCanonicalRvz)
            {
                container = MediaFactFixtures.Rvz(LogicalSize, media.Platform == "wii" ? 2u : 1u, 5,
                    media.Stored == StoredAs.CanonicalRvz ? 19 : 5, 131072, media.Platform == "wii");
                decoder = new RomDecoderIdentity.Managed(MediaInspectionService.DecoderBuild);
            }
            else
            {
                bool current = media.Stored == StoredAs.CurrentChd;
                raw = sha1;
                overall = Sha1.FromSpan(SHA1.HashData(artifact));
                container = MediaFactFixtures.Chd(LogicalSize, current ? 16384u : 4096u, 2048,
                    [current ? 0x7a737464u : 0x7a6c6962u, 0, 0, 0], raw.Value, overall.Value,
                    [new(0x44564420, 1, new byte[] { 0 })]);
                decoder = new RomDecoderIdentity.Native(Decoder);
            }
            var measurement = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), artifactHash, artifact.Length, identity,
                container, decoder, "logical-sha256/v1", "synthetic-verifier", now, raw, overall);
            var graph = await db.StageMeasuredWholeAsync(media.RomId, container, measurement,
                new(RomOutputSelector.Whole.Instance, media.RomId, true), CancellationToken.None);
            // Original runtime claims require raw structure. No cached eligibility or filename inference is seeded.
            RomLogicalInspection? inspection = !media.IsFallbackWithHolding && media.Stored == StoredAs.Original ? media.Platform switch
            {
                "gc" => MediaFactFixtures.ObserveNintendo(Guid.NewGuid(), identity, logical.AsSpan(0, 32), new(0, 0, 0),
                    "logical-inspection/v1", "characterization-inspector/v1", now),
                "ps2" => MediaFactFixtures.ObservePs2(Guid.NewGuid(), identity, logical.AsSpan(0, 32),
                    MediaFactFixtures.Ps2Boot(identity.Size, "characterization-inspector/v1"),
                    "logical-inspection/v1", "characterization-inspector/v1", now),
                _ => null
            } : null;
            if (inspection is not null)
                (await MediaTestServices.CreateObservationStore(db, TimeProvider.System).StageAsync(media.RomId,
                    new(graph.Id, graph.Outputs.Single().Id, graph.FileId, measurement.Id), inspection)).IsError.ShouldBeFalse();

            if (media.Stored == StoredAs.Original && media.Platform == "ps2")
            {
                byte[] rawReference = [1, 0, 255];
                var reference = new RomDiscMediaReferenceRecord(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2,
                    RomDiscReferenceMedia.Dvd5, 1, identity.Size, identity.Sha1, identity.Md5, identity.Crc32,
                    rawReference.Length, Sha256.FromSpan(SHA256.HashData(rawReference)), "test-only:characterization",
                    "synthetic-reference/v1", "characterization-fixture/v1", now);
                db.Set<RomDiscMediaReferenceEntity>().Add(RomDiscMediaReferenceEntity.FromDomain(reference, rawReference));
                await db.SaveChangesAsync();
            }

            if (media.IsFallbackWithHolding)
            {
                // Captured RVZ headers retain a holding, but establish neither a playable kind nor delivery proof.
                byte[] held = Holding(media);
                int heldFileId = 1000 + media.RomId;
                db.Files.Add(new FileEntityPersistence
                {
                    Id = heldFileId,
                    Sha256 = Sha256.FromSpan(SHA256.HashData(held)),
                    Size = held.Length,
                    SizeOnDisk = held.Length,
                    ArtifactMd5 = Md5.FromSpan(MD5.HashData(held)),
                    ArtifactSha1 = Sha1.FromSpan(SHA1.HashData(held)),
                    CreatedAt = now
                });
                await db.SaveChangesAsync();
                await db.StageUnmeasuredWholeAsync(heldFileId, identity,
                    MediaFactFixtures.Rvz(LogicalSize, 1, 5, 19, 131072, false), media.RomId, false, CancellationToken.None);
            }

            SeedMatch(db, media, platformId, media.TitleId, media.TitleName, sha1, now);
            if (media.AlsoMatchedOn is { } other)
                SeedMatch(db, media, PlatformId(other), media.TitleId + OtherPlatformTitleOffset, media.TitleName + " (Other)", sha1, now);
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = media.ReleaseId,
                PlatformId = platformId,
                CatalogTitleId = media.TitleId,
                Fingerprint = $"release-{media.ReleaseId}",
                Name = media.TitleName,
                CreatedAt = now,
                UpdatedAt = now
            });
            SeedReleaseSource(db, media.ReleaseId, media.TitleId);
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                CatalogReleaseId = media.ReleaseId,
                FileFingerprint = sha1.ToString(),
                Name = media.Filename,
                Size = LogicalSize,
                Sha1 = sha1
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = LibraryId,
                TitleId = media.TitleId,
                CatalogReleaseId = media.ReleaseId,
                DatGameId = media.TitleId,
                DatFileId = platformId,
                PlatformId = platformId,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            });
        }

        /// <summary>
        ///     A PlayStation 2 release of an RVZ disc, a CHD disc and a cartridge. Its title owns no DAT files, so it joins no
        ///     title ZIP or export and changes no case's delivery.
        /// </summary>
        private static void SeedMixedRelease(RomdDbContext db, DateTimeOffset now)
        {
            const string name = "Mixed Release";
            int platformId = PlatformId("ps2");
            db.Titles.Add(new TitleEntity
            {
                Id = MixedTitleId,
                PlatformId = platformId,
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                EnrichmentStatus = "None",
                CreatedAt = now
            });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = MixedTitleId,
                CatalogSourceId = platformId,
                EntryKey = name,
                Name = name,
                PlatformId = platformId,
                CreatedAt = now
            });
            db.DatGames.Add(new DatGameEntity { Id = MixedTitleId, DatFileId = platformId, SourceEntryId = MixedTitleId, Name = name, CreatedAt = now });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = MixedTitleId, TitleId = MixedTitleId, CreatedAt = now });
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = MixedReleaseId,
                PlatformId = platformId,
                CatalogTitleId = MixedTitleId,
                Fingerprint = $"release-{MixedReleaseId}",
                Name = name,
                CreatedAt = now,
                UpdatedAt = now
            });
            SeedReleaseSource(db, MixedReleaseId, MixedTitleId);
            foreach (var media in new[] { Case("gc rvz canonical"), Case("ps2 chd current recipe"), Case("snes cartridge") })
            {
                var sha1 = Sha1.FromSpan(SHA1.HashData(Logical(media)));
                db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
                {
                    CatalogReleaseId = MixedReleaseId,
                    FileFingerprint = sha1.ToString(),
                    Name = media.Filename,
                    Size = LogicalSize,
                    Sha1 = sha1
                });
            }
        }

        /// <summary>The catalog projection's record that a title's DAT entry, keyed by the same id, asserts the release.</summary>
        private static void SeedReleaseSource(RomdDbContext db, int releaseId, int entryId) =>
            db.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
            {
                CatalogReleaseId = releaseId,
                SourceEntryId = entryId,
                AssertedTitleId = entryId,
                ProviderClaimKey = entryId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        /// <summary>A DAT entry naming the ROM, its source entry and the title it is linked to.</summary>
        private static void SeedMatch(RomdDbContext db, MediaCase media, int platformId, int id, string titleName,
            Sha1 sha1, DateTimeOffset now)
        {
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = platformId,
                Name = titleName,
                NormalizedName = titleName.ToLowerInvariant(),
                EnrichmentStatus = "None",
                CreatedAt = now
            });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = id,
                CatalogSourceId = platformId,
                EntryKey = titleName,
                Name = titleName,
                PlatformId = platformId,
                CreatedAt = now
            });
            db.DatGames.Add(new DatGameEntity { Id = id, DatFileId = platformId, SourceEntryId = id, Name = titleName, CreatedAt = now });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = id, TitleId = id, CreatedAt = now });
            db.DatRoms.Add(new DatRomEntity
            {
                Id = id,
                DatGameId = id,
                Name = media.Filename,
                Size = LogicalSize,
                Sha1 = sha1,
                RomFileId = media.RomId,
                CreatedAt = now
            });
        }

        private static int PlatformId(string key) => Platforms.Single(platform => platform.Key == key).Id;

        private static byte[] Logical(MediaCase media)
        {
            var bytes = Bytes(media.Name, LogicalSize);
            if (media.Platform == "gc")
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x1c), 0xc2339f3d);
            return bytes;
        }

        /// <summary>The bytes of the ROM's preferred file: its logical bytes, or a stand-in encoded artifact.</summary>
        private static byte[] Preferred(MediaCase media) =>
            media.Stored == StoredAs.Original ? Logical(media) : Bytes(media.Name + " artifact", ArtifactSize);

        private static byte[] Holding(MediaCase media) => Bytes(media.Name + " holding", ArtifactSize);

        private static byte[] Bytes(string seed, int length)
        {
            byte[] block = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Enumerable.Range(0, length).Select(index => (byte)(block[index % block.Length] ^ index)).ToArray();
        }
    }

    public sealed class CapturingTickets : IAdminDownloadTickets
    {
        public List<AdminDownloadPlan> Plans { get; } = [];

        public AdminDownloadTicket Issue(AdminDownloadPlan plan)
        {
            Plans.Add(plan);
            return new("/api/download-tickets/characterization", DateTimeOffset.UtcNow.AddMinutes(1));
        }

        public AdminDownloadPlan? Resolve(string token) => null;
    }

    private sealed class CapturingArchiveWriter : IExportArchiveWriter
    {
        public List<ExportArchiveEntry> Entries { get; } = [];

        public async Task WriteAsync(Stream destination, IReadOnlyList<ExportArchiveEntry> entries,
            Func<ExportArchiveEntry, bool, Task>? onEntry = null, bool skipMissing = false, CancellationToken ct = default)
        {
            Entries.AddRange(entries);
            foreach (var entry in entries)
                if (onEntry is not null)
                    await onEntry(entry, true);
        }
    }
}
