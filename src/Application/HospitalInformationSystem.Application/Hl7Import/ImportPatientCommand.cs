using HospitalInformationSystem.Application.Models.Hl7Import;
using MediatR;

namespace HospitalInformationSystem.Application.Hl7Import;

public sealed record ImportPatientCommand(
    string MessageControlId,
    ImportedPatientInput Patient,
    ImportConnectionContext Context) : IRequest<ImportPatientResult>;
