using System.Globalization;

namespace LabelTransPrint.Services;

/// <summary>Common production fields used by the existing inspection applications.</summary>
public sealed class ProductionLabelContext
{
    public static IReadOnlyList<LabelTemplateField> Fields { get; } =
    [
        new("{{ProjectCode}}", "配方名称", "配方字段"),
        new("{{ProductName}}", "零件名称", "配方字段"),
        new("{{SideLabel}}", "安装位置", "配方字段"),
        new("{{RevisionCode}}", "零件版本", "配方字段"),
        new("{{HardwareVersion}}", "硬件版本", "配方字段"),
        new("{{SoftwareVersion}}", "软件版本", "配方字段"),
        new("{{DrawingVersion}}", "图纸版本", "配方字段"),
        new("{{VehicleModelCode}}", "车型代码", "配方字段"),
        new("{{SupplierCode}}", "供应商码", "配方字段"),
        new("{{ShiftCode}}", "班次", "生产字段"),
        new("{{OriginalCode}}", "原始扫码内容", "扫码字段"),
        new("{{NewCode}}", "生成二维码内容", "扫码字段"),
        new("{{BatchNumber}}", "批次号", "生产字段"),
        new("{{PartNumberLine}}", "零件号+零件版本行", "组合字段"),
        new("{{SequenceFixedLine}}", "序列号+零件版本行", "组合字段")
    ];
    public string OriginalCode { get; set; } = "";
    public string NewCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProjectCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SideLabel { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string RevisionCode { get; set; } = "";
    public string HardwareVersion { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string DrawingVersion { get; set; } = "";
    public string VehicleModelCode { get; set; } = "";
    public string SupplierCode { get; set; } = "";
    public string SequenceCode { get; set; } = "";
    public string ShiftCode { get; set; } = "";
    public DateTime ProductionDate { get; set; } = DateTime.Today;

    public static ProductionLabelContext Create(string originalCode, string newCode, string title,
        string projectCode, string productName, string sideLabel, string partNumber, string revisionCode,
        string hardwareVersion, string softwareVersion, string drawingVersion, string vehicleModelCode,
        string supplierCode, string sequenceCode, string shiftCode, DateTime productionDate)
        => new()
        {
            OriginalCode = originalCode, NewCode = newCode, Title = title, ProjectCode = projectCode,
            ProductName = productName, SideLabel = sideLabel, PartNumber = partNumber, RevisionCode = revisionCode,
            HardwareVersion = hardwareVersion, SoftwareVersion = softwareVersion, DrawingVersion = drawingVersion,
            VehicleModelCode = vehicleModelCode, SupplierCode = supplierCode, SequenceCode = sequenceCode,
            ShiftCode = shiftCode, ProductionDate = productionDate
        };

    public LabelFieldValues CreateFields()
    {
        var fields = new LabelFieldValues();
        void Set(string name, string? value) => fields.Set("{{" + name + "}}", value);
        Set("OriginalCode", OriginalCode);
        Set("NewCode", NewCode);
        Set("Title", Title);
        Set("ProjectCode", ProjectCode);
        Set("RecipeName", ProjectCode);
        Set("ProductName", ProductName);
        Set("SideLabel", SideLabel);
        Set("PartNumber", PartNumber);
        Set("RevisionCode", RevisionCode);
        Set("PartRevision", RevisionCode);
        Set("HardwareVersion", HardwareVersion);
        Set("SoftwareVersion", SoftwareVersion);
        Set("DrawingVersion", DrawingVersion);
        Set("VehicleModelCode", VehicleModelCode);
        Set("SupplierCode", SupplierCode);
        Set("SequenceCode", SequenceCode);
        Set("SequenceCode4", SequenceCode);
        Set("SequenceCode5", SequenceCode);
        Set("ShiftCode", ShiftCode);
        Set("ProductionDate", ProductionDate.ToString("yyMMdd", CultureInfo.InvariantCulture));
        Set("ProductionDateCompact", ProductionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Set("ProductionDateDash", ProductionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Set("ProductionDateShortDash", ProductionDate.ToString("yy-MM-dd", CultureInfo.InvariantCulture));
        Set("ProductionDateShortSlash", ProductionDate.ToString("yy/MM/dd", CultureInfo.InvariantCulture));
        Set("ProductionDateYYYYMMDD", ProductionDate.ToString("yyyy/M/d", CultureInfo.InvariantCulture));
        Set("ProductionDateLabel", "(生产日期)" + ProductionDate.ToString("yyMMdd", CultureInfo.InvariantCulture));
        Set("BatchNumber", ProductionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (SequenceCode ?? "").PadLeft(5, '0'));
        Set("PartNumberLine", AlignLeftRight(PartNumber, RevisionCode));
        var revision = (RevisionCode ?? "").Trim().ToUpperInvariant();
        if (revision.Length != 2 || revision[0] is < 'A' or > 'Z' || revision[1] is < 'A' or > 'Z')
            Set("SequenceFixedLine", AlignLeftRight(SequenceCode, "S00"));
        else
        {
            var version = (revision[0] - 'A') * 26 + revision[1] - 'A' + 1;
            if (version <= 99)
                Set("SequenceFixedLine", AlignLeftRight(SequenceCode, $"S{version:D2}"));
        }
        return fields;
    }

    private static string AlignLeftRight(string? left, string? right)
    {
        var first = (left ?? "").Trim();
        var second = (right ?? "").Trim();
        if (first.Length == 0) return second;
        if (second.Length == 0) return first;
        return first.PadRight(Math.Max(16, first.Length + second.Length + 1) - second.Length) + second;
    }
}
