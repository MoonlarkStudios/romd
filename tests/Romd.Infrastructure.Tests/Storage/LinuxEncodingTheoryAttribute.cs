using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class LinuxEncodingTheoryAttribute : TheoryAttribute
{
    public LinuxEncodingTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Production chdman encoding is supported only on Linux.";
    }
}
