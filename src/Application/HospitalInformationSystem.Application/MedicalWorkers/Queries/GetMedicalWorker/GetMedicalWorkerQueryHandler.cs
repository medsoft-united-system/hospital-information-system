using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalWorkers.Queries.GetMedicalWorker;

public sealed class GetMedicalWorkerQueryHandler(IMedicalWorkerRepository repository)
    : IRequestHandler<GetMedicalWorkerQuery, MedicalWorkerDto>
{
    public async Task<MedicalWorkerDto> Handle(GetMedicalWorkerQuery request, CancellationToken cancellationToken)
    {
        var worker = await repository.FindAsync(request.Id, tracking: false, cancellationToken)
            ?? throw new EntityNotFoundException("Медработник не найден.");
        return Mapping.MedicalWorkerMapping.ToDto(worker);
    }
}
