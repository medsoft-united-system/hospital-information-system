using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.Security;

public sealed record AuditActorDto(
    AuditActorKind Kind,
    Guid? UserId,
    string DisplayName,
    string? RequestId,
    string? Component,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ServiceAccountId = null);
