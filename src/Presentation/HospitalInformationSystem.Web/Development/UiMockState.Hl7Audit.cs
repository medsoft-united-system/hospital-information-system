using System.Text.Json;
using HospitalInformationSystem.Contracts.Hl7Import;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockState
{
    private void SeedHl7Audit()
    {
        var now = DateTimeOffset.UtcNow;
        var serviceId = Guid.Parse("90000000-0000-4000-8000-000000000003");
        var serviceName = ServiceAccounts[serviceId].Name;
        var attempts = new[]
        {
            (Outcome: Hl7ImportAuditOutcomes.Success, Minutes: 3, Count: 2),
            (Outcome: Hl7ImportAuditOutcomes.Duplicate, Minutes: 2, Count: 0),
            (Outcome: Hl7ImportAuditOutcomes.ValidationFailed, Minutes: 1, Count: 0)
        };
        foreach (var attempt in attempts)
        {
            var messageId = "ui-mock-hl7-" + attempt.Outcome;
            var metadata = new Dictionary<string, JsonElement>
            {
                ["action"] = JsonSerializer.SerializeToElement("hl7-patient-import"),
                ["outcome"] = JsonSerializer.SerializeToElement(attempt.Outcome),
                ["correlationId"] = JsonSerializer.SerializeToElement(messageId),
                ["sourceSystem"] = JsonSerializer.SerializeToElement(Hl7ImportProtocol.SourceSystem),
                ["externalPatientId"] = JsonSerializer.SerializeToElement("000042"),
                ["diagnosisCount"] = JsonSerializer.SerializeToElement(attempt.Count),
                ["clientCertificateSha256"] = JsonSerializer.SerializeToElement(new string('A', 64)),
                ["clientCertificateSubject"] = JsonSerializer.SerializeToElement("CN=hl7-import-demo, OU=MedSoft"),
                ["clientCertificateIssuer"] = JsonSerializer.SerializeToElement("CN=MedSoft Demo CA"),
                ["clientCertificateSerialNumber"] = JsonSerializer.SerializeToElement("DE00000001"),
                ["clientCertificateNotBeforeUtc"] = JsonSerializer.SerializeToElement(now.AddDays(-1)),
                ["clientCertificateNotAfterUtc"] = JsonSerializer.SerializeToElement(now.AddYears(1))
            };
            if (attempt.Outcome != Hl7ImportAuditOutcomes.ValidationFailed)
                metadata["targetId"] = JsonSerializer.SerializeToElement(PatientWithRecordId);
            Audit.Add(new MockAuditEvent(Guid.NewGuid(), now.AddMinutes(-attempt.Minutes), "Hl7PatientImported",
                null, serviceName, attempt.Outcome == Hl7ImportAuditOutcomes.Success
                    ? "Пациент импортирован через HL7." : "Импорт пациента через HL7 отклонён.",
                null, ServiceAccountId: serviceId, Metadata: metadata, RequestId: messageId));
        }
    }
}
