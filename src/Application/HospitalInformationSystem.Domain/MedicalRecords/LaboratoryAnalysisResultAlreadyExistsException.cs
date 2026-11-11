using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.MedicalRecords;

public sealed class LaboratoryAnalysisResultAlreadyExistsException()
    : BusinessRuleConflictException("Результат этого анализа уже зарегистрирован.");
