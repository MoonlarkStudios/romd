using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSubject;

public sealed record StartMediaReconciliationSubjectCommand(Guid RequestId, string SubjectKind, int SubjectId, bool ConversionRequested) : ICommand<Guid>;

public sealed class StartMediaReconciliationSubjectCommandHandler(IMediaReconciliationRequests requests, IUnitOfWork work,
    ICurrentUser user, ILogger<StartMediaReconciliationSubjectCommandHandler> logger)
    : ICommandHandler<StartMediaReconciliationSubjectCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(StartMediaReconciliationSubjectCommand command, CancellationToken ct = default)
    {
        if (user.UserId is not { } actor || actor == Guid.Empty)
            return MediaReconciliationErrors.ActorRequired();

        MediaReconciliationSubject? subject = command.SubjectId <= 0 ? null : command.SubjectKind switch
        {
            "LogicalRom" => new MediaReconciliationSubject.LogicalRom(command.SubjectId),
            "OriginalLayout" => new MediaReconciliationSubject.OriginalLayout(command.SubjectId),
            "Representation" => new MediaReconciliationSubject.Representation(command.SubjectId),
            "CatalogCdLayout" => new MediaReconciliationSubject.CatalogCdLayout(command.SubjectId),
            _ => null
        };
        if (command.RequestId == Guid.Empty || subject is null || subject is MediaReconciliationSubject.Representation && command.ConversionRequested)
            return MediaReconciliationErrors.InvalidRequest("Provide an exact request identity and supported subject. Retained representations only request verification.");

        return await work.ExecuteInTransactionAsync<Guid>(async token =>
        {
            var accepted = await requests.StageSubjectAsync(new(command.RequestId, subject, command.ConversionRequested,
                MediaReconciliationCause.Admin, actor, null), token);
            return accepted.IsError ? accepted.Errors : accepted.Value.Job.Id;
        }, logger, ct);
    }
}
