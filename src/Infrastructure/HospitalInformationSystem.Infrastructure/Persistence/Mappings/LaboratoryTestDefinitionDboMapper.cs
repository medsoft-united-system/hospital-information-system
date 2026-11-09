using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Persistence.Mappings;

[Mapper]
internal static partial class LaboratoryTestDefinitionDboMapper
{
    public static partial LaboratoryTestDefinition ToDomain(this LaboratoryTestDefinitionDbo dbo);
    public static partial LaboratoryTestDefinitionDbo ToDbo(this LaboratoryTestDefinition entity);

    [MapperIgnoreSource(nameof(LaboratoryTestDefinition.Id))]
    [MapperIgnoreSource(nameof(LaboratoryTestDefinition.CreatedAtUtc))]
    [MapperIgnoreTarget(nameof(LaboratoryTestDefinitionDbo.Id))]
    [MapperIgnoreTarget(nameof(LaboratoryTestDefinitionDbo.CreatedAtUtc))]
    public static partial void UpdateDbo(this LaboratoryTestDefinition entity, LaboratoryTestDefinitionDbo dbo);
}
