using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;

namespace Romd.Infrastructure.Storage;

public sealed class RomPreparationService : IRomPreparationService
{
    private readonly RomPreparationAdapterRegistry _adapters;
    private readonly RomPs2DvdEligibilityResolver _ps2Resolver;
    public RomPreparationService(RomPreparationAdapterRegistry adapters, RomPs2DvdEligibilityResolver ps2Resolver)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(ps2Resolver);
        _adapters = adapters;
        _ps2Resolver = ps2Resolver;
    }
    public int SelectPrefixLength(ReadOnlyMemory<byte> prefix, string filename) => RomPreparationDetection.SelectPrefixLength(prefix, filename);
    public bool RequiresDatHashes(ReadOnlyMemory<byte> magic, string filename) =>
        _adapters.Detect(magic.Span, filename).RequiresDatHashes(magic.Span);

    public async Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, string filename, CancellationToken ct = default)
    {
        var admission = await _adapters.Detect(magic.Span, filename).AdmitAsync(magic, ct);
        return admission is null ? null : new Admission(this, admission);
    }

    public async Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default)
    {
        byte[] prefix = new byte[4];
        int length;
        await using (var input = source.OpenRead())
        {
            length = await input.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct);
            for (int round = 0; round < 2; round++)
            {
                int selected = SelectPrefixLength(prefix.AsMemory(0, length), filename);
                if (length != prefix.Length || selected <= prefix.Length)
                    break;
                Array.Resize(ref prefix, selected);
                length += await input.ReadAtLeastAsync(prefix.AsMemory(length), selected - length, throwOnEndOfStream: false, ct);
            }
        }
        return await ResolvePreparedAsync(await _adapters.Detect(prefix.AsSpan(0, length), filename).VerifyAsync(source, filename, ct), ct);
    }

    private async Task<ErrorOr<PreparedRom>> ResolvePreparedAsync(ErrorOr<PreparedRom> inspected, CancellationToken ct)
    {
        if (inspected.IsError)
            return inspected.Errors;
        if (inspected.Value is not PreparedRom.WholeFile prepared)
            return inspected;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (prepared.ChdPs2Boot is { } chdBoot)
            {
                var chdResolved = await _ps2Resolver.ResolveAsync(prepared.Verification.Identity, chdBoot, prepared.Verification.VerifiedAt, ct);
                ct.ThrowIfCancellationRequested();
                if (chdResolved.IsError)
                    return chdResolved.Errors;
                return prepared.WithChdPs2Eligibility(chdResolved.Value.Eligibility);
            }
            if (prepared.Ps2Observation is not { } ps2)
                return prepared;
            var resolved = await _ps2Resolver.ResolveAsync(prepared.Verification.Identity, ps2.Boot, prepared.Verification.VerifiedAt, ct);
            ct.ThrowIfCancellationRequested();
            if (resolved.IsError)
                return resolved.Errors;
            return prepared.WithPs2Eligibility(resolved.Value.Eligibility);
        }
        catch
        {
            await prepared.DisposeAsync();
            throw;
        }
    }

    private sealed class Admission(RomPreparationService owner, IRomPreparationAdmission inner) : IRomPreparationAdmission
    {
        public async Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default) =>
            await owner.ResolvePreparedAsync(await inner.PrepareAsync(source, filename, ct), ct);
        public void Dispose() => inner.Dispose();
    }
}
