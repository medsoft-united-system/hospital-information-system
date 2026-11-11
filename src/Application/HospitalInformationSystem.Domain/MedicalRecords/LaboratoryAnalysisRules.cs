using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.MedicalRecords;

public static class LaboratoryAnalysisRules
{
    public const int MaxBatchSize = 100;
    public const int MaxCommentLength = 2000;
    public const int MaxValueLength = 2000;

    public static bool IsValidComment(string? comment) => comment is null ||
        !comment.Contains('\0') && comment.Trim().EnumerateRunes().Count() <= MaxCommentLength;

    public static bool IsValidValue(string? value) => value is not null && !value.Contains('\0') &&
        value.Trim().EnumerateRunes().Count() is >= 1 and <= MaxValueLength;

    public static bool IsValidIssuedAt(DateTimeOffset value) => value != default &&
        value.UtcTicks % TimeSpan.TicksPerMillisecond == 0;

    public static string? NormalizeComment(string? comment)
    {
        if (!IsValidComment(comment))
            throw new DomainValidationException("Комментарий к анализу: не более 2000 символов без нулевого символа.");
        var normalized = comment?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    public static string NormalizeValue(string value)
    {
        if (!IsValidValue(value))
            throw new DomainValidationException("Результат анализа: от 1 до 2000 символов без нулевого символа.");
        return value.Trim();
    }

    public static DateTimeOffset NormalizeIssuedAt(DateTimeOffset issuedAt)
    {
        if (!IsValidIssuedAt(issuedAt))
            throw new DomainValidationException("Укажите время фиксации результата с точностью не более миллисекунд.");
        return issuedAt.ToUniversalTime();
    }
}
