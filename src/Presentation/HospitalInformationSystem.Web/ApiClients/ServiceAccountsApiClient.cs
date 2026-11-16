using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalInformationSystem.Contracts.ServiceAccounts;

namespace HospitalInformationSystem.Web.ApiClients;

public sealed class ServiceAccountsApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private const string InvalidResponse = "Сервер вернул некорректный ответ. Обновите список и проверьте состояние аккаунта перед новой операцией.";

    public async Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await SendAsync<ServiceAccountDto[]>(HttpMethod.Get, "api/service-accounts", null,
            HttpStatusCode.OK, cancellationToken);
        if (items.Any(item => item is null || !Valid(item)) || items.Select(item => item.Id).Distinct().Count() != items.Length)
            throw Invalid();
        return items;
    }

    public async Task<ServiceAccountDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireId(id);
        var saved = await SendAsync<ServiceAccountDto>(HttpMethod.Get, $"api/service-accounts/{id:D}", null,
            HttpStatusCode.OK, cancellationToken);
        if (!Valid(saved) || saved.Id != id) throw Invalid();
        return saved;
    }

    public async Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest input, CancellationToken cancellationToken)
    {
        var saved = await SendAsync<ServiceAccountKeyResultDto>(HttpMethod.Post, "api/service-accounts", input,
            HttpStatusCode.Created, cancellationToken);
        if (!ValidKeyResult(saved) || saved.Account.Name != input.Name.Trim() || saved.Account.Role != input.Role || !saved.Account.IsActive || saved.RevokedSessionCount != 0)
            throw Invalid();
        return saved;
    }

    public async Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest input, CancellationToken cancellationToken)
    {
        RequireId(id);
        var saved = await SendAsync<ServiceAccountDto>(HttpMethod.Put, $"api/service-accounts/{id:D}", input,
            HttpStatusCode.OK, cancellationToken);
        if (!Valid(saved) || saved.Id != id || saved.Name != input.Name.Trim()) throw Invalid();
        return saved;
    }

    public async Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id, SetServiceAccountActiveRequest input,
        CancellationToken cancellationToken)
    {
        RequireId(id);
        var saved = await SendAsync<ServiceAccountActivityResultDto>(HttpMethod.Put, $"api/service-accounts/{id:D}/active", input,
            HttpStatusCode.OK, cancellationToken);
        if (saved.Account is null || !Valid(saved.Account) || saved.Account.Id != id || saved.Account.IsActive != input.IsActive ||
            saved.RevokedSessionCount < 0) throw Invalid();
        return saved;
    }

    public async Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest input,
        CancellationToken cancellationToken)
    {
        RequireId(id);
        var saved = await SendAsync<ServiceAccountKeyResultDto>(HttpMethod.Post, $"api/service-accounts/{id:D}/rotate-key", input,
            HttpStatusCode.OK, cancellationToken);
        if (!ValidKeyResult(saved) || saved.Account.Id != id || saved.Account.Version == input.Version) throw Invalid();
        return saved;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        if (method != HttpMethod.Get) request.Options.Set(ApiRequestOptions.NoAutomaticRetry, true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        if (response.StatusCode != expected || !string.Equals(response.Content.Headers.ContentType?.MediaType,
                "application/json", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            const int limit = 1024 * 1024;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)data.Length)), cancellationToken);
                if (read == 0) break;
                data.Write(buffer, 0, read);
                if (data.Length > limit) throw Invalid();
            }
            using var document = JsonDocument.Parse(data.ToArray());
            if (!UniqueProperties(document.RootElement)) throw Invalid();
            return document.Deserialize<T>(JsonOptions) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        catch (NotSupportedException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
    }

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().All(UniqueProperties);
        if (element.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(property => names.Add(property.Name) && UniqueProperties(property.Value));
    }

    private static bool Valid(ServiceAccountDto account) => account.Id != Guid.Empty &&
        ServiceAccountNameRules.IsValid(account.Name) && account.Name == account.Name.Trim() && ServiceAccountRoles.IsSupported(account.Role) &&
        account.CreatedAtUtc != default && account.CreatedAtUtc.Offset == TimeSpan.Zero &&
        account.UpdatedAtUtc >= account.CreatedAtUtc && account.UpdatedAtUtc.Offset == TimeSpan.Zero &&
        account.Version > 0 && account.KeyPrefix == $"msa_{account.Id:N}.";

    private static bool ValidKeyResult(ServiceAccountKeyResultDto result)
    {
        if (result.Account is null || !Valid(result.Account) || result.RevokedSessionCount < 0 || result.ApiKey is null ||
            !result.ApiKey.StartsWith(result.Account.KeyPrefix, StringComparison.Ordinal)) return false;
        var secret = result.ApiKey[result.Account.KeyPrefix.Length..];
        if (secret.Length != 43 || secret.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(secret.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == 32 && Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == secret;
        }
        catch (FormatException) { return false; }
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty) throw new ApiException("Выберите сервисный аккаунт.", HttpStatusCode.BadRequest);
    }

    private static ApiException Invalid() => new(InvalidResponse);
    private static ApiException Failure(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => "Сессия завершена. Войдите снова и проверьте состояние аккаунта.",
        HttpStatusCode.Forbidden => "У текущего пользователя нет прав управления сервисными аккаунтами.",
        HttpStatusCode.Conflict => "Аккаунт уже изменён или имя занято. Обновите список перед новой операцией.",
        HttpStatusCode.BadRequest => "Проверьте имя, роль и актуальность версии сервисного аккаунта.",
        HttpStatusCode.NotFound => "Сервисный аккаунт не найден. Обновите список.",
        _ => "Не удалось подтвердить результат операции. Обновите список перед новой отправкой."
    }, status);
}
