using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Audit.Services;

public sealed class AuditEntryFactory(TimeProvider timeProvider)
{
    public AuditEntry Create(AuditEventType eventType, string message, AuditActor actor,
        AuditMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(eventType) || !Enum.IsDefined(actor.Kind))
            throw new DomainValidationException("Указан неизвестный тип события или инициатора аудита.");
        if ((actor.Kind == AuditActorKind.User) != actor.UserId.HasValue || actor.UserId == Guid.Empty)
            throw new DomainValidationException("Проверенный идентификатор обязателен только для пользователя.");
        if ((actor.Kind == AuditActorKind.ServiceAccount) != actor.ServiceAccountId.HasValue ||
            actor.ServiceAccountId == Guid.Empty)
            throw new DomainValidationException("Проверенный идентификатор сервиса обязателен только для сервисного аккаунта.");
        RequireText(message, 2000, "описание события");
        RequireText(actor.DisplayName, 256, "имя инициатора");
        OptionalText(actor.RequestId, 128, "идентификатор запроса");
        OptionalText(actor.Component, 128, "имя компонента");
        if (actor.Kind == AuditActorKind.Anonymous && string.IsNullOrWhiteSpace(actor.RequestId))
            throw new DomainValidationException("Для анонимного события требуется идентификатор запроса.");
        if (actor.Kind == AuditActorKind.System && string.IsNullOrWhiteSpace(actor.Component))
            throw new DomainValidationException("Для системного события требуется имя компонента.");

        metadata = (metadata ?? new AuditMetadata()) with
        {
            CorrelationId = metadata?.CorrelationId ?? actor.RequestId
        };
        ValidateMetadata(metadata);
        return new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow().ToUniversalTime(),
            eventType, actor, message, metadata);
    }

    private static void ValidateMetadata(AuditMetadata metadata)
    {
        OptionalText(metadata.Action, 64, "действие");
        OptionalText(metadata.CorrelationId, 128, "идентификатор корреляции");
        OptionalText(metadata.RevokeReason, 128, "причина отзыва сессий");
        OptionalText(metadata.AttemptedUserName, 128, "введённый логин");
        OptionalText(metadata.RemoteIp, 64, "IP-адрес");
        OptionalText(metadata.Outcome, 64, "результат операции");
        OptionalText(metadata.SourceSystem, 64, "внешняя система");
        OptionalText(metadata.ExternalPatientId, 128, "внешний идентификатор пациента");
        OptionalText(metadata.ClientCertificateSha256, 64, "отпечаток сертификата");
        OptionalText(metadata.ClientCertificateSubject, 2048, "субъект сертификата");
        OptionalText(metadata.ClientCertificateIssuer, 2048, "издатель сертификата");
        OptionalText(metadata.ClientCertificateSerialNumber, 128, "серийный номер сертификата");
        if (metadata.DiagnosisCount < 0)
            throw new DomainValidationException("Число диагнозов не может быть отрицательным.");
        if (metadata.RevokedRefreshTokenCount < 0 || metadata.RevokedServiceSessionCount < 0)
            throw new DomainValidationException("Число отозванных сессий не может быть отрицательным.");
    }

    private static void RequireText(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new DomainValidationException($"Недопустимое поле аудита: {field}.");
    }

    private static void OptionalText(string? value, int maxLength, string field)
    {
        if (value is not null) RequireText(value, maxLength, field);
    }
}
