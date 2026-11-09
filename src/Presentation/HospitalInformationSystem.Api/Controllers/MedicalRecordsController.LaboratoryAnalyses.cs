using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.MedicalRecords.Commands.CreateLaboratoryAnalysisRequests;
using HospitalInformationSystem.Contracts.MedicalRecords;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Controllers;

public sealed partial class MedicalRecordsController
{
    [HttpPost("{id:guid}/laboratory-analysis-requests")]
    [Authorize(Policy = ApiSecurityPolicies.EditMedicalRecord)]
    [ProducesResponseType<IReadOnlyList<LaboratoryAnalysisRequestDto>>(StatusCodes.Status201Created)]
    public async Task<ActionResult<IReadOnlyList<LaboratoryAnalysisRequestDto>>> CreateLaboratoryAnalysisRequestsAsync(
        Guid id, CreateLaboratoryAnalysisRequestsRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateLaboratoryAnalysisRequestsCommand(id,
            request.LaboratoryTestDefinitionIds, request.Comment), cancellationToken);
        return CreatedAtAction("Get", new { id }, result);
    }
}
