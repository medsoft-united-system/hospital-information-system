using HospitalInformationSystem.Contracts.ServiceAccounts;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockState
{
    internal Dictionary<Guid, ServiceAccountDto> ServiceAccounts { get; } = [];

    private void SeedServiceAccounts(DateTimeOffset at)
    {
        ServiceAccounts[LaboratoryServiceId] = new ServiceAccountDto(LaboratoryServiceId, "LIS учебной лаборатории",
            "LIS", true, at, at, 1, $"msa_{LaboratoryServiceId:N}.");
        var inactiveId = Guid.Parse("90000000-0000-4000-8000-000000000002");
        ServiceAccounts[inactiveId] = new ServiceAccountDto(inactiveId, "Резервная лаборатория",
            "LIS", false, at, at, 1, $"msa_{inactiveId:N}.");
    }
}
