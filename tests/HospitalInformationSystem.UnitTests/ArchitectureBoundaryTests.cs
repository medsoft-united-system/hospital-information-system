using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.DI;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Persistence;

namespace HospitalInformationSystem.UnitTests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void ModelsDoNotDependOnApplicationOrAdapters()
    {
        var references = typeof(Patient).Assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();
        references.Should().NotContain(name => name.StartsWith("HospitalInformationSystem.", StringComparison.Ordinal));
        references.Should().NotContain(name => IsAdapterAssembly(name));
    }

    [Theory]
    [InlineData(typeof(IAccountService))]
    [InlineData(typeof(IPatientRepository))]
    public void PortsContainOnlyInterfacesAndDependOnlyOnModels(Type port)
    {
        var assembly = port.Assembly;
        assembly.GetExportedTypes().Should().OnlyContain(type => type.IsInterface);
        assembly.GetReferencedAssemblies().Where(reference =>
                reference.Name!.StartsWith("HospitalInformationSystem.", StringComparison.Ordinal))
            .Select(reference => reference.Name).Should().Equal("HospitalInformationSystem.Application.Models");
        assembly.GetReferencedAssemblies().Select(reference => reference.Name!).Should().NotContain(name => IsAdapterAssembly(name));
    }

    [Fact]
    public void ApplicationDoesNotReferenceConcreteAdapters()
    {
        var references = typeof(DependencyInjection).Assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();
        references.Should().NotContain("HospitalInformationSystem.Infrastructure");
        references.Should().NotContain(name => IsAdapterAssembly(name));
    }

    [Fact]
    public void DatabaseObjectsBelongToSharedModels()
    {
        var models = typeof(Patient).Assembly;
        typeof(PatientDbo).Assembly.Should().BeSameAs(models);
        typeof(ServiceAccountDbo).Assembly.Should().BeSameAs(models);
        typeof(MedicalRecordHistoryEventDbo).Assembly.Should().BeSameAs(models);
        models.GetExportedTypes().Where(type => type.Name.EndsWith("Dbo", StringComparison.Ordinal))
            .Should().NotBeEmpty().And.OnlyContain(type => type.Namespace == "HospitalInformationSystem.Application.Models.Persistence");
    }

    [Theory]
    [InlineData(AdministrativeSex.Male, "Male", 0)]
    [InlineData(AdministrativeSex.Female, "Female", 1)]
    [InlineData(AdministrativeSex.Unknown, "Unknown", 2)]
    public void SharedSexPreservesStringWireFormatAndLegacyNumbers(AdministrativeSex value, string wireValue, int number)
    {
        JsonSerializer.Serialize(value).Should().Be($"\"{wireValue}\"");
        JsonSerializer.Deserialize<AdministrativeSex>($"\"{wireValue}\"").Should().Be(value);
        JsonSerializer.Deserialize<AdministrativeSex>(number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(value);
    }

    [Theory]
    [InlineData("Application", "Models", "")]
    [InlineData("Application", "Contracts", "Models")]
    [InlineData("Application", "Abstractions", "Models")]
    [InlineData("Application", "Application", "Abstractions,Contracts,Models")]
    [InlineData("Presentation", "Web", "Models")]
    public void ProjectReferencesRespectLayerBoundaries(string folder, string name, string allowedReferences)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HospitalInformationSystem.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("architecture checks run from the repository build output");
        var projectName = $"HospitalInformationSystem.{name}";
        var project = XDocument.Load(Path.Combine(root!.FullName, "src", folder, projectName, projectName + ".csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value)
                .Replace("HospitalInformationSystem.", "", StringComparison.Ordinal)).Order().ToArray();
        references.Should().Equal(allowedReferences.Split(',', StringSplitOptions.RemoveEmptyEntries).Order());
        if (folder == "Application")
        {
            project.Descendants("FrameworkReference").Should().BeEmpty();
            project.Descendants("PackageReference").Select(item => item.Attribute("Include")!.Value)
                .Should().NotContain(package => IsAdapterAssembly(package));
        }
    }

    private static bool IsAdapterAssembly(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
        name.StartsWith("Npgsql", StringComparison.Ordinal) ||
        name.StartsWith("Hl7.Fhir", StringComparison.Ordinal) ||
        name.StartsWith("Konscious", StringComparison.Ordinal);
}
