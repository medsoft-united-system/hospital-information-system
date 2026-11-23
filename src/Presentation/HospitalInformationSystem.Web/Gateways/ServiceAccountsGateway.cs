using System.Net;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.Web.Gateways;

public sealed class ServiceAccountsGateway(ServiceAccountsApiClient client, SessionState session) : IServiceAccountsGateway
{
    public Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken)
    { RequireAdministrator(); return client.ListAsync(cancellationToken); }
    public Task<ServiceAccountDto> GetAsync(Guid id, CancellationToken cancellationToken)
    { RequireAdministrator(); return client.GetAsync(id, cancellationToken); }
    public Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest request, CancellationToken cancellationToken)
    { RequireAdministrator(); return client.CreateAsync(request, cancellationToken); }
    public Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest request, CancellationToken cancellationToken)
    { RequireAdministrator(); return client.UpdateAsync(id, request, cancellationToken); }
    public Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id, SetServiceAccountActiveRequest request, CancellationToken cancellationToken)
    { RequireAdministrator(); return client.SetActiveAsync(id, request, cancellationToken); }
    public Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest request, CancellationToken cancellationToken)
    { RequireAdministrator(); return client.RotateKeyAsync(id, request, cancellationToken); }

    private void RequireAdministrator()
    {
        if (session.Current is null) throw new ApiException("Войдите в систему для управления сервисными аккаунтами.", HttpStatusCode.Unauthorized);
        if (session.Current.Role != ApplicationRoles.Administrator)
            throw new ApiException("Сервисными аккаунтами управляет администратор.", HttpStatusCode.Forbidden);
    }
}
