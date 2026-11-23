using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.MedicalWorkers.Factories;
using HospitalInformationSystem.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalWorkers.Commands.CreateMedicalWorker;

public sealed class CreateMedicalWorkerCommandHandler(
    IMedicalWorkerRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<CreateMedicalWorkerCommand, MedicalWorkerDto>
{
    public async Task<MedicalWorkerDto> Handle(CreateMedicalWorkerCommand request, CancellationToken cancellationToken)
    {
        var worker = MedicalWorkerFactory.Create(request.FirstName, request.LastName, request.Position,
            timeProvider.GetUtcNow(), request.Role is { } role
                ? Mapping.MedicalWorkerMapping.ToDomain(role)
                : MedicalWorkerRole.Other);
        repository.Add(worker);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Mapping.MedicalWorkerMapping.ToDto(worker);
    }
}
