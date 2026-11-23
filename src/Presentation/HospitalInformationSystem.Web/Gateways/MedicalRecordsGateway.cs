using System.Net;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.Web.Gateways;

public sealed class MedicalRecordsGateway(MedicalRecordsApiClient client, SessionState session) : IMedicalRecordsGateway
{
    public Task<MedicalRecordDto?> FindForPatientAsync(Guid patientId, CancellationToken cancellationToken) =>
        client.FindForPatientAsync(patientId, cancellationToken);

    public Task<MedicalRecordDto> CreateAsync(Guid patientId, CancellationToken cancellationToken)
    {
        RequirePermission(creating: true);
        return client.CreateAsync(patientId, cancellationToken);
    }

    public Task AddDiagnosisAsync(Guid recordId, Guid diagnosisId, CancellationToken cancellationToken)
    {
        RequirePermission();
        return client.AddDiagnosisAsync(recordId, diagnosisId, cancellationToken);
    }

    public Task RemoveDiagnosisAsync(Guid recordId, Guid medicalRecordDiagnosisId,
        CancellationToken cancellationToken)
    {
        RequirePermission();
        return client.RemoveDiagnosisAsync(recordId, medicalRecordDiagnosisId, cancellationToken);
    }

    public Task AddPrescriptionAsync(Guid recordId, string text, CancellationToken cancellationToken)
    {
        RequirePermission();
        return client.AddPrescriptionAsync(recordId, text, cancellationToken);
    }

    public Task CancelPrescriptionAsync(Guid recordId, Guid prescriptionId,
        CancellationToken cancellationToken)
    {
        RequirePermission();
        return client.CancelPrescriptionAsync(recordId, prescriptionId, cancellationToken);
    }

    public Task ReplacePrescriptionAsync(Guid recordId, Guid prescriptionId, string text,
        CancellationToken cancellationToken)
    {
        RequirePermission();
        return client.ReplacePrescriptionAsync(recordId, prescriptionId, text, cancellationToken);
    }

    public Task<IReadOnlyList<LaboratoryAnalysisRequestDto>> CreateLaboratoryAnalysisRequestsAsync(Guid recordId,
        CreateLaboratoryAnalysisRequestsRequest request, CancellationToken cancellationToken)
    {
        RequirePermission();
        return client.CreateLaboratoryAnalysisRequestsAsync(recordId, request, cancellationToken);
    }

    public Task<IReadOnlyList<MedicalRecordHistoryEventDto>> GetHistoryAsync(Guid recordId,
        CancellationToken cancellationToken) => client.GetHistoryAsync(recordId, cancellationToken);

    public Task<MedicalRecordSnapshotDto> GetSnapshotAsync(Guid recordId, DateTimeOffset at,
        CancellationToken cancellationToken) => client.GetSnapshotAsync(recordId, at, cancellationToken);

    private void RequirePermission(bool creating = false)
    {
        if (session.Current is not { } current)
            throw new ApiException("Для изменения карты необходимо войти в систему.", HttpStatusCode.Unauthorized);
        if (current.Role != ApplicationRoles.MedicalWorker || current.MedicalWorkerId is not { } id || id == Guid.Empty)
            throw new ApiException("Изменять карту может только медработник с действующей учётной записью.",
                HttpStatusCode.Forbidden);
        var authenticatedSession = new AuthenticationSessionDto(current.Id, current.UserName, current.Role,
            current.MedicalWorkerId, current.MedicalWorkerRole);
        if (!(creating ? SessionPermissions.CanCreateMedicalRecord(authenticatedSession)
                : SessionPermissions.CanEditMedicalRecord(authenticatedSession)))
            throw new ApiException(creating ? "У текущей медицинской роли нет права создавать ЭМК."
                : "Изменять содержимое ЭМК может только врач.", HttpStatusCode.Forbidden);
    }
}
