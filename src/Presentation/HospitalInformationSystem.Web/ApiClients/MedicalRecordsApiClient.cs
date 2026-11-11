using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using HospitalInformationSystem.Contracts.MedicalRecords;

namespace HospitalInformationSystem.Web.ApiClients;

public sealed class MedicalRecordsApiClient(HttpClient http)
{
    public async Task<MedicalRecordDto?> FindForPatientAsync(Guid patientId,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/patients/{patientId}/medical-record", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ApiResponseReader.ReadAsync<MedicalRecordDto>(response, cancellationToken);
    }

    public Task<MedicalRecordDto> CreateAsync(Guid patientId,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync<MedicalRecordDto>(HttpMethod.Post, $"api/patients/{patientId}/medical-record",
            null, null, cancellationToken);

    public Task AddDiagnosisAsync(Guid recordId, Guid diagnosisId,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync(HttpMethod.Post, $"api/medical-records/{recordId}/diagnoses",
            new AddMedicalRecordDiagnosisRequest(diagnosisId), null, cancellationToken);

    public Task RemoveDiagnosisAsync(Guid recordId, Guid medicalRecordDiagnosisId,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync(HttpMethod.Delete, $"api/medical-records/{recordId}/diagnoses/{medicalRecordDiagnosisId}",
            null, null, cancellationToken);

    public Task AddPrescriptionAsync(Guid recordId, string text,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync(HttpMethod.Post, $"api/medical-records/{recordId}/prescriptions",
            new SavePrescriptionRequest(text), null, cancellationToken);

    public Task CancelPrescriptionAsync(Guid recordId, Guid prescriptionId,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync(HttpMethod.Post, $"api/medical-records/{recordId}/prescriptions/{prescriptionId}/cancel",
            null, null, cancellationToken);

    public Task ReplacePrescriptionAsync(Guid recordId, Guid prescriptionId, string text,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync(HttpMethod.Put, $"api/medical-records/{recordId}/prescriptions/{prescriptionId}",
            new SavePrescriptionRequest(text), null, cancellationToken);

    public async Task<IReadOnlyList<LaboratoryAnalysisRequestDto>> CreateLaboratoryAnalysisRequestsAsync(
        Guid recordId, CreateLaboratoryAnalysisRequestsRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/medical-records/{recordId}/laboratory-analysis-requests", request, cancellationToken);
        var created = await ApiResponseReader.ReadListAsync<LaboratoryAnalysisRequestDto>(response, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created || created.Count == 0 ||
            created.Count != request.LaboratoryTestDefinitionIds.Count ||
            created.Select(x => x.Id).Distinct().Count() != created.Count ||
            created.Select(x => x.PatientId).Distinct().Count() != 1 ||
            created.Select(x => x.RequestedByMedicalWorkerId).Distinct().Count() != 1 ||
            !created.Select(x => x.LaboratoryTestDefinitionId).Order()
                .SequenceEqual(request.LaboratoryTestDefinitionIds.Order()) ||
            created.Any(x => x.Id == Guid.Empty || x.MedicalRecordId != recordId || x.PatientId == Guid.Empty ||
                x.RequestedByMedicalWorkerId == Guid.Empty || x.Status != LaboratoryAnalysisRequestStatus.Active ||
                x.Result is not null || x.AuthoredAtUtc == default))
            throw new ApiException(
                "Сервер вернул некорректный ответ. Обновите карту, чтобы проверить результат назначения.");
        return created;
    }

    public async Task<IReadOnlyList<MedicalRecordHistoryEventDto>> GetHistoryAsync(Guid recordId,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/medical-records/{recordId}/history", cancellationToken);
        return await ApiResponseReader.ReadListAsync<MedicalRecordHistoryEventDto>(response, cancellationToken);
    }

    public async Task<MedicalRecordSnapshotDto> GetSnapshotAsync(Guid recordId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(at.ToString("o", CultureInfo.InvariantCulture));
        using var response = await http.GetAsync($"api/medical-records/{recordId}/snapshot?at={query}",
            cancellationToken);
        return await ApiResponseReader.ReadAsync<MedicalRecordSnapshotDto>(response, cancellationToken);
    }

}
