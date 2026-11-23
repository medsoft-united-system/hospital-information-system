namespace HospitalInformationSystem.Models.Common;

public sealed class ClinicalAccessDeniedException() : Exception("Назначать лабораторные анализы может только врач.");
