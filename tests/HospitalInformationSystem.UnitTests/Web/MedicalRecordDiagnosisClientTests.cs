using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Web.ApiClients;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class MedicalRecordDiagnosisClientTests
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 17, 12, 34, 56, TimeSpan.Zero);
    private static readonly DateTimeOffset DiagnosedAt = new(2023, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Theory]
    [InlineData("record", false)]
    [InlineData("record", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("snapshot", false)]
    [InlineData("snapshot", true)]
    public async Task Read_HumanOrServiceDiagnosis_ShouldPreserveAuthorsAndClinicalDate(string operation, bool serviceAuthor)
    {
        var diagnosis = Diagnosis(serviceAuthor);
        using var http = Client(operation, diagnosis);

        var actual = await ReadDiagnosisAsync(new MedicalRecordsApiClient(http), operation);

        actual.Should().Be(diagnosis);
    }

    [Theory]
    [InlineData("record", "no-author")]
    [InlineData("record", "two-authors")]
    [InlineData("record", "empty-author")]
    [InlineData("record", "missing-offset")]
    [InlineData("record", "missing-date")]
    [InlineData("record", "invalid-offset")]
    [InlineData("snapshot", "no-author")]
    [InlineData("snapshot", "two-authors")]
    [InlineData("snapshot", "missing-offset")]
    [InlineData("create", "two-authors")]
    public async Task Read_InconsistentDiagnosis_ShouldRejectResponse(string operation, string problem)
    {
        var valid = Diagnosis(serviceAuthor: true);
        var diagnosis = problem switch
        {
            "no-author" => valid with { AddedByServiceAccountId = null },
            "two-authors" => valid with { AddedByMedicalWorkerId = Guid.NewGuid() },
            "empty-author" => valid with { AddedByServiceAccountId = Guid.Empty },
            "missing-offset" => valid with { DiagnosedAtOffsetMinutes = null },
            "missing-date" => valid with { DiagnosedAtUtc = null },
            "invalid-offset" => valid with { DiagnosedAtOffsetMinutes = 841 },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };
        using var http = Client(operation, diagnosis);

        var action = () => ReadDiagnosisAsync(new MedicalRecordsApiClient(http), operation);

        await action.Should().ThrowAsync<ApiException>().WithMessage("*некорректный ответ*");
    }

    private static MedicalRecordDiagnosisDto Diagnosis(bool serviceAuthor) => new(
        Guid.NewGuid(), "I10", new string('Н', 256), ImportedAt,
        serviceAuthor ? null : Guid.NewGuid(), serviceAuthor ? Guid.NewGuid() : null,
        serviceAuthor ? DiagnosedAt : null, serviceAuthor ? 180 : null,
        serviceAuthor ? "I10" : null, serviceAuthor ? "U^Unspecified^99MEDSOFT" : null);

    private static HttpClient Client(string operation, MedicalRecordDiagnosisDto diagnosis) => new(
        new ResponseHandler(operation, diagnosis))
    { BaseAddress = new Uri("https://his.invalid/") };

    private static async Task<MedicalRecordDiagnosisDto> ReadDiagnosisAsync(MedicalRecordsApiClient client,
        string operation)
    {
        if (operation == "snapshot")
            return (await client.GetSnapshotAsync(Guid.NewGuid(), ImportedAt, CancellationToken.None)).Diagnoses.Single();
        var record = operation == "create"
            ? await client.CreateAsync(Guid.NewGuid(), CancellationToken.None)
            : await client.FindForPatientAsync(Guid.NewGuid(), CancellationToken.None);
        return record!.Diagnoses.Single();
    }

    private sealed class ResponseHandler(string operation, MedicalRecordDiagnosisDto diagnosis) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = operation == "snapshot"
                    ? JsonContent.Create(new MedicalRecordSnapshotDto(Guid.NewGuid(), Guid.NewGuid(), ImportedAt,
                        2, [diagnosis], []))
                    : JsonContent.Create(new MedicalRecordDto(Guid.NewGuid(), Guid.NewGuid(), ImportedAt,
                        2, [diagnosis], []))
            };
            return Task.FromResult(response);
        }
    }
}
