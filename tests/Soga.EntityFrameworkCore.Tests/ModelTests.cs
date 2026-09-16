using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Soga.EntityFrameworkCore.Extensions;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

public sealed class ModelTests
{
    [Fact]
    public void AddSoga_RegistersInitialPersistenceModel()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());

        modelBuilder.AddSoga();

        Assert.Equal(4, modelBuilder.Model.GetEntityTypes().Count());
    }
}
