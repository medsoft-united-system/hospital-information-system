using System.Text.Json;
using System.Text.Json.Serialization;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Audit;

[Mapper]
internal static partial class AuditEntryMapping
{
    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [MapProperty("Actor.Kind", nameof(AuditEventDbo.ActorKind))]
    [MapProperty("Actor.UserId", nameof(AuditEventDbo.ActorUserId))]
    [MapProperty("Actor.DisplayName", nameof(AuditEventDbo.ActorDisplayName))]
    [MapProperty("Actor.RequestId", nameof(AuditEventDbo.RequestId))]
    [MapProperty("Actor.Component", nameof(AuditEventDbo.Component))]
    [MapProperty(nameof(AuditEntry.Metadata), nameof(AuditEventDbo.MetadataJson))]
    public static partial AuditEventDbo ToDbo(AuditEntry entry);

    public static AuditEventDto ToDto(AuditEventDbo entry) => new(entry.EventId, entry.EventTimeUtc,
        ParseEventType(entry.EventType),
        new AuditActorDto(ParseActorKind(entry.ActorKind), entry.ActorUserId, entry.ActorDisplayName,
            entry.RequestId, entry.Component), entry.Message,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entry.MetadataJson, MetadataOptions)
        ?? new Dictionary<string, JsonElement>());

    private static string ToEventType(AuditEventType eventType) => eventType.ToString();

    private static string ToActorKind(AuditActorKind kind) => kind switch
    {
        AuditActorKind.User => "user",
        AuditActorKind.Anonymous => "anonymous",
        AuditActorKind.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string SerializeMetadata(AuditMetadata metadata) => JsonSerializer.Serialize(metadata, MetadataOptions);

    private static AuditEventType ParseEventType(string eventType) => Enum.Parse<AuditEventType>(eventType);

    private static AuditActorKind ParseActorKind(string kind) => kind switch
    {
        "user" => AuditActorKind.User,
        "anonymous" => AuditActorKind.Anonymous,
        "system" => AuditActorKind.System,
        _ => throw new InvalidOperationException("В журнале сохранён неизвестный тип инициатора.")
    };
}
