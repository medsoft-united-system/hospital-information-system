using HospitalInformationSystem.Contracts.LaboratoryTests;

namespace HospitalInformationSystem.Web.ApiClients;

public sealed class LaboratoryTestsApiClient(HttpClient http)
{
    private const string MissingVersionMessage =
        "Сервер вернул некорректный ответ без версии анализа. Обновите данные, чтобы проверить результат операции.";

    public async Task<IReadOnlyList<LaboratoryTestDefinitionDto>> ListAsync(
        CancellationToken cancellationToken, bool activeOnly = true)
    {
        using var response = await http.GetAsync(
            $"api/laboratory-tests?activeOnly={activeOnly.ToString().ToLowerInvariant()}", cancellationToken);
        var tests = await ApiResponseReader.ReadListAsync<LaboratoryTestDefinitionDto>(response, cancellationToken);
        if (tests.Any(test => test.Version == 0)) throw new ApiException(MissingVersionMessage);
        return tests;
    }

    public async Task<LaboratoryTestDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/laboratory-tests/{id}", cancellationToken);
        return RequireVersion(await ApiResponseReader.ReadAsync<LaboratoryTestDefinitionDto>(response, cancellationToken));
    }

    public async Task<LaboratoryTestDefinitionDto> CreateAsync(SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken) =>
        RequireVersion(await http.SendJsonAsync<LaboratoryTestDefinitionDto>(
            HttpMethod.Post, "api/laboratory-tests", request, null, cancellationToken));

    public async Task<LaboratoryTestDefinitionDto> UpdateAsync(Guid id, SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken) =>
        RequireVersion(await http.SendJsonAsync<LaboratoryTestDefinitionDto>(
            HttpMethod.Put, $"api/laboratory-tests/{id}", request, null, cancellationToken));

    public async Task<LaboratoryTestDefinitionDto> SetActiveAsync(Guid id, bool isActive, uint version,
        CancellationToken cancellationToken) =>
        RequireVersion(await http.SendJsonAsync<LaboratoryTestDefinitionDto>(HttpMethod.Put, $"api/laboratory-tests/{id}/active",
            new SetLaboratoryTestActiveRequest(isActive, version), null, cancellationToken));

    private static LaboratoryTestDefinitionDto RequireVersion(LaboratoryTestDefinitionDto test) =>
        test.Version > 0 ? test : throw new ApiException(MissingVersionMessage);
}
