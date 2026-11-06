using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Moonlark.Libchdr stays a standalone wrapper: it never depends on ROMD's own assemblies or projects.</summary>
public sealed class ArchitectureTests
{
    private static readonly string[] ReferenceItems = ["ProjectReference", "PackageReference", "Reference"];

    /// <summary>The compiled library references no Romd.* assembly.</summary>
    [Fact]
    public void CompiledLibraryReferencesNoRomdAssembly()
    {
        AssemblyName[] references = typeof(ChdFile).Assembly.GetReferencedAssemblies();
        Assert.NotEmpty(references);
        Assert.DoesNotContain(references, reference => IsRomd(reference.Name ?? ""));
    }

    /// <summary>Neither the project nor any MSBuild file it imports declares a Romd.* project or package reference.</summary>
    [Fact]
    public void LibraryProjectDeclaresNoRomdReference()
    {
        string project = Path.Combine(TestRepository.Root, "src", "Moonlark.Libchdr", "Moonlark.Libchdr.csproj");
        string[] files = [.. MsBuildFiles(project)];
        Assert.Contains(Path.GetFullPath(project), files);
        Assert.Contains(Path.GetFullPath(Path.Combine(TestRepository.Root, "build", "Moonlark.Native.props")), files);
        string[] romd = [.. files.SelectMany(file => XDocument.Load(file).Descendants()
            .Where(element => ReferenceItems.Contains(element.Name.LocalName, StringComparer.Ordinal))
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "")
            .Where(include => IsRomd(Path.GetFileNameWithoutExtension(include.Replace('\\', '/'))))
            .Select(include => file + ": " + include))];
        Assert.Empty(romd);
    }

    /// <summary>The project file, its directory's Directory.Build.props and every file they import, transitively.</summary>
    private static IEnumerable<string> MsBuildFiles(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.GetFullPath(project), Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "Directory.Build.props")]);
        while (pending.TryPop(out string? file))
        {
            if (!File.Exists(file) || !seen.Add(file))
                continue;
            yield return file;
            foreach (XElement import in XDocument.Load(file).Descendants().Where(element => element.Name.LocalName == "Import"))
            {
                string target = (string?)import.Attribute("Project") ?? "";
                Assert.DoesNotContain("$(", target, StringComparison.Ordinal);
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, target)));
            }
        }
    }

    private static bool IsRomd(string name) =>
        name.Equals("Romd", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Romd.", StringComparison.OrdinalIgnoreCase);
}
