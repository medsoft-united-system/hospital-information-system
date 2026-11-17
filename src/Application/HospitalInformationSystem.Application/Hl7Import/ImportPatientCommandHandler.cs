using MediatR;

namespace HospitalInformationSystem.Application.Hl7Import;

public sealed class ImportPatientCommandHandler(IImportedPatientService imports)
    : IRequestHandler<ImportPatientCommand, ImportPatientResult>
{
    public Task<ImportPatientResult> Handle(ImportPatientCommand request, CancellationToken cancellationToken) =>
        imports.ImportAsync(request, cancellationToken);
}
