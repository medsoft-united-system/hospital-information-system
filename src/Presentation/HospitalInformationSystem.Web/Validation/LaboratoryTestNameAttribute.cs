using System.ComponentModel.DataAnnotations;
using System.Text;

namespace HospitalInformationSystem.Web.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class LaboratoryTestNameAttribute : ValidationAttribute
{
    public LaboratoryTestNameAttribute() : base("Наименование должно содержать от 3 до 256 символов без нулевого символа.") { }

    public override bool IsValid(object? value) => value is null || value is string text &&
        !text.Contains('\0') && text.Trim().EnumerateRunes().Count() is >= 3 and <= 256;
}
