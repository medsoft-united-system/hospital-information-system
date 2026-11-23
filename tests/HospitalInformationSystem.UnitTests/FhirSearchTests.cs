using FluentAssertions;
using HospitalInformationSystem.Api.Fhir;
using HospitalInformationSystem.Models.Fhir;
using HospitalInformationSystem.Models.MedicalRecords;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace HospitalInformationSystem.UnitTests;

public sealed class FhirSearchTests
{
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly FhirPagePosition Position = new(
        new DateTimeOffset(2026, 11, 9, 12, 5, 3, 123, TimeSpan.Zero),
        Guid.Parse("22222222-2222-4222-8222-222222222222"));

    [Fact]
    public void SearchServiceRequests_WithoutParameters_ShouldUseBothStatesAndTwentyResults()
    {
        var parsed = Parser().ParseServiceRequests(Query(""));

        parsed.ResourceType.Should().Be("ServiceRequest");
        parsed.Status.Should().BeNull();
        parsed.BasedOn.Should().BeNull();
        parsed.Count.Should().Be(20);
        parsed.After.Should().BeNull();
        parsed.Cursor.Should().BeNull();
    }

    [Theory]
    [InlineData("active", LaboratoryAnalysisRequestStatus.Active)]
    [InlineData("completed", LaboratoryAnalysisRequestStatus.Completed)]
    public void SearchServiceRequests_SupportedStatus_ShouldParseExplicitState(string text,
        LaboratoryAnalysisRequestStatus expected)
    {
        var parsed = Parser().ParseServiceRequests(Query("?status=" + text + "&_count=100"));

        parsed.Status.Should().Be(expected);
        parsed.Count.Should().Be(100);
    }

    [Theory]
    [InlineData("?status=")]
    [InlineData("?status=ACTIVE")]
    [InlineData("?status=unknown")]
    [InlineData("?status=active,completed")]
    [InlineData("?status=active&status=completed")]
    [InlineData("?status=active&STATUS=active")]
    [InlineData("?STATUS=active")]
    [InlineData("?_count=1&_count=1")]
    [InlineData("?_count=")]
    [InlineData("?_count=0")]
    [InlineData("?_count=101")]
    [InlineData("?_count=-1")]
    [InlineData("?_count=%2B1")]
    [InlineData("?_count=1.5")]
    [InlineData("?_count=%201")]
    [InlineData("?_count=1%20")]
    [InlineData("?_count=١")]
    [InlineData("?_count=2147483648")]
    [InlineData("?_cursor=")]
    [InlineData("?_cursor=fixture-page-2")]
    [InlineData("?_cursor=abc&_cursor=abc")]
    [InlineData("?patient=11111111-1111-4111-8111-111111111111")]
    [InlineData("?_format=json")]
    [InlineData("?_Count=20")]
    public void SearchServiceRequests_InvalidOrAmbiguousQuery_ShouldReturnInvalidSearch(string query)
    {
        var action = () => Parser().ParseServiceRequests(Query(query));

        var error = action.Should().Throw<FhirHttpException>().Which;
        error.StatusCode.Should().Be(400);
        error.MachineCode.Should().Be("invalid_search");
    }

    [Fact]
    public void SearchObservations_ValidRelativeReference_ShouldNormalizeUuid()
    {
        var parsed = Parser().ParseObservations(Query($"?based-on=ServiceRequest/{RequestId:D}&_count=1"));

        parsed.ResourceType.Should().Be("Observation");
        parsed.BasedOn.Should().Be(RequestId);
        parsed.Status.Should().BeNull();
        parsed.Count.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?based-on=")]
    [InlineData("?based-on=ServiceRequest/not-a-uuid")]
    [InlineData("?based-on=Patient/11111111-1111-4111-8111-111111111111")]
    [InlineData("?based-on=servicerequest/11111111-1111-4111-8111-111111111111")]
    [InlineData("?based-on=https://his-api:5201/fhir/ServiceRequest/11111111-1111-4111-8111-111111111111")]
    [InlineData("?based-on=urn:uuid:11111111-1111-4111-8111-111111111111")]
    [InlineData("?based-on=../ServiceRequest/11111111-1111-4111-8111-111111111111")]
    [InlineData("?based-on=ServiceRequest/11111111111141118111111111111111")]
    [InlineData("?based-on=ServiceRequest/00000000-0000-0000-0000-000000000000")]
    [InlineData("?based-on=ServiceRequest/11111111-1111-4111-8111-111111111111/_history/1")]
    [InlineData("?based-on=ServiceRequest/11111111-1111-4111-8111-111111111111%23fragment")]
    [InlineData("?based-on=ServiceRequest/11111111-1111-4111-8111-111111111111%3Fpatient=other")]
    [InlineData("?based-on=ServiceRequest/11111111-1111-4111-8111-111111111111&based-on=ServiceRequest/11111111-1111-4111-8111-111111111111")]
    [InlineData("?based-on=ServiceRequest/11111111-1111-4111-8111-111111111111&status=final")]
    public void SearchObservations_InvalidReferenceOrUnsupportedQuery_ShouldReturnInvalidSearch(string query)
    {
        var action = () => Parser().ParseObservations(Query(query));

        var error = action.Should().Throw<FhirHttpException>().Which;
        error.StatusCode.Should().Be(400);
        error.MachineCode.Should().Be("invalid_search");
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("{11111111-1111-4111-8111-111111111111}")]
    [InlineData("11111111111141118111111111111111")]
    [InlineData(" 11111111-1111-4111-8111-111111111111")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Read_NonCanonicalOrEmptyId_ShouldReturnInvalidResource(string value)
    {
        var action = () => FhirSearchParser.ParseResourceId(value);

        var error = action.Should().Throw<FhirHttpException>().Which;
        error.StatusCode.Should().Be(400);
        error.MachineCode.Should().Be("invalid_resource");
    }

    [Fact]
    public void MetadataOrRead_UnknownQuery_ShouldRejectInsteadOfSilentlyIgnoring()
    {
        var action = () => FhirSearchParser.RejectQuery(Query("?_pretty=true"));

        action.Should().Throw<FhirHttpException>().Which.MachineCode.Should().Be("invalid_search");
        FhirSearchParser.RejectQuery(Query(""));
    }

    [Fact]
    public void Cursor_ShouldRoundTripBoundQueryAndUtcPagePositionWithoutExposingIdentifiers()
    {
        var protector = Protector();
        var parser = new FhirSearchParser(protector);
        var search = parser.ParseServiceRequests(Query("?status=active&_count=2"));
        var token = protector.Protect(search, Position with { AtUtc = Position.AtUtc.ToOffset(TimeSpan.FromHours(3)) });

        var next = parser.ParseServiceRequests(Query("?status=active&_count=2&_cursor=" + token));

        next.After.Should().Be(Position);
        next.After!.AtUtc.Offset.Should().Be(TimeSpan.Zero);
        token.Should().NotContain(Position.Id.ToString("D")).And.NotContain("ServiceRequest");
        next.Cursor.Should().Be(token);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("count")]
    [InlineData("resource")]
    [InlineData("tamper")]
    [InlineData("other-key")]
    [InlineData("length")]
    public void Cursor_CrossQueryTamperedOrOversizedToken_ShouldReject(string invalid)
    {
        var protector = Protector();
        var search = new FhirSearchParameters("ServiceRequest", LaboratoryAnalysisRequestStatus.Active, null,
            2, null, null);
        var token = protector.Protect(search, Position);
        if (invalid == "tamper")
        {
            var index = token.Length / 2;
            token = token[..index] + (token[index] == 'A' ? 'B' : 'A') + token[(index + 1)..];
        }
        if (invalid == "length") token = new string('A', 4097);
        if (invalid == "other-key") protector = Protector();

        var action = () => protector.Unprotect(token,
            invalid == "resource" ? "Observation" : search.ResourceType,
            invalid == "status" ? LaboratoryAnalysisRequestStatus.Completed : search.Status,
            null, invalid == "count" ? 3 : search.Count);

        var error = action.Should().Throw<FhirHttpException>().Which;
        error.StatusCode.Should().Be(400);
        error.MachineCode.Should().Be("invalid_search");
    }

    [Fact]
    public void ObservationCursor_ShouldNotBeReusableForAnotherBasedOnReference()
    {
        var protector = Protector();
        var search = new FhirSearchParameters("Observation", null, RequestId, 20, null, null);
        var token = protector.Protect(search, Position);

        var action = () => protector.Unprotect(token, "Observation", null, Guid.NewGuid(), 20);

        action.Should().Throw<FhirHttpException>().Which.MachineCode.Should().Be("invalid_search");
    }

    [Fact]
    public void Links_ShouldUseTrustedConfiguredOriginAndPathBaseAndPreserveCanonicalSearch()
    {
        var options = new FhirEndpointOptions("https://his-api:5201/hospital/fhir/");
        var protector = Protector();
        var search = new FhirSearchParameters("Observation", null, RequestId, 20, null, null);
        var token = protector.Protect(search, Position);

        var next = new Uri(options.SearchUrl(search, token));
        var query = Query(next.Query);

        next.GetLeftPart(UriPartial.Authority).Should().Be("https://his-api:5201");
        next.AbsolutePath.Should().Be("/hospital/fhir/Observation");
        query["based-on"].ToString().Should().Be($"ServiceRequest/{RequestId:D}");
        query["_count"].ToString().Should().Be("20");
        query["_cursor"].ToString().Should().Be(token);
    }

    [Fact]
    public void EndpointConfiguration_ShouldHaveSafeLocalDefaultAndUseExplicitSetting()
    {
        new FhirEndpointOptions(new ConfigurationBuilder().Build()).PublicBaseUrl
            .Should().Be("https://localhost:5201/fhir");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [FhirEndpointOptions.ConfigurationKey] = "https://hospital.example.edu/prefix/fhir" }).Build();

        new FhirEndpointOptions(configuration).PublicBaseUrl.Should().Be("https://hospital.example.edu/prefix/fhir");
    }

    [Theory]
    [InlineData("http://his-api:5201/fhir")]
    [InlineData("/fhir")]
    [InlineData("https://user:password@his-api:5201/fhir")]
    [InlineData("https://his-api:5201/fhir?secret=value")]
    [InlineData("https://his-api:5201/fhir#fragment")]
    [InlineData("https://his-api:5201/other")]
    [InlineData("https://his-api:5201/fhir2")]
    [InlineData("https://his-api:5201//fhir")]
    [InlineData("https://his-api:5201/prefix/../fhir")]
    [InlineData("https://his-api:5201/prefix%2Ffhir")]
    [InlineData("https://his-api:5201\\other/fhir")]
    [InlineData(" https://his-api:5201/fhir")]
    public void UnsafeOrAmbiguousPublicBaseUrl_ShouldFailAtStartup(string value)
    {
        var action = () => new FhirEndpointOptions(value);

        action.Should().Throw<InvalidOperationException>();
    }

    private static QueryCollection Query(string value) => new(QueryHelpers.ParseQuery(value));
    private static FhirCursorProtector Protector() => new(new EphemeralDataProtectionProvider());
    private static FhirSearchParser Parser() => new(Protector());
}
