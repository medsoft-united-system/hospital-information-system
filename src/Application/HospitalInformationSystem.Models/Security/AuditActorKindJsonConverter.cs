using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.Security;

public sealed class AuditActorKindJsonConverter() : JsonStringEnumConverter<AuditActorKind>(
    JsonNamingPolicy.CamelCase, allowIntegerValues: false);
