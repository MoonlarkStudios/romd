using Romd.Dat.Parsing.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public class DatProvenanceDetectorTests
{
    [Theory]
    [InlineData("Nintendo - Nintendo 64", "NESBrew12", "https://No-Intro.org", DatProvenance.NoIntro)]
    [InlineData("Sony - PlayStation 2 - BIOS Images", "Jackal, AKuHAK | redump.org", null, DatProvenance.Redump)]
    [InlineData("Sega Mega Drive TOSEC", "TOSEC", null, DatProvenance.Tosec)]
    [InlineData("MAME 0.245", null, null, DatProvenance.Mame)]
    [InlineData("Some Custom Set", "anonymous", null, DatProvenance.Unknown)]
    public void Detect_ClassifiesByHeaderProvenance(string name, string? author, string? url, DatProvenance expected)
    {
        DatProvenanceDetector.Detect(name, author, url).ShouldBe(expected);
    }
}
