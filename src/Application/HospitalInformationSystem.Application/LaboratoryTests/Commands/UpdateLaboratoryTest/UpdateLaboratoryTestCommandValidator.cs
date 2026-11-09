using FluentValidation;
using HospitalInformationSystem.Domain.LaboratoryTests;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.UpdateLaboratoryTest;

public sealed class UpdateLaboratoryTestCommandValidator : AbstractValidator<UpdateLaboratoryTestCommand>
{
    public UpdateLaboratoryTestCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.LoincCode).Must(LaboratoryTestDefinitionRules.IsValidCode)
            .WithMessage("Код LOINC: от 3 до 32 символов, цифры и контрольная цифра через дефис.");
        RuleFor(x => x.DisplayName).Must(LaboratoryTestDefinitionRules.IsValidDisplayName)
            .WithMessage("Название анализа: требуется от 3 до 256 символов без нулевого символа.");
    }
}
