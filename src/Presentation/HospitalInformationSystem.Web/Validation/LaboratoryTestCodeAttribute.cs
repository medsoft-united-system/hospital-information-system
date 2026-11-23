using System.ComponentModel.DataAnnotations;
using HospitalInformationSystem.Models.LaboratoryTests;

namespace HospitalInformationSystem.Web.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class LaboratoryTestCodeAttribute : ValidationAttribute
{
    public LaboratoryTestCodeAttribute()
        : base("Укажите числовой код LOINC длиной от 3 до 32 символов с правильной контрольной цифрой.") { }

    public override bool IsValid(object? value) => value is null ||
        value is string text && LaboratoryTestCodeRules.IsValidCode(text);
}
