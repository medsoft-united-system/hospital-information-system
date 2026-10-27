using System.Globalization;
using System.Net;
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
