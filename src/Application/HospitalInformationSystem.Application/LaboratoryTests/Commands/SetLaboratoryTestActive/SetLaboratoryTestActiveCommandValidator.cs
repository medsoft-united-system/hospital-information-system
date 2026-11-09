using FluentValidation;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.SetLaboratoryTestActive;

public sealed class SetLaboratoryTestActiveCommandValidator : AbstractValidator<SetLaboratoryTestActiveCommand>
{
    public SetLaboratoryTestActiveCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
