using System.Net;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.Web.Gateways;

public sealed class LaboratoryTestsGateway(LaboratoryTestsApiClient client, SessionState session)
    : ILaboratoryTestsGateway
{
    public Task<IReadOnlyList<LaboratoryTestDefinitionDto>> ListAsync(CancellationToken cancellationToken,
        bool activeOnly = true)
    {
        RequireAccess();
        return client.ListAsync(cancellationToken, activeOnly);
    }

    public Task<LaboratoryTestDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireAccess();
        return client.GetAsync(id, cancellationToken);
    }

    public Task<LaboratoryTestDefinitionDto> SaveAsync(Guid? id, SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(administratorOnly: true);
        return id is { } existingId
            ? client.UpdateAsync(existingId, request, cancellationToken)
            : client.CreateAsync(request, cancellationToken);
    }

    public Task<LaboratoryTestDefinitionDto> SetActiveAsync(Guid id, bool isActive, uint version,
        CancellationToken cancellationToken)
    {
        RequireAccess(administratorOnly: true);
        return client.SetActiveAsync(id, isActive, version, cancellationToken);
    }

    private void RequireAccess(bool administratorOnly = false)
    {
        if (session.Current is not { } current)
            throw new ApiException("Для работы со справочником необходимо войти в систему.",
                HttpStatusCode.Unauthorized);
        if (administratorOnly ? current.Role != ApplicationRoles.Administrator
            : current.Role is not (ApplicationRoles.Administrator or ApplicationRoles.MedicalWorker))
            throw new ApiException(administratorOnly
                ? "Изменять справочник лабораторных анализов может только администратор."
                : "У текущей учётной записи нет доступа к справочнику лабораторных анализов.",
                HttpStatusCode.Forbidden);
    }
}
