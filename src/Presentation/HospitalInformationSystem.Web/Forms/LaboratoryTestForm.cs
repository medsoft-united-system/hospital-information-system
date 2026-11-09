using System.ComponentModel.DataAnnotations;
using HospitalInformationSystem.Web.Validation;

namespace HospitalInformationSystem.Web.Forms;

public sealed class LaboratoryTestForm
{
    [Required(ErrorMessage = "Укажите код LOINC.")]
    [LaboratoryTestCode]
    public string LoincCode { get; set; } = "";

    [Required(ErrorMessage = "Укажите наименование анализа.")]
    [LaboratoryTestName]
    public string DisplayName { get; set; } = "";
}
