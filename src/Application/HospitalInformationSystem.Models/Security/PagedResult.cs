using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.Security;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasMore);
