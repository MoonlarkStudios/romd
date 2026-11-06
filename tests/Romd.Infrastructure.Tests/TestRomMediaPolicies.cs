using Romd.Admin.Application.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Tests;

/// <summary>Infrastructure fixtures exercise the production media policy composition.</summary>
internal static class TestRomMediaPolicies
{
    internal const string Recipe = RvzCanonicalRecipe.Id;
    internal static RomMediaPolicyRegistry Create() => RomMediaPolicyComposition.CreateRegistry();
}
