using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed record ResetSystemOverridesCommand(string Key, SystemOverrideField? Field, RevisionPrecondition IfMatch) : ICommand<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>;
