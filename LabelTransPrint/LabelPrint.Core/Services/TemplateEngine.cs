using System.Globalization;

namespace LabelTransPrint.Services;

public static class TemplateEngine
{
    public static string Resolve(
        string? template,
        string originalCode,
        string newCode,
        string title,
        string projectCode = "",
        string productName = "",
        string sideLabel = "",
        string partNumber = "",
        string revisionCode = "",
        string hardwareVersion = "",
        string softwareVersion = "",
        string drawingVersion = "",
        string vehicleModelCode = "",
        string supplierCode = "",
        string sequenceCode = "",
        string shiftCode = "",
        DateTime? productionDate = null)
    {
        var result = template ?? string.Empty;
        result = result.Replace("{{OriginalCode}}", originalCode ?? string.Empty);
        result = result.Replace("{{NewCode}}", newCode ?? string.Empty);
        result = result.Replace("{{Title}}", title ?? string.Empty);
        result = result.Replace("{{ProjectCode}}", projectCode ?? string.Empty);
        result = result.Replace("{{RecipeName}}", projectCode ?? string.Empty);
        result = result.Replace("{{ProductName}}", productName ?? string.Empty);
        result = result.Replace("{{SideLabel}}", sideLabel ?? string.Empty);
        result = result.Replace("{{PartNumber}}", partNumber ?? string.Empty);
        result = result.Replace("{{RevisionCode}}", revisionCode ?? string.Empty);
        result = result.Replace("{{PartRevision}}", revisionCode ?? string.Empty);
        result = result.Replace("{{HardwareVersion}}", hardwareVersion ?? string.Empty);
        result = result.Replace("{{SoftwareVersion}}", softwareVersion ?? string.Empty);
        result = result.Replace("{{DrawingVersion}}", drawingVersion ?? string.Empty);
        result = result.Replace("{{VehicleModelCode}}", vehicleModelCode ?? string.Empty);
        result = result.Replace("{{SupplierCode}}", supplierCode ?? string.Empty);
        var actualProductionDate = productionDate ?? DateTime.Now;
        var sequenceCode4 = FormatSequenceCode(sequenceCode, 4);
        var sequenceCode5 = FormatSequenceCode(sequenceCode, 5);
        result = result.Replace("{{SequenceCode}}", sequenceCode4);
        result = result.Replace("{{SequenceCode5}}", sequenceCode5);
        result = result.Replace("{{ShiftCode}}", shiftCode ?? string.Empty);
        result = result.Replace("{{BatchNumber}}", actualProductionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + sequenceCode5);
        result = result.Replace("{{ProductionDate}}", actualProductionDate.ToString("yyMMdd", CultureInfo.InvariantCulture));
        result = result.Replace("{{ProductionDateYYYYMMDD}}", actualProductionDate.ToString("yyyy/M/d", CultureInfo.InvariantCulture));
        result = result.Replace("{{ProductionDateCompact}}", actualProductionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        result = result.Replace("{{ProductionDateLabel}}", $"(生产日期){actualProductionDate:yyMMdd}");
        result = result.Replace("{{PartNumberLine}}", AlignLeftRight(partNumber, revisionCode, 16));
        result = result.Replace("{{SequenceFixedLine}}", AlignLeftRight(sequenceCode4, ResolveSequenceSuffix(revisionCode), 16));

        while (true)
        {
            int start = result.IndexOf("{{Now:", StringComparison.Ordinal);
            if (start < 0) break;

            int end = result.IndexOf("}}", start, StringComparison.Ordinal);
            if (end < 0) break;

            var fmt = result.Substring(start + 6, end - start - 6);
            var dt = DateTime.Now.ToString(fmt, CultureInfo.InvariantCulture);
            result = result[..start] + dt + result[(end + 2)..];
        }

        return result;
    }

    private static string FormatSequenceCode(string? rawValue, int width)
    {
        var cleaned = new string((rawValue ?? string.Empty)
            .Trim()
            .Where(char.IsDigit)
            .ToArray());

        if (cleaned.Length == 0)
            return new string('0', width - 1) + "1";

        if (int.TryParse(cleaned, out var sequence))
            return Math.Max(sequence, 1).ToString($"D{width}");

        return cleaned.PadLeft(width, '0')[^width..];
    }

    private static string AlignLeftRight(string? left, string? right, int minWidth)
    {
        var leftText = (left ?? string.Empty).Trim();
        var rightText = (right ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(leftText))
            return rightText;
        if (string.IsNullOrEmpty(rightText))
            return leftText;

        var totalWidth = Math.Max(minWidth, leftText.Length + 1 + rightText.Length);
        return leftText.PadRight(totalWidth - rightText.Length) + rightText;
    }

    private static string ResolveSequenceSuffix(string? colorCode)
    {
        var normalized = (colorCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 2 || !char.IsLetter(normalized[0]) || !char.IsLetter(normalized[1]))
            return "S00";

        var versionNumber = (normalized[0] - 'A') * 26 + (normalized[1] - 'A') + 1;
        if (versionNumber > 99)
            throw new InvalidOperationException("零件版本错误：二维码字段转换后超过99");

        return $"S{versionNumber:D2}";
    }
}
