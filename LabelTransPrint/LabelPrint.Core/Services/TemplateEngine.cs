using System.Globalization;

namespace LabelTransPrint.Services;

public static class TemplateEngine
{
    public static string Resolve(
        string? template,
        string title,
        string partNumber,
        string backupField1,
        string backupField2,
        string sequenceCode,
        DateTime? productionDate,
        string operatorName)
    {
        var result = template ?? string.Empty;
        var actualProductionDate = productionDate ?? DateTime.Now;
        var sequenceCode5 = sequenceCode ?? string.Empty;

        result = result.Replace("{{Title}}", title ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{PartNumber}}", partNumber ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{BackupField1}}", backupField1 ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{BackupField2}}", backupField2 ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{SequenceCode}}", sequenceCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{SequenceCode4}}", sequenceCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{SequenceCode5}}", sequenceCode5, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{ProductionDate}}", actualProductionDate.ToString("yyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{ProductionDateCompact}}", actualProductionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{ProductionDateShortDash}}", actualProductionDate.ToString("yy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{ProductionDateShortSlash}}", actualProductionDate.ToString("yy/MM/dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{ProductionDateDash}}", actualProductionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{ProductionDateYYYYMMDD}}", actualProductionDate.ToString("yyyy/M/d", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{{OperatorName}}", operatorName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

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

    public static string FormatSequenceCode5(string? rawValue)
        => FormatSequenceCode(rawValue, 5);

    private static string FormatSequenceCode(string? rawValue, int width)
    {
        var cleaned = new string((rawValue ?? string.Empty)
            .Trim()
            .Where(char.IsDigit)
            .ToArray());

        if (cleaned.Length == 0)
            return new string('0', width - 1) + "1";

        if (long.TryParse(cleaned, out var sequence))
            return Math.Max(sequence, 1).ToString($"D{width}");

        return cleaned.PadLeft(width, '0')[^width..];
    }
}
