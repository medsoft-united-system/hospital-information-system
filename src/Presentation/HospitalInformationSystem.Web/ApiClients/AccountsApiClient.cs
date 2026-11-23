using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.Gateways;

namespace HospitalInformationSystem.Web.ApiClients;

public sealed class AccountsApiClient(HttpClient http)
{
    private readonly AccountManagementGateway _gateway = new(http);

    public Task<IReadOnlyList<AccountDetailsDto>> ListAsync(CancellationToken cancellationToken) =>
        _gateway.ListAsync(cancellationToken);

    public Task<CreateWorkerAccountResultDto> CreateForWorkerAsync(Guid medicalWorkerId,
        CreateWorkerAccountRequest request, CancellationToken cancellationToken) =>
        _gateway.CreateForWorkerAsync(medicalWorkerId, request, cancellationToken);

    public Task<ResetAccountPasswordResultDto> ResetPasswordAsync(Guid accountId, CancellationToken cancellationToken) =>
        _gateway.ResetPasswordAsync(accountId, cancellationToken);

    public Task<SetAccountActiveResultDto> SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken) =>
        _gateway.SetActiveAsync(accountId, isActive, cancellationToken);
}
