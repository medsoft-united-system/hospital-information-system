using System.ComponentModel.DataAnnotations;
using HospitalInformationSystem.Contracts.ServiceAccounts;

namespace HospitalInformationSystem.Web.Forms;

public sealed class ServiceAccountForm : IValidatableObject
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = ServiceAccountRoles.Lis;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!ServiceAccountNameRules.IsValid(Name))
            yield return new ValidationResult("Введите имя от 3 до 64 символов без управляющих символов.", [nameof(Name)]);
        if (!ServiceAccountRoles.IsSupported(Role))
            yield return new ValidationResult("Выберите роль LIS или HL7 Import.", [nameof(Role)]);
    }
}
