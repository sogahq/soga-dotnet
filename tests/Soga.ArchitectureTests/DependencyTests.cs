using Xunit;

namespace Soga.ArchitectureTests;

public sealed class DependencyTests
{
    [Fact]
    public void CorePackage_DoesNotReferenceEntityFrameworkCore()
    {
        var references = typeof(Soga.Extensions.SogaServiceCollectionExtensions)
            .Assembly
            .GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) is true);
    }
}
