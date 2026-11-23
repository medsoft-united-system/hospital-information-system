using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.LaboratoryTests.Commands.CreateLaboratoryTest;
using HospitalInformationSystem.Application.LaboratoryTests.Commands.SetLaboratoryTestActive;
using HospitalInformationSystem.Application.LaboratoryTests.Commands.UpdateLaboratoryTest;
using HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTest;
using HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTests;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiSecurityPolicies.CatalogRead)]
[Route("api/laboratory-tests")]
public sealed class LaboratoryTestsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LaboratoryTestDefinitionDto>>> GetListAsync(
        CancellationToken cancellationToken, [FromQuery] bool activeOnly = true) =>
        Ok(await sender.Send(new GetLaboratoryTestsQuery(activeOnly), cancellationToken));

    [HttpGet("{id:guid}", Name = "GetLaboratoryTest")]
    public async Task<ActionResult<LaboratoryTestDefinitionDto>> GetAsync(Guid id,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetLaboratoryTestQuery(id), cancellationToken));

    [HttpPost]
    [Authorize(Policy = ApiSecurityPolicies.Administrator)]
    [ProducesResponseType<LaboratoryTestDefinitionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<LaboratoryTestDefinitionDto>> CreateAsync(SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateLaboratoryTestCommand(request.LoincCode, request.DisplayName),
            cancellationToken);
        return CreatedAtRoute("GetLaboratoryTest", new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = ApiSecurityPolicies.Administrator)]
    public async Task<ActionResult<LaboratoryTestDefinitionDto>> UpdateAsync(Guid id,
        SaveLaboratoryTestRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UpdateLaboratoryTestCommand(id, request.LoincCode, request.DisplayName, request.Version ?? 0),
            cancellationToken));

    [HttpPut("{id:guid}/active")]
    [Authorize(Policy = ApiSecurityPolicies.Administrator)]
    public async Task<ActionResult<LaboratoryTestDefinitionDto>> SetActiveAsync(Guid id,
        SetLaboratoryTestActiveRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new SetLaboratoryTestActiveCommand(id, request.IsActive, request.Version), cancellationToken));
}
