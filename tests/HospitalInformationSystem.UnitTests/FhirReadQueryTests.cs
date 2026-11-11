using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.Patients;
using Moq;

namespace HospitalInformationSystem.UnitTests;

public sealed class FhirReadQueryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResourceQueries_ShouldForwardIdAndCancellationAndPreserveStoredData()
    {
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var patient = new Patient
        {
            Id = id, FirstName = "Иван", LastName = "Петров", BirthDate = new DateOnly(1990, 1, 1),
            Sex = AdministrativeSex.Male
        };
        var practitioner = new MedicalWorker
        {
            Id = id, FirstName = "Алексей", LastName = "Иванов", Position = "Врач", IsDeleted = true
        };
        var request = new FhirServiceRequestReadModel(id, Guid.NewGuid(), practitioner.Id, "2345-7", "Глюкоза",
            "Сохранённый комментарий", At, LaboratoryAnalysisRequestStatus.Completed);
        var observation = new FhirObservationReadModel(Guid.NewGuid(), id, request.PatientId, "2345-7", "Глюкоза",
            "5.6", At);
        var repository = new Mock<IFhirReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetPatientAsync(id, token)).ReturnsAsync(patient);
        repository.Setup(x => x.GetPractitionerAsync(id, token)).ReturnsAsync(practitioner);
        repository.Setup(x => x.GetServiceRequestAsync(id, token)).ReturnsAsync(request);
        repository.Setup(x => x.GetObservationAsync(observation.Id, token)).ReturnsAsync(observation);

        (await new GetFhirPatientQueryHandler(repository.Object).Handle(new GetFhirPatientQuery(id), token))
            .Should().BeSameAs(patient);
        (await new GetFhirPractitionerQueryHandler(repository.Object).Handle(new GetFhirPractitionerQuery(id), token))
            .Should().BeSameAs(practitioner);
        (await new GetFhirServiceRequestQueryHandler(repository.Object).Handle(new GetFhirServiceRequestQuery(id), token))
            .Should().BeSameAs(request);
        (await new GetFhirObservationQueryHandler(repository.Object).Handle(new GetFhirObservationQuery(observation.Id), token))
            .Should().BeSameAs(observation);
        repository.VerifyAll();
    }

    [Fact]
    public async Task UnknownResources_ShouldRaiseNotFoundForAllReadQueries()
    {
        var id = Guid.NewGuid();
        var repository = Mock.Of<IFhirReadRepository>();
        Func<Task>[] reads =
        [
            async () => await new GetFhirPatientQueryHandler(repository).Handle(new GetFhirPatientQuery(id), CancellationToken.None),
            async () => await new GetFhirPractitionerQueryHandler(repository).Handle(new GetFhirPractitionerQuery(id), CancellationToken.None),
            async () => await new GetFhirServiceRequestQueryHandler(repository).Handle(new GetFhirServiceRequestQuery(id), CancellationToken.None),
            async () => await new GetFhirObservationQueryHandler(repository).Handle(new GetFhirObservationQuery(id), CancellationToken.None)
        ];

        foreach (var read in reads) await read.Should().ThrowAsync<EntityNotFoundException>();
    }

    [Fact]
    public async Task EmptyIds_ShouldBeRejectedBeforeAnyRepositoryRead()
    {
        var repository = new Mock<IFhirReadRepository>(MockBehavior.Strict);
        Func<Task>[] reads =
        [
            async () => await new GetFhirPatientQueryHandler(repository.Object).Handle(new GetFhirPatientQuery(Guid.Empty), CancellationToken.None),
            async () => await new GetFhirPractitionerQueryHandler(repository.Object).Handle(new GetFhirPractitionerQuery(Guid.Empty), CancellationToken.None),
            async () => await new GetFhirServiceRequestQueryHandler(repository.Object).Handle(new GetFhirServiceRequestQuery(Guid.Empty), CancellationToken.None),
            async () => await new GetFhirObservationQueryHandler(repository.Object).Handle(new GetFhirObservationQuery(Guid.Empty), CancellationToken.None),
            async () => await new SearchFhirObservationsQueryHandler(repository.Object).Handle(new SearchFhirObservationsQuery(Guid.Empty, 20, null), CancellationToken.None)
        ];

        foreach (var read in reads) await read.Should().ThrowAsync<DomainValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Searches_ShouldForwardExactFilterPageAndCancellationIncludingEmptyResults()
    {
        var repository = new Mock<IFhirReadRepository>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var after = new FhirPagePosition(At, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        var requests = new FhirReadPage<FhirServiceRequestReadModel>([], null);
        var observations = new FhirReadPage<FhirObservationReadModel>([], null);
        repository.Setup(x => x.SearchServiceRequestsAsync(LaboratoryAnalysisRequestStatus.Completed, 7, after, token))
            .ReturnsAsync(requests);
        repository.Setup(x => x.SearchObservationsAsync(requestId, 1, after, token)).ReturnsAsync(observations);

        (await new SearchFhirServiceRequestsQueryHandler(repository.Object).Handle(
            new SearchFhirServiceRequestsQuery(LaboratoryAnalysisRequestStatus.Completed, 7, after), token))
            .Should().BeSameAs(requests);
        (await new SearchFhirObservationsQueryHandler(repository.Object).Handle(
            new SearchFhirObservationsQuery(requestId, 1, after), token)).Should().BeSameAs(observations);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task InvalidPageSize_ShouldRejectBothSearchesBeforeRepositoryAccess(int count)
    {
        var repository = new Mock<IFhirReadRepository>(MockBehavior.Strict);
        var requests = () => new SearchFhirServiceRequestsQueryHandler(repository.Object).Handle(
            new SearchFhirServiceRequestsQuery(null, count, null), CancellationToken.None);
        var observations = () => new SearchFhirObservationsQueryHandler(repository.Object).Handle(
            new SearchFhirObservationsQuery(Guid.NewGuid(), count, null), CancellationToken.None);

        await requests.Should().ThrowAsync<DomainValidationException>();
        await observations.Should().ThrowAsync<DomainValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task UnsupportedStatus_ShouldRejectBeforeRepositoryAccess(int status)
    {
        var repository = new Mock<IFhirReadRepository>(MockBehavior.Strict);
        var query = () => new SearchFhirServiceRequestsQueryHandler(repository.Object).Handle(
            new SearchFhirServiceRequestsQuery((LaboratoryAnalysisRequestStatus)status, 20, null), CancellationToken.None);

        await query.Should().ThrowAsync<DomainValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("offset")]
    [InlineData("precision")]
    public async Task InvalidCursorPosition_ShouldBeRejectedBeforeRepositoryAccess(string kind)
    {
        var after = new FhirPagePosition(kind == "offset" ? At.ToOffset(TimeSpan.FromHours(3)) :
            kind == "precision" ? At.AddTicks(10) : At, kind == "empty-id" ? Guid.Empty : Guid.NewGuid());
        var repository = new Mock<IFhirReadRepository>(MockBehavior.Strict);
        var query = () => new SearchFhirServiceRequestsQueryHandler(repository.Object).Handle(
            new SearchFhirServiceRequestsQuery(null, 20, after), CancellationToken.None);

        await query.Should().ThrowAsync<DomainValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelledRepositoryRead_ShouldNotTurnCancellationIntoNotFound()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var id = Guid.NewGuid();
        var repository = new Mock<IFhirReadRepository>();
        repository.Setup(x => x.GetPatientAsync(id, token)).Returns(Task.FromCanceled<Patient?>(token));

        var read = () => new GetFhirPatientQueryHandler(repository.Object).Handle(new GetFhirPatientQuery(id), token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
