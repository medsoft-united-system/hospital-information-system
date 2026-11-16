namespace HospitalInformationSystem.Contracts.ServiceAccounts;

public static class ServiceAccountRoles
{
    public const string Lis = "LIS";
    public const string Hl7Import = "HL7Import";

    public static bool IsSupported(string? role) => role is Lis or Hl7Import;
}
