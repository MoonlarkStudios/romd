using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Companies.Commands;

public sealed record ResetCompanyOverridesCommand(string Key, CompanyOverrideField? Field, string? IfMatch) : ICommand<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>;
