using HospitalInformationSystem.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalWorkers.Commands.CreateMedicalWorker;

public sealed record CreateMedicalWorkerCommand(string FirstName, string LastName, string Position,
    Models.Security.MedicalWorkerRole? Role = null)
    : IRequest<MedicalWorkerDto>;
