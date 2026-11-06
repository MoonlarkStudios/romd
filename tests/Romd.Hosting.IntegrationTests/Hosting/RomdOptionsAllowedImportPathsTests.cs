using System;
using System.IO;
using Romd.Host.Configuration;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class RomdOptionsAllowedImportPathsTests
{
    [Fact]
    public void ValidateAllowedImportPaths_RelativeEntry_ThrowsNamingTheSetting()
    {
        var options = new RomdOptions
        {
            AllowedImportPaths = [Path.Combine("relative", "roms")]
        };

        var exception = Should.Throw<InvalidOperationException>(options.ValidateAllowedImportPaths);
        exception.Message.ShouldContain("Romd:AllowedImportPaths");
        exception.Message.ShouldContain("relative");
    }

    [Fact]
    public void ValidateAllowedImportPaths_AbsoluteEntries_DoesNotThrow()
    {
        var options = new RomdOptions
        {
            AllowedImportPaths = [Path.GetTempPath()]
        };

        Should.NotThrow(options.ValidateAllowedImportPaths);
    }

    [Fact]
    public void ValidateAllowedImportPaths_EmptyDefault_DoesNotThrow()
    {
        Should.NotThrow(new RomdOptions().ValidateAllowedImportPaths);
    }
}
