using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.Security;

[JsonConverter(typeof(AuditActorKindJsonConverter))]
public enum AuditActorKind
{
    User,
    Anonymous,
    System,
    ServiceAccount
}
