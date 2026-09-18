using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed record CreateSystemCommand(string Key, SystemDefinition Definition, IReadOnlyList<string>? ManufacturerKeys = null) : ICommand<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>;
