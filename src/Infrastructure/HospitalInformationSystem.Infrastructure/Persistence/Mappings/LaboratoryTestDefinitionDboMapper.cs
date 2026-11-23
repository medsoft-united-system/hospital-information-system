using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Models.Persistence;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Persistence.Mappings;

[Mapper]
internal static partial class LaboratoryTestDefinitionDboMapper
{
    public static partial LaboratoryTestDefinition ToDomain(this LaboratoryTestDefinitionDbo dbo, uint version);

    [MapperIgnoreSource(nameof(LaboratoryTestDefinition.Version))]
    public static partial LaboratoryTestDefinitionDbo ToDbo(this LaboratoryTestDefinition entity);

    [MapperIgnoreSource(nameof(LaboratoryTestDefinition.Id))]
    [MapperIgnoreSource(nameof(LaboratoryTestDefinition.CreatedAtUtc))]
    [MapperIgnoreSource(nameof(LaboratoryTestDefinition.Version))]
    [MapperIgnoreTarget(nameof(LaboratoryTestDefinitionDbo.Id))]
    [MapperIgnoreTarget(nameof(LaboratoryTestDefinitionDbo.CreatedAtUtc))]
    public static partial void UpdateDbo(this LaboratoryTestDefinition entity, LaboratoryTestDefinitionDbo dbo);
}
