using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Companies.Commands;

public sealed record UpdateCompanyCommand(string Key, CompanyOverrides Changes, string? IfMatch) : ICommand<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>;
