using HospitalInformationSystem.Application.Contracts.Hl7Import;
using HospitalInformationSystem.Application.Models.Hl7Import;
using MediatR;

namespace HospitalInformationSystem.Application.Hl7Import;

public sealed class ImportPatientCommandHandler(IImportedPatientService imports)
    : IRequestHandler<ImportPatientCommand, ImportPatientResult>
{
    public Task<ImportPatientResult> Handle(ImportPatientCommand request, CancellationToken cancellationToken) =>
        imports.ImportAsync(new ImportPatientRequest(request.MessageControlId, request.Patient, request.Context),
            cancellationToken);
}
