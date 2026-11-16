using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Application.Hl7Import;

public interface IHl7ImportTokenValidator
{
    // A successful result requires a valid JWT, a live service session and the HL7Import role.
    Task<ServiceAccountIdentity?> ValidateImportAccessAsync(
        string accessToken, CancellationToken cancellationToken = default);
}
