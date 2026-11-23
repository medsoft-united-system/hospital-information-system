using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Web.ApiClients;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryTestsApiClientTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task List_uses_explicit_active_filter(bool activeOnly, string expected)
    {
        string? uri = null;
        using var http = CreateClient((request, _) =>
        {
            uri = request.RequestUri!.PathAndQuery;
            return Task.FromResult(Json(Array.Empty<LaboratoryTestDefinitionDto>()));
        });

        await new LaboratoryTestsApiClient(http).ListAsync(CancellationToken.None, activeOnly);

        uri.Should().Be($"/api/laboratory-tests?activeOnly={expected}");
    }

    [Fact]
    public async Task Clinical_list_default_excludes_inactive_tests()
    {
        string? query = null;
        using var http = CreateClient((request, _) =>
        {
            query = request.RequestUri!.Query;
            return Task.FromResult(Json(Array.Empty<LaboratoryTestDefinitionDto>()));
        });

        await new LaboratoryTestsApiClient(http).ListAsync(CancellationToken.None);

        query.Should().Be("?activeOnly=true");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_changes_send_explicit_boolean_and_return_server_state(bool active)
    {
        var test = Definition(active);
        string? path = null;
        bool? sentActive = null;
        uint? sentVersion = null;
        HttpMethod? method = null;
        using var http = CreateClient(async (request, cancellationToken) =>
        {
            path = request.RequestUri!.AbsolutePath;
            method = request.Method;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            sentActive = body.RootElement.GetProperty("isActive").GetBoolean();
            sentVersion = body.RootElement.GetProperty("version").GetUInt32();
            return Json(test);
        });

        var saved = await new LaboratoryTestsApiClient(http).SetActiveAsync(test.Id, active, 16, CancellationToken.None);

        path.Should().Be($"/api/laboratory-tests/{test.Id}/active");
        method.Should().Be(HttpMethod.Put);
        sentActive.Should().Be(active);
        sentVersion.Should().Be(16);
        saved.Should().Be(test);
        saved.Version.Should().Be(17);
    }

    [Fact]
    public async Task Update_sends_the_version_that_was_read_and_returns_the_new_version()
    {
        var updated = Definition(true) with { Version = 18 };
        uint? sentVersion = null;
        using var http = CreateClient(async (request, cancellationToken) =>
        {
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.AbsolutePath.Should().Be($"/api/laboratory-tests/{updated.Id}");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            sentVersion = body.RootElement.GetProperty("version").GetUInt32();
            return Json(updated);
        });

        var saved = await new LaboratoryTestsApiClient(http).UpdateAsync(updated.Id,
            new SaveLaboratoryTestRequest(updated.LoincCode, updated.DisplayName, 17), CancellationToken.None);

        sentVersion.Should().Be(17);
        saved.Should().Be(updated);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("status")]
    public async Task A_successful_response_without_a_usable_version_is_rejected(string operation)
    {
        var test = Definition(true) with { Version = 0 };
        using var http = CreateClient((_, _) => Task.FromResult(operation == "list" ? Json(new[] { test }) : Json(test)));
        var client = new LaboratoryTestsApiClient(http);
        Func<Task> action = operation switch
        {
            "list" => () => client.ListAsync(CancellationToken.None),
            "get" => () => client.GetAsync(test.Id, CancellationToken.None),
            "create" => () => client.CreateAsync(new SaveLaboratoryTestRequest(test.LoincCode, test.DisplayName), CancellationToken.None),
            "update" => () => client.UpdateAsync(test.Id, new SaveLaboratoryTestRequest(test.LoincCode, test.DisplayName, 17), CancellationToken.None),
            _ => () => client.SetActiveAsync(test.Id, false, 17, CancellationToken.None)
        };

        await action.Should().ThrowAsync<ApiException>().WithMessage("*некорректный ответ*версии*");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("{\"items\":[]}")]
    public async Task Malformed_success_response_is_not_treated_as_an_empty_catalog(string body)
    {
        using var http = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));

        var action = () => new LaboratoryTestsApiClient(http).ListAsync(CancellationToken.None);

        await action.Should().ThrowAsync<ApiException>().WithMessage("*некорректный ответ*");
    }

    [Fact]
    public async Task Conflict_reports_server_validation_message()
    {
        using var http = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { detail = "Код LOINC уже существует." })
        }));

        var action = () => new LaboratoryTestsApiClient(http).CreateAsync(
            new SaveLaboratoryTestRequest("2345-7", "Глюкоза в крови"), CancellationToken.None);

        (await action.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static LaboratoryTestDefinitionDto Definition(bool active) => new(Guid.NewGuid(), "2345-7",
        "Глюкоза в крови", active, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 17);

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ResponseHandler(send)) { BaseAddress = new Uri("https://localhost/") };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
