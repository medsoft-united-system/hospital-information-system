using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using MediatR;

namespace HospitalInformationSystem.Application.Diagnoses.Commands.DeleteDiagnosis;

public sealed class DeleteDiagnosisCommandHandler(
    IDiagnosisRepository repository,
    IUnitOfWork unitOfWork
) : IRequestHandler<DeleteDiagnosisCommand>
{
    public async Task Handle(DeleteDiagnosisCommand request, CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(request.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
