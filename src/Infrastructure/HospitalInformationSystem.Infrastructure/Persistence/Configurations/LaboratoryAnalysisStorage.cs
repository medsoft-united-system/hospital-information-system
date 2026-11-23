using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

internal static class LaboratoryAnalysisStorage
{
    public static string WriteStatus(LaboratoryAnalysisRequestStatus value) => value switch
    {
        LaboratoryAnalysisRequestStatus.Active => "active",
        LaboratoryAnalysisRequestStatus.Completed => "completed",
        _ => throw new InvalidOperationException("Неизвестный статус лабораторного назначения.")
    };

    public static LaboratoryAnalysisRequestStatus ReadStatus(string value) => value switch
    {
        "active" => LaboratoryAnalysisRequestStatus.Active,
        "completed" => LaboratoryAnalysisRequestStatus.Completed,
        _ => throw new InvalidOperationException("Неизвестный статус лабораторного назначения.")
    };

    public static string WriteAuthorKind(MedicalRecordAuthorKind value) => value switch
    {
        MedicalRecordAuthorKind.MedicalWorker => "MedicalWorker",
        MedicalRecordAuthorKind.ServiceAccount => "ServiceAccount",
        _ => throw new InvalidOperationException("Неизвестный вид автора медицинского события.")
    };

    public static MedicalRecordAuthorKind ReadAuthorKind(string value) => value switch
    {
        "MedicalWorker" => MedicalRecordAuthorKind.MedicalWorker,
        "ServiceAccount" => MedicalRecordAuthorKind.ServiceAccount,
        _ => throw new InvalidOperationException("Неизвестный вид автора медицинского события.")
    };
}
