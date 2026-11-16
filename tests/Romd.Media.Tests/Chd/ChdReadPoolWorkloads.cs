using Xunit;

namespace Romd.Media.Tests.Chd;

/// <summary>Blocking native callbacks retain their own concurrency without competing with unrelated media collections.</summary>
[CollectionDefinition(nameof(ChdReadPoolWorkloads), DisableParallelization = true)]
public sealed class ChdReadPoolWorkloads
{
}
