using HospitalInformationSystem.Application.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalWorkers.Commands.UpdateMedicalWorker;

public sealed record UpdateMedicalWorkerCommand(Guid Id, string FirstName, string LastName, string Position,
    Models.Security.MedicalWorkerRole? Role = null)
    : IRequest<MedicalWorkerDto>;
