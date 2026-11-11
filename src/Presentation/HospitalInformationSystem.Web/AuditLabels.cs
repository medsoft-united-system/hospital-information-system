using System.Text.Json;
using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Web;

public static class AuditLabels
{
    public static string? RefreshRevocationSummary(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is not null && metadata.TryGetValue("revokedServiceSessionCount", out var serviceValue) &&
            serviceValue.ValueKind == JsonValueKind.Number && serviceValue.TryGetInt32(out var serviceCount) && serviceCount >= 0)
            return $"Отозвано сервисных сессий: {serviceCount}.";
        if (metadata is null || !metadata.TryGetValue("revokedRefreshTokenCount", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
            return null;
        return $"Отозвано refresh-токенов: {count}.";
    }

    public static string EventType(AuditEventType type) => type switch
    {
        AuditEventType.AdministratorLogin => "Вход администратора",
        AuditEventType.UserLogin => "Вход сотрудника",
        AuditEventType.ServiceLogin => "Вход сервисного аккаунта",
        AuditEventType.LoginFailed => "Неудачный вход",
        AuditEventType.MedicalWorkerCreated => "Создан медработник",
        AuditEventType.MedicalWorkerUpdated => "Изменён медработник",
        AuditEventType.MedicalWorkerDeleted => "Удалён медработник",
        AuditEventType.PatientCreated => "Создан пациент",
        AuditEventType.PatientUpdated => "Изменён пациент",
        AuditEventType.PatientDeleted => "Удалён пациент",
        AuditEventType.MedicalRecordCreated => "Создана ЭМК",
        AuditEventType.MedicalRecordChanged => "Изменена ЭМК",
        AuditEventType.AccountCreated => "Создан аккаунт",
        AuditEventType.AccountChanged => "Изменён аккаунт",
        AuditEventType.AdministratorSetup => "Первичная настройка",
        AuditEventType.UserLogout => "Выход",
        AuditEventType.SessionRefreshed => "Обновление сессии",
        _ => type.ToString()
    };

    public static string ActorTitle(AuditActorDto actor) => actor.Kind switch
    {
        AuditActorKind.System => string.IsNullOrWhiteSpace(actor.Component)
            ? "Система"
            : $"Система · {actor.Component}",
        _ => actor.DisplayName
    };

    public static string? ActorDetail(AuditActorDto actor) => actor.Kind switch
    {
        AuditActorKind.User => actor.UserId is { } id ? $"ID: {id}" : null,
        AuditActorKind.ServiceAccount => actor.ServiceAccountId is { } id ? $"ID сервиса: {id}" : null,
        AuditActorKind.Anonymous => string.IsNullOrWhiteSpace(actor.RequestId) ? null : $"Запрос: {actor.RequestId}",
        _ => null
    };

    public static string ActorKind(AuditActorDto actor) => actor.Kind switch
    {
        AuditActorKind.User => "Пользователь",
        AuditActorKind.Anonymous => "Аноним",
        AuditActorKind.System => "Система",
        AuditActorKind.ServiceAccount => "Сервисный аккаунт",
        _ => ""
    };
}
