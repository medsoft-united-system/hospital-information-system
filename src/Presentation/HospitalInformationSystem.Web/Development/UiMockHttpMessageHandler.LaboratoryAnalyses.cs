using System.Net;
using System.Text;
using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockHttpMessageHandler
{
    private HttpResponseMessage LaboratoryTests(HttpRequestMessage request, string[] path, string? body)
    {
        if (request.Method != HttpMethod.Get && !state.IsAdministrator) return Forbidden();
        if (path.Length == 2)
        {
            if (request.Method == HttpMethod.Get)
            {
                var query = Query(request.RequestUri!);
                var activeOnly = true;
                if (query.TryGetValue("activeOnly", out var raw) && !bool.TryParse(raw, out activeOnly))
                    return BadRequest("Параметр activeOnly должен содержать true или false.");
                return Json(state.LaboratoryTests.Values.Where(x => !activeOnly || x.IsActive)
                    .OrderBy(x => x.LoincCode, StringComparer.Ordinal).ToArray());
            }
            if (request.Method != HttpMethod.Post) return Error(HttpStatusCode.MethodNotAllowed, "Метод недоступен.");
            var input = Read<SaveLaboratoryTestRequest>(body);
            if (!ValidLaboratoryDefinition(input)) return BadRequest("Код LOINC: 3–32; название: 3–256 символов.");
            var code = input.LoincCode.Trim();
            if (state.LaboratoryTests.Values.Any(x => x.LoincCode == code))
                return Error(HttpStatusCode.Conflict, "Анализ с таким кодом LOINC уже существует.");
            var now = DateTimeOffset.UtcNow;
            var item = new LaboratoryTestDefinitionDto(Guid.NewGuid(), code, input.DisplayName.Trim(), true, now, now, 1);
            state.LaboratoryTests.Add(item.Id, item);
            var response = Json(item, HttpStatusCode.Created);
            response.Headers.Location = new Uri($"api/laboratory-tests/{item.Id}", UriKind.Relative);
            return response;
        }
        if (path.Length is not (3 or 4) || !state.LaboratoryTests.TryGetValue(Id(path[2]), out var current))
            return NotFound();
        if (path.Length == 4)
        {
            if (path[3] != "active" || request.Method != HttpMethod.Put) return NotFound();
            var input = Read<SetLaboratoryTestActiveRequest>(body);
            if (input.Version == 0) return BadRequest("Передайте прочитанную версию анализа.");
            if (input.Version != current.Version) return Error(HttpStatusCode.Conflict, "Анализ уже изменён. Обновите карточку.");
            var updated = current.IsActive == input.IsActive ? current
                : current with { IsActive = input.IsActive, UpdatedAtUtc = DateTimeOffset.UtcNow, Version = current.Version + 1 };
            state.LaboratoryTests[current.Id] = updated;
            return Json(updated);
        }
        if (request.Method == HttpMethod.Get) return Json(current);
        if (request.Method != HttpMethod.Put) return Error(HttpStatusCode.MethodNotAllowed, "Удаление анализов недоступно.");
        var update = Read<SaveLaboratoryTestRequest>(body);
        if (!ValidLaboratoryDefinition(update)) return BadRequest("Код LOINC: 3–32; название: 3–256 символов.");
        if (update.Version is null or 0) return BadRequest("Передайте прочитанную версию анализа.");
        if (update.Version != current.Version) return Error(HttpStatusCode.Conflict, "Анализ уже изменён. Обновите карточку.");
        var updatedCode = update.LoincCode.Trim();
        if (state.LaboratoryTests.Values.Any(x => x.Id != current.Id && x.LoincCode == updatedCode))
            return Error(HttpStatusCode.Conflict, "Анализ с таким кодом LOINC уже существует.");
        var saved = current with
        {
            LoincCode = updatedCode, DisplayName = update.DisplayName.Trim(), UpdatedAtUtc = DateTimeOffset.UtcNow,
            Version = current.Version + 1
        };
        state.LaboratoryTests[current.Id] = saved;
        return Json(saved);
    }

    private HttpResponseMessage CreateLaboratoryAnalysisRequests(MockMedicalRecord record, string? body)
    {
        var input = Read<CreateLaboratoryAnalysisRequestsRequest>(body);
        var ids = input.LaboratoryTestDefinitionIds;
        if (ids is null || ids.Count is < 1 or > 100 || ids.Any(x => x == Guid.Empty)
            || ids.Distinct().Count() != ids.Count || !ValidOptionalLaboratoryComment(input.Comment))
            return BadRequest("Выберите 1–100 разных анализов; комментарий не должен превышать 2000 символов.");
        var versions = input.ExpectedVersions;
        if (versions is null || versions.Count != ids.Count || versions.Any(x => x.Value == 0 || !ids.Contains(x.Key)))
            return BadRequest("Передайте прочитанные версии всех выбранных анализов.");
        var definitions = new List<LaboratoryTestDefinitionDto>();
        foreach (var id in ids)
        {
            if (!state.LaboratoryTests.TryGetValue(id, out var definition)) return NotFound();
            if (versions[id] != definition.Version)
                return Error(HttpStatusCode.Conflict, "Выбранный анализ уже изменён. Обновите справочник и выберите исследования заново.");
            if (!definition.IsActive) return BadRequest("Неактивный анализ нельзя назначить.");
            definitions.Add(definition);
        }
        var comment = input.Comment?.Trim();
        if (string.IsNullOrEmpty(comment)) comment = null;
        var now = DateTimeOffset.UtcNow;
        var author = state.CurrentSession!.MedicalWorkerId!.Value;
        var worker = state.Workers[author];
        var created = definitions.Select(x => new LaboratoryAnalysisRequestDto(Guid.NewGuid(), record.Current.Id,
            record.Current.PatientId, author, x.Id, x.LoincCode, x.DisplayName, comment, now,
            LaboratoryAnalysisRequestStatus.Active, null, $"{worker.LastName} {worker.FirstName}")).ToArray();
        foreach (var request in created)
        {
            record.Current = record.Current with
            {
                LaboratoryAnalysisRequests = record.Current.LaboratoryAnalysisRequests.Append(request).ToArray()
            };
            state.AppendEvent(record, "LaboratoryAnalysisRequested", request.Id, Payload(new
            {
                LaboratoryAnalysisRequestId = request.Id, request.MedicalRecordId, request.PatientId,
                request.RequestedByMedicalWorkerId, request.LaboratoryTestDefinitionId, request.LoincCode,
                request.DisplayName, request.Comment, request.AuthoredAtUtc
            }), now);
        }
        state.AddAudit("MedicalRecordChanged", "Назначены демонстрационные лабораторные анализы.", record.Current.Id);
        var response = Json(created, HttpStatusCode.Created);
        response.Headers.Location = new Uri($"api/medical-records/{record.Current.Id}", UriKind.Relative);
        return response;
    }

    private static bool ValidLaboratoryDefinition(SaveLaboratoryTestRequest input) =>
        LaboratoryTestCodeRules.IsValidCode(input.LoincCode)
        && ValidLaboratoryText(input.DisplayName, 3, 256);

    private static bool ValidLaboratoryText(string? text, int minimum, int maximum) => text is not null
        && !text.Contains('\0') && text.Trim().EnumerateRunes().Count() >= minimum
        && text.Trim().EnumerateRunes().Count() <= maximum;

    private static bool ValidOptionalLaboratoryComment(string? comment) => comment is null
        || ValidLaboratoryText(comment, 0, 2000);

}
