using System.Xml.Linq;
using Xunit;

namespace NatsSyncLab.ArchitectureTests;

public sealed class ProjectReferenceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Module_projects_reference_only_allowed_layers()
    {
        foreach (var project in EnumerateProjects("Module"))
        {
            var layer = project.Name.Split('.').Last();
            var service = project.Name.Split('.')[1];

            foreach (var reference in project.References)
            {
                var referenceName = Path.GetFileName(reference);

                if (IsOtherModulePublicApi(project, reference))
                {
                    continue;
                }

                var allowed = layer switch
                {
                    "Domain" => referenceName == "NatsSyncLab.SharedKernel.csproj",
                    "Application" => referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.Domain.csproj",
                    "PublicApi" => referenceName == "NatsSyncLab.SharedKernel.csproj"
                        || referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.Domain.csproj"
                        || referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.Application.csproj",
                    "IntegrationEvent" => referenceName == "NatsSyncLab.SharedKernel.csproj"
                        || referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.Domain.csproj",
                    "Infrastructure" => referenceName == "NatsSyncLab.SharedKernel.csproj"
                        || referenceName == $"NatsSyncLab.{service}.SharedKernel.csproj"
                        || referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.Domain.csproj"
                        || referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.Application.csproj"
                        || referenceName == $"NatsSyncLab.{service}.Module.{project.Module}.IntegrationEvent.csproj",
                    _ => false,
                };

                Assert.True(
                    allowed,
                    $"{project.Name} has disallowed reference {referenceName}");
            }
        }
    }

    [Fact]
    public void Public_api_projects_do_not_reference_infrastructure()
    {
        foreach (var project in EnumerateProjects("Module").Where(project => project.Name.EndsWith(".PublicApi")))
        {
            Assert.DoesNotContain(
                project.References,
                reference => reference.EndsWith(".Infrastructure.csproj", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Modules_reference_other_modules_only_through_public_api()
    {
        foreach (var project in EnumerateProjects("Module"))
        {
            foreach (var reference in project.References)
            {
                if (!reference.Contains("Module", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var referenceModule = GetModuleName(reference);
                if (string.Equals(referenceModule, project.Module, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Assert.EndsWith(".PublicApi.csproj", reference, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static bool IsOtherModulePublicApi(ProjectProject project, string reference)
    {
        var referenceModule = GetModuleName(reference);
        return !string.IsNullOrEmpty(referenceModule)
            && !string.Equals(referenceModule, project.Module, StringComparison.OrdinalIgnoreCase)
            && reference.EndsWith(".PublicApi.csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetModuleName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var moduleIndex = Array.FindIndex(segments, segment => segment.Equals("Module", StringComparison.OrdinalIgnoreCase));
        if (moduleIndex >= 0 && moduleIndex + 1 < segments.Length)
        {
            return segments[moduleIndex + 1];
        }

        var nameSegments = Path.GetFileNameWithoutExtension(normalized).Split('.');
        var nameModuleIndex = Array.FindIndex(
            nameSegments,
            segment => segment.Equals("Module", StringComparison.OrdinalIgnoreCase));

        return nameModuleIndex >= 0 && nameModuleIndex + 1 < nameSegments.Length
            ? nameSegments[nameModuleIndex + 1]
            : string.Empty;
    }

    private static IEnumerable<ProjectProject> EnumerateProjects(string folderName)
    {
        var projectFiles = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => path.Replace('\\', '/').Contains($"/{folderName}/", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase));

        foreach (var path in projectFiles)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var module = GetModuleName(path);
            var references = XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Replace('\\', '/'))
                .ToArray();

            yield return new ProjectProject(name, module, references);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NatsSyncLab.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate NatsSyncLab.slnx.");
    }

    private sealed record ProjectProject(string Name, string Module, string[] References);
}
