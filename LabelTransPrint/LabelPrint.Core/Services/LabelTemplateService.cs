using LabelTransPrint.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelTransPrint.Services;

public sealed class LabelTemplateService
{
    private static readonly IReadOnlyDictionary<string, string> LegacyTokenMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["{{RecipeName}}"] = "{{ProjectCode}}",
        ["{{PartRevision}}"] = "{{RevisionCode}}"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<LabelTemplateField> Fields { get; } =
    [
        new("{{ProjectCode}}", "配方名称", "配方字段"),
        new("{{ProductName}}", "零件名称", "配方字段"),
        new("{{PartNumber}}", "零件号", "配方字段"),
        new("{{RevisionCode}}", "零件版本", "配方字段"),
        new("{{HardwareVersion}}", "硬件版本", "配方字段"),
        new("{{SoftwareVersion}}", "软件版本", "配方字段"),
        new("{{DrawingVersion}}", "图纸版本", "配方字段"),
        new("{{VehicleModelCode}}", "车型代码", "配方字段"),
        new("{{SupplierCode}}", "供应商码", "配方字段"),
        new("{{SideLabel}}", "安装位置", "配方字段"),
        new("{{SequenceCode}}", "序列号", "生产字段"),
        new("{{SequenceCode5}}", "序列号（5位）", "生产字段"),
        new("{{ShiftCode}}", "班次", "生产字段"),
        new("{{ProductionDate}}", "生产日期(yyMMdd)", "生产字段"),
        new("{{ProductionDateCompact}}", "生产日期(yyyyMMdd)", "生产字段"),
        new("{{ProductionDateYYYYMMDD}}", "生产日期(yyyy/M/d)", "生产字段"),
        new("{{BatchNumber}}", "批次号(yyyyMMdd+5位序列号)", "生产字段"),
        new("{{OriginalCode}}", "原始扫码内容", "扫码/输出字段"),
        new("{{NewCode}}", "生成二维码内容", "扫码/输出字段", false),
        new("{{Title}}", "产品标题", "模板字段"),
        new("{{PartNumberLine}}", "零件号+零件版本行", "组合字段", false),
        new("{{SequenceFixedLine}}", "序列号+零件版本行", "组合字段", false)
    ];

    public IReadOnlyList<LabelTemplateField> QrRuleFields => Fields.Where(x => x.IsQrRuleVisible).ToArray();

    public string TemplateRoot { get; }

    public LabelTemplateService()
    {
        TemplateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LabelTransPrint",
            "LabelTemplates");
    }

    public LabelTemplate? TryLoad(string? templateCode)
    {
        var path = GetTemplatePath(templateCode);
        if (!File.Exists(path))
            return null;

        var template = JsonSerializer.Deserialize<LabelTemplate>(File.ReadAllText(path), JsonOptions);
        if (template is null)
            throw new InvalidOperationException($"打印模板文件无效：{path}");

        if (string.IsNullOrWhiteSpace(template.TemplateCode))
            template.TemplateCode = NormalizeTemplateCode(templateCode);
        if (string.IsNullOrWhiteSpace(template.DisplayName))
            template.DisplayName = template.TemplateCode;
        if (string.IsNullOrWhiteSpace(template.QrPayloadExpression))
            template.QrPayloadExpression = "{{NewCode}}";
        MigrateLegacyFields(template);

        return template;
    }

    public void Save(LabelTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.TemplateCode))
            throw new InvalidOperationException("模板编码不能为空。");

        Directory.CreateDirectory(TemplateRoot);
        template.TemplateCode = NormalizeTemplateCode(template.TemplateCode);
        template.DisplayName = string.IsNullOrWhiteSpace(template.DisplayName) ? template.TemplateCode : template.DisplayName.Trim();
        MigrateLegacyFields(template);
        var json = JsonSerializer.Serialize(template, JsonOptions);
        File.WriteAllText(GetTemplatePath(template.TemplateCode), json);
    }

    public IReadOnlyList<string> GetTemplateCodes()
    {
        Directory.CreateDirectory(TemplateRoot);
        return Directory.GetFiles(TemplateRoot, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Delete(string templateCode)
    {
        var path = GetTemplatePath(templateCode);
        if (!File.Exists(path))
            throw new InvalidOperationException($"打印模板不存在：{templateCode}");

        File.Delete(path);
    }

    public LabelTemplate Clone(LabelTemplate template)
    {
        var json = JsonSerializer.Serialize(template, JsonOptions);
        var clone = JsonSerializer.Deserialize<LabelTemplate>(json, JsonOptions) ?? new LabelTemplate();
        MigrateLegacyFields(clone);
        return clone;
    }

    public string GetTemplatePath(string? templateCode)
    {
        var safeName = NormalizeTemplateCode(templateCode);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalid, '_');

        return Path.Combine(TemplateRoot, $"{safeName}.json");
    }

    public static string NormalizeTemplateCode(string? templateCode)
        => string.IsNullOrWhiteSpace(templateCode) ? "Default" : templateCode.Trim();

    public static void MigrateLegacyFields(LabelTemplate template)
    {
        template.QrPayloadExpression = NormalizeFieldTokens(template.QrPayloadExpression);
        foreach (var element in template.Elements)
        {
            element.ContentTemplate = NormalizeFieldTokens(element.ContentTemplate);
        }
    }

    public static string NormalizeFieldTokens(string? value)
    {
        var result = value ?? string.Empty;
        foreach (var legacy in LegacyTokenMap)
            result = result.Replace(legacy.Key, legacy.Value, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    public static string BuildQrPayload(
        LabelTemplate? template,
        string fallbackPayload,
        string originalCode,
        string title,
        string projectCode,
        string productName,
        string sideLabel,
        string partNumber,
        string revisionCode,
        string hardwareVersion,
        string softwareVersion,
        string drawingVersion,
        string vehicleModelCode,
        string supplierCode,
        string sequenceCode,
        string shiftCode,
        DateTime productionDate)
    {
        if (template is null
            || !string.Equals(template.QrPayloadMode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackPayload;
        }

        var expression = string.IsNullOrWhiteSpace(template.QrPayloadExpression)
            ? "{{NewCode}}"
            : template.QrPayloadExpression;

        return TemplateEngine.Resolve(
            expression.Replace("{{NewCode}}", fallbackPayload, StringComparison.OrdinalIgnoreCase),
            originalCode,
            fallbackPayload,
            title,
            projectCode,
            productName,
            sideLabel,
            partNumber,
            revisionCode,
            hardwareVersion,
            softwareVersion,
            drawingVersion,
            vehicleModelCode,
            supplierCode,
            sequenceCode,
            shiftCode,
            productionDate);
    }
}
