namespace HospitalInformationSystem.Application.Common;

public sealed class ClinicalAccessDeniedException() : Exception("Назначать лабораторные анализы может только врач.");
