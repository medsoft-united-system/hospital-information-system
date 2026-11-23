using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Fhir;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Fhir;

public static class FhirReadValidation
{
    public static void RequireId(Guid id)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Укажите непустой идентификатор FHIR-ресурса.");
    }

    public static void RequirePage(int count, FhirPagePosition? after)
    {
        if (count is < 1 or > 100)
            throw new DomainValidationException("Размер FHIR-страницы должен быть от 1 до 100.");
        if (after is not null && (after.Id == Guid.Empty || after.AtUtc.Offset != TimeSpan.Zero ||
                                 after.AtUtc.UtcTicks % TimeSpan.TicksPerMillisecond != 0))
            throw new DomainValidationException("Некорректная позиция FHIR-страницы.");
    }

    public static void RequireStatus(LaboratoryAnalysisRequestStatus? status)
    {
        if (status is not null && !Enum.IsDefined(status.Value))
            throw new DomainValidationException("Неподдерживаемый статус назначения анализа.");
    }
}
