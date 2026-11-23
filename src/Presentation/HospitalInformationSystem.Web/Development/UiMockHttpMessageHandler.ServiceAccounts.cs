using System.Net;
using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockHttpMessageHandler
{
    private HttpResponseMessage ServiceAccounts(HttpRequestMessage request, string[] path, string? body)
    {
        if (!state.IsAdministrator) return Forbidden();
        if (path.Length == 2)
        {
            if (request.Method == HttpMethod.Get) return Json(state.ServiceAccounts.Values.OrderBy(x => x.Name).ToArray());
            if (request.Method != HttpMethod.Post) return Error(HttpStatusCode.MethodNotAllowed, "Метод недоступен.");
            var input = Read<CreateServiceAccountRequest>(body);
            if (!ServiceAccountNameRules.IsValid(input.Name)) return BadRequest("Имя должно содержать 3–64 символа.");
            if (!ServiceAccountRoles.IsSupported(input.Role)) return BadRequest("Выберите роль LIS или HL7 Import.");
            var name = input.Name.Trim();
            if (state.ServiceAccounts.Values.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Error(HttpStatusCode.Conflict, "Имя занято.");
            var id = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow;
            var account = new ServiceAccountDto(id, name, input.Role, true, at, at, 1, $"msa_{id:N}.");
            state.ServiceAccounts.Add(id, account);
            state.AddAudit("AccountCreated", "Создан демонстрационный сервисный аккаунт.", id);
            var response = Json(new ServiceAccountKeyResultDto(account, MockServiceKey(account), 0), HttpStatusCode.Created);
            response.Headers.Location = new Uri($"api/service-accounts/{id:D}", UriKind.Relative);
            return response;
        }
        if (path.Length is not (3 or 4) || !state.ServiceAccounts.TryGetValue(Id(path[2]), out var current)) return NotFound();
        if (path.Length == 3 && request.Method == HttpMethod.Get) return Json(current);
        if (path.Length == 3 && request.Method == HttpMethod.Put)
        {
            var input = Read<UpdateServiceAccountRequest>(body);
            if (input.Version != current.Version) return Error(HttpStatusCode.Conflict, "Версия изменилась.");
            if (!ServiceAccountNameRules.IsValid(input.Name)) return BadRequest("Имя должно содержать 3–64 символа.");
            var name = input.Name.Trim();
            if (state.ServiceAccounts.Values.Any(x => x.Id != current.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Error(HttpStatusCode.Conflict, "Имя занято.");
            var saved = current with { Name = name, Version = checked(current.Version + 1), UpdatedAtUtc = DateTimeOffset.UtcNow };
            state.ServiceAccounts[current.Id] = saved;
            state.AddAudit("AccountChanged", "Изменено имя демонстрационного сервисного аккаунта.", current.Id);
            return Json(saved);
        }
        if (path.Length == 4 && path[3] == "active" && request.Method == HttpMethod.Put)
        {
            var input = Read<SetServiceAccountActiveRequest>(body);
            if (input.Version != current.Version) return Error(HttpStatusCode.Conflict, "Версия изменилась.");
            var saved = current.IsActive == input.IsActive ? current : current with
            { IsActive = input.IsActive, Version = checked(current.Version + 1), UpdatedAtUtc = DateTimeOffset.UtcNow };
            state.ServiceAccounts[current.Id] = saved;
            if (saved != current) state.AddAudit("AccountChanged", "Изменён статус демонстрационного сервисного аккаунта.", current.Id);
            return Json(new ServiceAccountActivityResultDto(saved, 0));
        }
        if (path.Length == 4 && path[3] == "rotate-key" && request.Method == HttpMethod.Post)
        {
            var input = Read<RotateServiceAccountKeyRequest>(body);
            if (input.Version != current.Version) return Error(HttpStatusCode.Conflict, "Версия изменилась.");
            var saved = current with { Version = checked(current.Version + 1), UpdatedAtUtc = DateTimeOffset.UtcNow };
            state.ServiceAccounts[current.Id] = saved;
            state.AddAudit("AccountChanged", "Заменён демонстрационный ключ сервисного аккаунта.", current.Id);
            return Json(new ServiceAccountKeyResultDto(saved, MockServiceKey(saved), 0));
        }
        return Error(HttpStatusCode.MethodNotAllowed, "Удаление сервисного аккаунта недоступно.");
    }

    private static string MockServiceKey(ServiceAccountDto account) => account.KeyPrefix +
        Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
