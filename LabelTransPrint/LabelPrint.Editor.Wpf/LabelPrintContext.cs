namespace LabelTransPrint;

public sealed class LabelPrintContext
{
    public string OriginalCode { get; set; } = string.Empty;
    public string NewCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ProjectCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string SideLabel { get; set; } = string.Empty;
    public string RevisionCode { get; set; } = string.Empty;
    public string HardwareVersion { get; set; } = string.Empty;
    public string SoftwareVersion { get; set; } = string.Empty;
    public string DrawingVersion { get; set; } = string.Empty;
    public string VehicleModelCode { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string ShiftCode { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string BackupField1 { get; set; } = string.Empty;
    public string BackupField2 { get; set; } = string.Empty;
    public string SequenceCode { get; set; } = string.Empty;
    public DateTime ProductionDate { get; set; } = DateTime.Today;
    public string OperatorName { get; set; } = string.Empty;
    public LabelTransPrint.Services.LabelFieldValues? FieldValues { get; set; }
    public IReadOnlyList<LabelTransPrint.Services.LabelTemplateField>? Fields { get; set; }

    public LabelTransPrint.Services.ProductionLabelContext ToProductionContext()
        => LabelTransPrint.Services.ProductionLabelContext.Create(OriginalCode, NewCode, Title, ProjectCode,
            ProductName, SideLabel, PartNumber, RevisionCode, HardwareVersion, SoftwareVersion, DrawingVersion,
            VehicleModelCode, SupplierCode, SequenceCode, ShiftCode, ProductionDate);
}
