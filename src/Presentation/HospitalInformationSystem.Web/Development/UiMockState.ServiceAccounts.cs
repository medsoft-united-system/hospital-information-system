using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockState
{
    internal Dictionary<Guid, ServiceAccountDto> ServiceAccounts { get; } = [];

    private void SeedServiceAccounts(DateTimeOffset at)
    {
        ServiceAccounts[LaboratoryServiceId] = new ServiceAccountDto(LaboratoryServiceId, "LIS учебной лаборатории",
            ServiceAccountRoles.Lis, true, at, at, 1, $"msa_{LaboratoryServiceId:N}.");
        var inactiveId = Guid.Parse("90000000-0000-4000-8000-000000000002");
        ServiceAccounts[inactiveId] = new ServiceAccountDto(inactiveId, "Резервная лаборатория",
            ServiceAccountRoles.Lis, false, at, at, 1, $"msa_{inactiveId:N}.");
        var importerId = Guid.Parse("90000000-0000-4000-8000-000000000003");
        ServiceAccounts[importerId] = new ServiceAccountDto(importerId, "Импорт пациентов HL7",
            ServiceAccountRoles.Hl7Import, true, at, at, 1, $"msa_{importerId:N}.");
    }
}
