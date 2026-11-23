using HospitalInformationSystem.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Commands.CancelPrescription;

public sealed record CancelPrescriptionCommand(Guid MedicalRecordId, Guid PrescriptionId, Guid MedicalWorkerId)
    : IRequest;
