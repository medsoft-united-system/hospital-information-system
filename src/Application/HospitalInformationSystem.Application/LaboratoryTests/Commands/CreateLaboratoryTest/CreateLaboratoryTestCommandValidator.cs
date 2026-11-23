using FluentValidation;
using HospitalInformationSystem.Application.Models.LaboratoryTests;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.CreateLaboratoryTest;

public sealed class CreateLaboratoryTestCommandValidator : AbstractValidator<CreateLaboratoryTestCommand>
{
    public CreateLaboratoryTestCommandValidator()
    {
        RuleFor(x => x.LoincCode).Must(LaboratoryTestDefinitionRules.IsValidCode)
            .WithMessage("Код LOINC: от 3 до 32 символов, цифры и контрольная цифра через дефис.");
        RuleFor(x => x.DisplayName).Must(LaboratoryTestDefinitionRules.IsValidDisplayName)
            .WithMessage("Название анализа: требуется от 3 до 256 символов без нулевого символа.");
    }
}
