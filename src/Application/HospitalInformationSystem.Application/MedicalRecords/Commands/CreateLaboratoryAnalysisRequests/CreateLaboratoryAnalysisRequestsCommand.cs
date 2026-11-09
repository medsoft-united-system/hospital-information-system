using HospitalInformationSystem.Contracts.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.CreateLaboratoryAnalysisRequests;

public sealed record CreateLaboratoryAnalysisRequestsCommand(Guid MedicalRecordId,
    IReadOnlyList<Guid> LaboratoryTestDefinitionIds, string? Comment) : IRequest<IReadOnlyList<LaboratoryAnalysisRequestDto>>;
