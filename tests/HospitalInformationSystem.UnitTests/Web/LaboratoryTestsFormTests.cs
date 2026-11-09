using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using HospitalInformationSystem.Web.Forms;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryTestsFormTests
{
    [Theory]
    [InlineData("1-2", true)]
    [InlineData(" 2345-7 ", true)]
    [InlineData("2345-77", false)]
    [InlineData("۲۳۴۵-۷", false)]
    [InlineData("2345‐7", false)]
    [InlineData("2345-7\0", false)]
    [InlineData("", false)]
    public void Codes_use_ascii_digits_and_a_single_trailing_digit(string code, bool valid) =>
        IsValid(new LaboratoryTestForm { LoincCode = code, DisplayName = "Глюкоза" }).Should().Be(valid);

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Code_length_includes_hyphen_and_final_digit(int prefixLength, bool valid) =>
        IsValid(new LaboratoryTestForm { LoincCode = new string('1', prefixLength) + "-7", DisplayName = "Глюкоза" })
            .Should().Be(valid);

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Name_length_counts_unicode_scalars_not_utf16_units(int length, bool valid) =>
        IsValid(new LaboratoryTestForm { LoincCode = "2345-7", DisplayName = " " + string.Concat(Enumerable.Repeat("🧪", length)) + " " })
            .Should().Be(valid);

    [Fact]
    public void Names_reject_null_character_and_whitespace_only()
    {
        IsValid(new LaboratoryTestForm { LoincCode = "2345-7", DisplayName = "Глю\0коза" }).Should().BeFalse();
        IsValid(new LaboratoryTestForm { LoincCode = "2345-7", DisplayName = "    " }).Should().BeFalse();
    }

    private static bool IsValid(LaboratoryTestForm form) =>
        Validator.TryValidateObject(form, new ValidationContext(form), [], validateAllProperties: true);
}
