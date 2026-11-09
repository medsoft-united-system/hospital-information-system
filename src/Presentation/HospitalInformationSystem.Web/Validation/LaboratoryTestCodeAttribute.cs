using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace HospitalInformationSystem.Web.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed partial class LaboratoryTestCodeAttribute : ValidationAttribute
{
    public LaboratoryTestCodeAttribute()
        : base("Код LOINC должен содержать от 3 до 32 символов: цифры, дефис и одну завершающую цифру.") { }

    public override bool IsValid(object? value) => value is null ||
        value is string text && text.Trim() is { Length: >= 3 and <= 32 } code && CodePattern().IsMatch(code);

    [GeneratedRegex(@"\A[0-9]+-[0-9]\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CodePattern();
}
