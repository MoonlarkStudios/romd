using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Real codec workloads keep their finite deadlines without unrelated test-class CPU and I/O contention.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeMediaCodecWorkloads
{
    public const string Name = "Native media codec workloads";
}
