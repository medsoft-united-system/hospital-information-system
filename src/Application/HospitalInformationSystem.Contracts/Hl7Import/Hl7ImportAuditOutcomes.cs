namespace HospitalInformationSystem.Contracts.Hl7Import;

public static class Hl7ImportAuditOutcomes
{
    public const string Success = "success";
    public const string Duplicate = "duplicate";
    public const string ValidationFailed = "validation-failed";
    public const string AuthenticationRejected = "auth-rejected";
    public const string ProtocolRejected = "protocol-rejected";
    public const string InternalError = "internal-error";
}
