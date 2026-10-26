using System.Reflection;
using Soga.Errors;
using Xunit;

namespace Soga.ArchitectureTests;

public sealed class DependencyTests
{
    private static readonly Assembly Core = typeof(Soga.Extensions.SogaServiceCollectionExtensions).Assembly;
    private static readonly Assembly EfCore = typeof(Soga.EntityFrameworkCore.Extensions.SogaModelBuilderExtensions).Assembly;

    [Fact]
    public void CorePackage_DoesNotReferenceEntityFrameworkCore()
    {
        var references = Core.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void EfCorePackage_ExposesOnlyExtensionClasses()
    {
        var publicTypes = EfCore.GetExportedTypes();

        Assert.All(publicTypes, type => Assert.True(type.IsAbstract && type.IsSealed, $"{type.FullName} must be a static class."));
    }

    [Fact]
    public void PersistencePortAndRows_AreInternal()
    {
        var leaked = Core.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("Soga.Persistence", StringComparison.Ordinal) is true)
            .Select(type => type.FullName);

        Assert.Empty(leaked);
    }

    [Fact]
    public void WorkflowsAndHub_AreInternal()
    {
        var leaked = Core.GetExportedTypes()
            .Where(type => type.Name.EndsWith("Workflow", StringComparison.Ordinal) || type.Name.EndsWith("Hub", StringComparison.Ordinal))
            .Select(type => type.FullName);

        Assert.Empty(leaked);
    }

    [Fact]
    public void PublicContracts_DoNotExposePersistenceOrEfTypes()
    {
        var contractTypes = Core.GetExportedTypes().Where(type => type.Namespace == "Soga.Contracts");

        foreach (var type in contractTypes)
        {
            foreach (var property in type.GetProperties())
            {
                var propertyNamespace = property.PropertyType.Namespace ?? string.Empty;
                Assert.False(
                    propertyNamespace.StartsWith("Soga.Persistence", StringComparison.Ordinal) ||
                    propertyNamespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
                    $"{type.Name}.{property.Name} leaks {property.PropertyType}.");
            }
        }
    }

    [Fact]
    public void PublicContracts_AreImmutableRecords()
    {
        var contractTypes = Core.GetExportedTypes()
            .Where(type => type.Namespace == "Soga.Contracts" && !type.IsAbstract);

        foreach (var type in contractTypes)
        {
            Assert.All(
                type.GetProperties().Where(p => p.DeclaringType == type && p.Name != "EqualityContract"),
                property => Assert.True(
                    property.SetMethod is null || property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Length > 0,
                    $"{type.Name}.{property.Name} must be init-only."));
        }
    }

    [Fact]
    public void ExtensionPoints_ArePublicInterfacesThatDoNotExposePersistenceTypes()
    {
        var extensionPoints = new[]
        {
            typeof(Soga.Authorization.ISogaAuthorizationPolicy),
            typeof(Soga.Extensibility.ISogaMessageValidator),
            typeof(Soga.Extensibility.ISogaAuditSink),
            typeof(Soga.Extensibility.ISogaEventSubscriber),
            typeof(Soga.Extensibility.ISogaSystemMessages),
            typeof(Soga.Extensibility.ISogaPresenceProvider),
        };

        foreach (var type in extensionPoints)
        {
            Assert.True(type.IsInterface && type.IsPublic, $"{type.Name} must be a public interface.");
            var signatureTypes = type.GetMethods()
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .SelectMany(t => t.IsGenericType ? t.GetGenericArguments().Append(t) : [t]);
            Assert.DoesNotContain(
                signatureTypes,
                t => (t.Namespace ?? string.Empty).StartsWith("Soga.Persistence", StringComparison.Ordinal) ||
                    (t.Namespace ?? string.Empty).StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void GroupRulesAndAuthorizer_AreInternal()
    {
        var leaked = Core.GetExportedTypes()
            .Where(type => type.Namespace == "Soga.Groups" || type.Name.EndsWith("Rules", StringComparison.Ordinal) || type.Name.EndsWith("Authorizer", StringComparison.Ordinal))
            .Select(type => type.FullName);

        Assert.Empty(leaked);
    }

    [Fact]
    public void ErrorCodes_AreUniqueStableAndNamespaced()
    {
        var codes = typeof(SogaErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.StartsWith("soga.", code, StringComparison.Ordinal));
        Assert.Contains("soga.message.idempotency_conflict", codes);
        Assert.Contains("soga.cursor.invalid", codes);
    }
}
