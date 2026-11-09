using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

using ContractCompanyOverrides = Romd.Contracts.Common.ReferenceCatalog.CompanyOverrides;

namespace Romd.Admin.Application.ReferenceData.Companies.Commands;

public sealed record ResetCompanyOverridesCommand(string Key, CompanyOverrideField? Field, RevisionPrecondition IfMatch) : ICommand<ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>>;
