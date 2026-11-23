using HospitalInformationSystem.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalWorkers.Queries.GetMedicalWorker;

public sealed record GetMedicalWorkerQuery(Guid Id) : IRequest<MedicalWorkerDto>;
