using HospitalInformationSystem.Contracts.LaboratoryTests;

namespace HospitalInformationSystem.Web.ApiClients;

public sealed class LaboratoryTestsApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<LaboratoryTestDefinitionDto>> ListAsync(
        CancellationToken cancellationToken, bool activeOnly = true)
    {
        using var response = await http.GetAsync(
            $"api/laboratory-tests?activeOnly={activeOnly.ToString().ToLowerInvariant()}", cancellationToken);
        return await ApiResponseReader.ReadListAsync<LaboratoryTestDefinitionDto>(response, cancellationToken);
    }

    public async Task<LaboratoryTestDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/laboratory-tests/{id}", cancellationToken);
        return await ApiResponseReader.ReadAsync<LaboratoryTestDefinitionDto>(response, cancellationToken);
    }

    public Task<LaboratoryTestDefinitionDto> CreateAsync(SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync<LaboratoryTestDefinitionDto>(HttpMethod.Post, "api/laboratory-tests", request, null,
            cancellationToken);

    public Task<LaboratoryTestDefinitionDto> UpdateAsync(Guid id, SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync<LaboratoryTestDefinitionDto>(HttpMethod.Put, $"api/laboratory-tests/{id}", request, null,
            cancellationToken);

    public Task<LaboratoryTestDefinitionDto> SetActiveAsync(Guid id, bool isActive,
        CancellationToken cancellationToken) =>
        http.SendJsonAsync<LaboratoryTestDefinitionDto>(HttpMethod.Put, $"api/laboratory-tests/{id}/active",
            new SetLaboratoryTestActiveRequest(isActive), null, cancellationToken);
}
