using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog.Commands.DecideDiscSet;

public sealed record DecideDiscSetCommand(int Id, DecideDiscSetRequest Request, RevisionPrecondition Precondition, Guid Actor) : ICommand<DiscSetDto>;

public sealed class DecideDiscSetCommandHandler(IDiscSetDecisionStore store, IUnitOfWork unitOfWork,
    TimeProvider clock, ILogger<DecideDiscSetCommandHandler> logger) : ICommandHandler<DecideDiscSetCommand, DiscSetDto>
{
    public Task<ErrorOr<DiscSetDto>> HandleAsync(DecideDiscSetCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var input = new DiscSetDecisionInput(request.Decision, request.Name, request.ExpectedDiscCount,
            request.Members?.Select(member => member is null ? null : new DiscSetMemberChange(member.EntryKey, member.Ordinal)), request.Reason);
        return unitOfWork.ExecuteInTransactionAsync<DiscSetDto>(async token =>
        {
            var invalid = DiscSetDecisionPolicy.ValidateRequest(input);
            if (invalid != DiscSetDecisionProblem.None)
                return FromProblem(invalid);
            await store.LockTopologyAsync(token);
            var target = await store.ReadAsync(command.Id, token);
            if (target is null)
                return Error.NotFound("DiscSet.Missing", "Disc set not found.");
            if (!command.Precondition.IsSatisfiedBy(target.Revision))
                return Error.Custom(412, "DiscSet.PreconditionFailed", "This set changed. Reload before saving.");

            var row = store.ReadFacts(target);
            var keys = DiscSetDecisionPolicy.RequiredEntryKeys(input, row.Facts);
            var claims = await store.ReadClaimsAsync(row.PlatformId, row.Facts.SourceId, keys, token);
            var result = DiscSetDecisionPolicy.Decide(input, row.Facts, claims);
            if (result.Problem != DiscSetDecisionProblem.None)
                return FromProblem(result.Problem);
            var change = result.Change ?? throw new InvalidOperationException("A successful disc-set decision has no change.");
            var decidedAt = clock.GetUtcNow();
            var revision = Guid.NewGuid();
            await store.StageDecisionAsync(row, change, input.Reason, command.Actor, decidedAt, revision, token);
            await store.InvalidateAffectedLibrariesAsync(row.Id, Guid.NewGuid(), clock.GetUtcNow(), token);
            var systemKey = await store.ReadSystemKeyAsync(row.PlatformId, token);
            return DiscSetContractMapping.ToDto(row with
            {
                Revision = revision,
                Facts = new(row.Facts.SourceId, change.Decision, row.Facts.Proposal, change.Correction),
                DecidedBy = command.Actor,
                DecidedAt = decidedAt,
                Reason = input.Reason,
                SystemKey = systemKey
            }, claims);
        }, logger, ct);
    }

    private static Error FromProblem(DiscSetDecisionProblem problem) => problem switch
    {
        DiscSetDecisionProblem.Decision => Error.Validation("DiscSet.Decision", "Choose accept, reject, correct or reset and a reason of at most 1000 characters."),
        DiscSetDecisionProblem.Review => Error.Validation("DiscSet.Review", "Correct the review issues before accepting."),
        DiscSetDecisionProblem.Members => Error.Validation("DiscSet.Members", "Supply a name and 1–99 unique members with unique ordinals; total is optional, 1–99."),
        DiscSetDecisionProblem.Source => Error.Validation("DiscSet.Source", "Every member must resolve unambiguously in this source and system."),
        DiscSetDecisionProblem.Scope => Error.Validation("DiscSet.Scope", "Members must share a title, represent different releases, and fit the declared total."),
        DiscSetDecisionProblem.Changed => Error.Conflict("DiscSet.Changed", "Source membership changed; refresh and correct the proposal."),
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Unknown disc-set decision problem.")
    };
}
