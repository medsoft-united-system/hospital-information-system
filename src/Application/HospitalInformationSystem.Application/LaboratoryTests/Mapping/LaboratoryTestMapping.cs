using HospitalInformationSystem.Models.LaboratoryTests;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Application.LaboratoryTests.Mapping;

[Mapper]
internal static partial class LaboratoryTestMapping
{
    public static partial LaboratoryTestDefinitionDto ToDto(this LaboratoryTestDefinition definition);
}
