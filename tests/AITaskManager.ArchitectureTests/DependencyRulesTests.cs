using AITaskManager.Application;
using AITaskManager.Core;
using AITaskManager.Hosting;

namespace AITaskManager.ArchitectureTests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void CoreHasNoProjectDependencies()
    {
        var projectReferences = ProjectReferences(typeof(GpuId).Assembly);

        Assert.Empty(projectReferences);
    }

    [Fact]
    public void ApplicationDependsOnlyOnCore()
    {
        var projectReferences = ProjectReferences(typeof(IGpuTelemetryProvider).Assembly);

        Assert.Equal(["AITaskManager.Core"], projectReferences);
    }

    [Fact]
    public void HostingUsesOnlyApprovedFoundationProjects()
    {
        var projectReferences = ProjectReferences(typeof(ApplicationHost).Assembly);

        Assert.Equal(
            [
                "AITaskManager.Adapters.LlamaCpp",
                "AITaskManager.Adapters.Nvidia",
                "AITaskManager.Application",
                "AITaskManager.Contracts",
                "AITaskManager.Core",
                "AITaskManager.Infrastructure.Windows",
                "AITaskManager.Persistence",
            ],
            projectReferences);
    }

    [Fact]
    public void HeadlessServiceIsAThinHostingEntryPoint()
    {
        var serviceAssembly = System.Reflection.Assembly.Load("AITaskManager.Service");

        Assert.Equal(["AITaskManager.Hosting"], ProjectReferences(serviceAssembly));
    }

    private static string[] ProjectReferences(System.Reflection.Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("AITaskManager.", StringComparison.Ordinal))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
}
