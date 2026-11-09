using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

using SystemOverrides = Romd.Domain.ReferenceData.SystemOverrides;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed record UpdateSystemCommand(string Key, SystemOverrides Changes, RevisionPrecondition IfMatch, IReadOnlyList<string>? ManufacturerKeys = null) : ICommand<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>;
