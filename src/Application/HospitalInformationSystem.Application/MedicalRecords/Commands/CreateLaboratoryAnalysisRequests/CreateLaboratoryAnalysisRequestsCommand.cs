using HospitalInformationSystem.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.CreateLaboratoryAnalysisRequests;

public sealed record CreateLaboratoryAnalysisRequestsCommand(Guid MedicalRecordId,
    IReadOnlyList<Guid> LaboratoryTestDefinitionIds, string? Comment,
    IReadOnlyDictionary<Guid, uint>? ExpectedVersions = null) : IRequest<IReadOnlyList<LaboratoryAnalysisRequestDto>>;
