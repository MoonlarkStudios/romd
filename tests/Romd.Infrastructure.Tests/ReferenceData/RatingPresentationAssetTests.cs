using System.Xml;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class RatingPresentationAssetTests
{
    [Fact]
    public void BundledRatingMarks_ReviewedVectors_DoNotCarryEmbeddedOrExternalImages()
    {
        var root = FindRepositoryRoot();
        var paths = Directory.GetFiles(Path.Combine(root, "reference-data/assets/presentation/ratings"), "*.svg", SearchOption.AllDirectories);
        paths.ShouldNotBeEmpty();

        foreach (var path in paths)
        {
            // Older official SVG exports declare a DTD; never resolve it or its external resources.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader);
            var relative = Path.GetRelativePath(root, path);

            document.Root.ShouldNotBeNull().Name.ShouldBe(XName.Get("svg", "http://www.w3.org/2000/svg"), relative);
            // Inspect definitions too: unused editor payloads still ship with the source and server artwork.
            document.Descendants().Where(element => element.Name.LocalName is "image" or "feImage")
                .Select(element => element.Attribute("id")?.Value ?? element.Name.LocalName)
                .ShouldBeEmpty($"{relative} must contain only the reviewed vector mark, including hidden definitions");
            document.Descendants().Attributes().Where(attribute => attribute.Name.LocalName == "href" &&
                !attribute.Value.StartsWith('#'))
                .Select(attribute => attribute.Parent!.Name.LocalName)
                .ShouldBeEmpty($"{relative} must not fetch or embed separate artwork");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
