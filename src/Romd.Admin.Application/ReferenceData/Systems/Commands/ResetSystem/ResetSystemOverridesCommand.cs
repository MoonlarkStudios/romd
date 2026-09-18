using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed record ResetSystemOverridesCommand(string Key, SystemOverrideField? Field, string? IfMatch) : ICommand<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>;
