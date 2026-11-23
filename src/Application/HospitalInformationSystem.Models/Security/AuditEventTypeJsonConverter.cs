using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.Security;

public sealed class AuditEventTypeJsonConverter() : JsonStringEnumConverter<AuditEventType>(
    namingPolicy: null, allowIntegerValues: false);
