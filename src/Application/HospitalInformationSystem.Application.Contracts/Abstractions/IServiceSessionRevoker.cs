namespace HospitalInformationSystem.Application.Contracts.Abstractions;

public interface IServiceSessionRevoker
{
    Task<int> RevokeAllAsync(Guid serviceAccountId, DateTimeOffset atUtc, string reason,
        CancellationToken cancellationToken);
}
