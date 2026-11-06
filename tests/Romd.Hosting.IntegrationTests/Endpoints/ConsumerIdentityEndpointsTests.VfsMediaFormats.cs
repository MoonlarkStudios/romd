using Romd.PostgreSql.TestSupport;
using System.IO.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Configuration;
using Romd.Contracts.Consumer.Account;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Storage;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    private static async Task ProbeRegisteredMediaFormatsAsync(HttpClient owner, ConsumerHostFixture fixture,
        VfsProbeContent original, string originalPath, string caFile, CancellationToken ct = default)
    {
        string? artifactPath = Environment.GetEnvironmentVariable("ROMD_VFS_MEDIA_RVZ");
        if (!File.Exists(artifactPath))
            throw new ArgumentException("Media format probe requires a real RVZ in ROMD_VFS_MEDIA_RVZ.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        ct = deadline.Token;
        await using var artifact = await VfsProbeContent.CreateAsync(artifactPath!, ct);
        using var issuedResponse = await owner.PostAsJsonAsync("/api/me/connections",
            new SaveVfsConnectionRequest("Media transition"), ct);
        issuedResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var credential = (await issuedResponse.Content.ReadFromJsonAsync<VfsConnectionCredentialDto>(ct))!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            credential.Connection.Username + ":" + credential.Password)));
        string storedPath = Path.ChangeExtension(originalPath, ".rvz");
        string Url(string path) => "/vfs/library/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        async Task Head(string path, VfsProbeContent expected, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Url(path));
            request.Headers.Authorization = basic;
            using var response = await owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentLength.ShouldBe(expected.Length);
            response.Headers.ETag!.Tag.ShouldBe("\"" + Convert.ToHexString(expected.Sha256).ToLowerInvariant() + "\"");
        }
        async Task Listing(string expectedPath, string missingPath, CancellationToken token)
        {
            string parent = expectedPath[..expectedPath.LastIndexOf('/')];
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), Url(parent));
            request.Headers.Authorization = basic;
            request.Headers.Add("Depth", "1");
            using var response = await owner.SendAsync(request, token);
            response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var hrefs = document.Descendants(XName.Get("href", "DAV:")).Select(element => element.Value).ToArray();
            hrefs.ShouldContain(Url(expectedPath));
            hrefs.ShouldNotContain(Url(missingPath));
        }
        await Head(originalPath, original, ct);
        await Listing(originalPath, storedPath, ct);
        object measured = await fixture.PublishMeasuredProbeRvzAsync(artifact, original, ct);
        await Head(storedPath, artifact, ct);
        await Listing(storedPath, originalPath, ct);
        using (var request = new HttpRequestMessage(HttpMethod.Head, Url(originalPath)))
        {
            request.Headers.Authorization = basic;
            using var old = await owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            old.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        using (var revoke = await owner.DeleteAsync("/api/me/connections/" + credential.Connection.Id, ct))
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ProbeRegisteredConnectionsAsync(owner, fixture, artifact, storedPath, caFile, mount: true,
            originalFormat: original, originalPath: originalPath, mediaTransition: measured, ct: ct);
    }

    private sealed partial class ConsumerHostFixture
    {
        public async Task<object> PublishMeasuredProbeRvzAsync(VfsProbeContent artifact,
            VfsProbeContent original, CancellationToken ct)
        {
            var staging = new LocalTempFileFactory(Factory.Services.GetRequiredService<IRomdOptions>(), new FileSystem());
            artifact.Stream.Position = 0;
            await using var staged = await staging.CreateAsync(artifact.Stream, ct);
            using var verifier = new RvzVerifier(new MediaProcessingOptions());
            var preparation = await new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(), () => verifier,
                () => new RomOriginalMediaIdentifier(verifier),
                () => throw new NotSupportedException("This managed VFS probe has no DVD native assets."),
                () => throw new NotSupportedException("This managed VFS probe has no CD native assets.")),
                new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(
                    Factory.Services.GetRequiredService<Romd.Admin.Application.Source.Rom.IRomDiscMediaReferenceStore>()))
                .PrepareAsync(staged, artifact.Name, ct);
            preparation.IsError.ShouldBeFalse();
            await using var prepared = preparation.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
            prepared.ContentSha256.ShouldBe(Sha256.FromSpan(original.Sha256));
            prepared.Hashes.BytesRead.ShouldBe(original.Length);
            prepared.Hashes.Sha1.ShouldBe(Sha1.FromSpan(original.Sha1));
            prepared.Hashes.Md5.ShouldBe(Md5.FromSpan(original.Md5));
            prepared.Hashes.Crc32.ShouldBe(Crc32.FromSpan(original.Crc32));
            prepared.Artifact.FileSha256.ShouldBe(Sha256.FromSpan(artifact.Sha256));
            prepared.Artifact.FileSize.ShouldBe(artifact.Length);
            var cas = Factory.Services.GetRequiredService<IContentAddressableStore>();
            await using var input = prepared.Artifact.OpenRead();
            var stored = await cas.StoreAsync(input, ct: ct);
            stored.Key.Hash.ShouldBe(prepared.Verification.ArtifactSha256);

            await using var db = CreateSetupDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var rom = await db.RomFiles.SingleAsync(row => row.ContentSha256 == prepared.ContentSha256, ct);
            var repository = Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db);
            await repository.LockRepresentationSetAsync(rom.Id, ct);
            var file = new FileEntityPersistence
            {
                Sha256 = prepared.Artifact.FileSha256,
                Size = prepared.Artifact.FileSize,
                SizeOnDisk = stored.StoredSize,
                IsCompressed = stored.IsCompressed,
                ArtifactMd5 = Md5.FromSpan(artifact.Md5),
                ArtifactSha1 = Sha1.FromSpan(artifact.Sha1),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Files.Add(file);
            await db.SaveChangesAsync(ct);
            var prior = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(rom.Id, ct))!.OutputId;
            var retained = (await repository.RecordWholeMeasurementStagedAsync(rom.Id, file.Id, prepared.Container,
                prepared.Measurement, ct)).ShouldNotBeNull();
            await repository.SetPreferredRepresentationStagedAsync(rom.Id, retained.OutputId, ct);
            await repository.ReleaseRepresentationsStagedAsync(rom.Id, [prior], ct);
            await transaction.CommitAsync(ct);
            (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(rom.Id, ct))!.Artifacts.Count.ShouldBe(1);
            return new
            {
                scope = "Disposable test representation transition; not the future convergence job",
                source = new
                {
                    bytes = original.Length,
                    sha256 = Convert.ToHexString(original.Sha256).ToLowerInvariant(),
                    md5 = Convert.ToHexString(original.Md5).ToLowerInvariant(),
                    sha1 = Convert.ToHexString(original.Sha1).ToLowerInvariant(),
                    crc32 = Convert.ToHexString(original.Crc32).ToLowerInvariant()
                },
                artifact = new
                {
                    bytes = artifact.Length,
                    sha256 = prepared.Artifact.FileSha256.ToString(),
                    md5 = file.ArtifactMd5.ToString(),
                    sha1 = file.ArtifactSha1.ToString()
                },
                prepared.EncodingProfile,
                verificationProfile = prepared.Verification.Profile,
                prepared.Verification.DecoderBuild,
                prepared.Verification.VerifierBuild,
                isoPathRemovedAfterTransition = true,
                measuredProof = true
            };
        }
    }
}
