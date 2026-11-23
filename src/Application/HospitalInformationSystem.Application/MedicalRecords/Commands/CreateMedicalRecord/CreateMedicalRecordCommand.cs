using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.CreateMedicalRecord;

public sealed record CreateMedicalRecordCommand(Guid PatientId, Guid MedicalWorkerId) : IRequest<MedicalRecordDto>;
