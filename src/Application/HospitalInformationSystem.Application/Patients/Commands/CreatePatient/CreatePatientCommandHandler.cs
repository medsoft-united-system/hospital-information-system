using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.Patients.Factories;
using HospitalInformationSystem.Models.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Patients.Commands.CreatePatient;

public sealed class CreatePatientCommandHandler(
    IPatientRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<CreatePatientCommand, PatientDto>
{
    public async Task<PatientDto> Handle(CreatePatientCommand request, CancellationToken cancellationToken)
    {
        var patient = PatientFactory.Create(request.FirstName, request.LastName, request.MiddleName,
            request.BirthDate, request.Sex, request.Comment, timeProvider.GetUtcNow());
        repository.Add(patient);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Mapping.PatientMapping.ToDto(patient);
    }
}
