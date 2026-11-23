namespace HospitalInformationSystem.Application.Models.Common;

public sealed class ClinicalAccessDeniedException() : Exception("Назначать лабораторные анализы может только врач.");
