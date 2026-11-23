using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.ServiceAccounts;

public sealed record CreateServiceAccountRequest(string Name, string Role = ServiceAccountRoles.Lis);

public sealed record UpdateServiceAccountRequest(string Name, [property: JsonRequired] uint Version);

public sealed record SetServiceAccountActiveRequest([property: JsonRequired] bool IsActive,
    [property: JsonRequired] uint Version);

public sealed record RotateServiceAccountKeyRequest([property: JsonRequired] uint Version);
