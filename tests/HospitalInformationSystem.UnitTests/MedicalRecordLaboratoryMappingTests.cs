using FluentAssertions;
using HospitalInformationSystem.Application.MedicalRecords.Mapping;
using HospitalInformationSystem.Domain.LaboratoryTests.Factories;
using HospitalInformationSystem.Domain.MedicalRecords.Factories;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.MedicalWorkers.Factories;

namespace HospitalInformationSystem.UnitTests;

public sealed class MedicalRecordLaboratoryMappingTests
{
    [Fact]
    public void OrdersBySameRenamedDoctor_ShouldUseEachRequestEventNameIncludingAfterServiceResult()
    {
        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var doctor = MedicalWorkerFactory.Create("Лев", "Иванов", "Врач", at, MedicalWorkerRole.Doctor);
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), doctor, at);
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", at);
        var first = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], null,
            doctor, at.AddMinutes(1)).Single();
        doctor.LastName = "Петров";
        var second = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], null,
            doctor, at.AddMinutes(2)).Single();
        MedicalRecordService.RecordLaboratoryAnalysisResult(record, first.Id, record.PatientId,
            first.LoincCodeSnapshot, "5.6", at.AddMinutes(2), Guid.NewGuid(), "go-lis", at.AddMinutes(3));
        doctor.LastName = "Сидоров";
        doctor.IsDeleted = true;

        var dto = record.ToDto();

        dto.LaboratoryAnalysisRequests.Single(x => x.Id == first.Id).RequestedByNameSnapshot.Should().Be("Иванов Лев");
        dto.LaboratoryAnalysisRequests.Single(x => x.Id == second.Id).RequestedByNameSnapshot.Should().Be("Петров Лев");
        dto.LaboratoryAnalysisRequests.Single(x => x.Id == first.Id).Result!.Value.Should().Be("5.6");
    }
}
