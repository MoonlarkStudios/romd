using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Isolates callback allocation measurements from concurrent test collections.</summary>
[CollectionDefinition(nameof(ChdSourceContextIsolation), DisableParallelization = true)]
public sealed class ChdSourceContextIsolation
{
}
