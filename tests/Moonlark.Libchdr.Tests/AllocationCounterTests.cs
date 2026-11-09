using System.Runtime.CompilerServices;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies the counter used by the exact-zero allocation checks.</summary>
public sealed class AllocationCounterTests
{
    /// <summary>Rejects background-GC accounting and confirms that a real heap allocation is counted.</summary>
    [Fact]
    public void AllocationCounterWithBackgroundGcDisabledDetectsRealAllocations()
    {
        Assert.False(Assert.IsType<bool>(GC.GetConfigurationVariables()["ConcurrentGC"]));
        _ = MeasureAllocation();
        Assert.True(MeasureAllocation() > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureAllocation()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        object value = Allocate();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(value);
        return allocated;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Allocate() => new byte[37];
}
