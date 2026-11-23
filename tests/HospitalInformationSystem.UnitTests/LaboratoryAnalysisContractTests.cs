using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryAnalysisContractTests
{
    [Fact]
    public void CreateRequest_ShouldRoundTripExpectedVersionsByDefinitionId()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var request = new CreateLaboratoryAnalysisRequestsRequest([first, second], "Комментарий",
            new Dictionary<Guid, uint> { [first] = 17, [second] = 29 });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(request, options);
        var restored = JsonSerializer.Deserialize<CreateLaboratoryAnalysisRequestsRequest>(json, options);

        restored.Should().BeEquivalentTo(request);
        json.Should().Contain("\"expectedVersions\"");
        new CreateLaboratoryAnalysisRequestsRequest([first], null).ExpectedVersions.Should().BeNull();
    }

    [Fact]
    public void OldMedicalRecordAndSnapshotJson_ShouldProvideEmptyLaboratoryCollections()
    {
        const string json = """
            {"id":"11111111-1111-4111-8111-111111111111","medicalRecordId":"11111111-1111-4111-8111-111111111111",
             "patientId":"22222222-2222-4222-8222-222222222222","createdAtUtc":"2026-10-01T10:00:00Z",
             "at":"2026-10-01T10:00:00Z","version":1,"diagnoses":[],"prescriptions":[]}
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        JsonSerializer.Deserialize<MedicalRecordDto>(json, options)!.LaboratoryAnalysisRequests.Should().BeEmpty();
        JsonSerializer.Deserialize<MedicalRecordSnapshotDto>(json, options)!.LaboratoryAnalysisRequests.Should().BeEmpty();
    }

    [Fact]
    public void OldHumanHistoryJson_ShouldKeepHumanAuthorAndNoServiceAccount()
    {
        const string json = """
            {"version":1,"occurredAtUtc":"2026-10-01T10:00:00Z",
             "performedByMedicalWorkerId":"33333333-3333-4333-8333-333333333333",
             "performedByName":"Иванов Алексей","performedByPosition":"Врач",
             "eventType":"MedicalRecordCreated","entityId":null,"payloadJson":"{}"}
            """;

        var item = JsonSerializer.Deserialize<MedicalRecordHistoryEventDto>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        item.AuthorKind.Should().Be(MedicalRecordAuthorKind.MedicalWorker);
        item.PerformedByMedicalWorkerId.Should().Be(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        item.PerformedByServiceAccountId.Should().BeNull();
    }
}
