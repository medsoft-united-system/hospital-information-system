using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Contracts.Hl7Import;

public interface IHl7ImportTokenValidator
{
    // A successful result requires a valid JWT, a live service session and the HL7Import role.
    Task<ServiceAccountIdentity?> ValidateImportAccessAsync(
        string accessToken, CancellationToken cancellationToken = default);
}
