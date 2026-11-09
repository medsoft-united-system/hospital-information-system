using HospitalInformationSystem.Contracts.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.RecordLaboratoryAnalysisResult;

public sealed record RecordLaboratoryAnalysisResultCommand(Guid RequestId, Guid PatientId, string LoincCode,
    string Value, DateTimeOffset IssuedAtUtc) : IRequest<LaboratoryAnalysisResultDto>;
