using LabelTransPrint.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace LabelTransPrint.Services;

public sealed class LabelTemplateService
{
    public const string SequenceCodeToken = "{{SequenceCode4}}";
    public const string LegacySequenceCodeToken = "{{SequenceCode}}";
    public const string SequenceCode5Token = "{{SequenceCode5}}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<LabelTemplateField> Fields { get; } =
    [
        new("{{PartNumber}}", "零件号", "打印字段"),
        new("{{BackupField1}}", "备用字段1", "打印字段"),
        new("{{BackupField2}}", "备用字段2", "打印字段"),
        new(SequenceCodeToken, "4位顺序号", "打印字段"),
        new(SequenceCode5Token, "5位顺序号", "打印字段"),
        new("{{ProductionDate}}", "生产日期(yyMMdd)", "打印字段"),
        new("{{ProductionDateCompact}}", "生产日期(yyyyMMdd)", "打印字段"),
        new("{{ProductionDateShortDash}}", "生产日期(yy-MM-dd)", "打印字段"),
        new("{{ProductionDateShortSlash}}", "生产日期(yy/MM/dd)", "打印字段"),
        new("{{ProductionDateDash}}", "生产日期(yyyy-MM-dd)", "打印字段"),
        new("{{ProductionDateYYYYMMDD}}", "生产日期(yyyy/M/d)", "打印字段"),
        new("{{OperatorName}}", "操作员", "打印字段")
    ];

    public string TemplateRoot { get; }

    public LabelTemplateService() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LabelTransPrint",
            "LabelTemplates"))
    {
    }

    public LabelTemplateService(string templateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateRoot);
        TemplateRoot = templateRoot;
    }

    public LabelTemplate? TryLoad(string? templateCode)
    {
        var path = GetTemplatePath(templateCode);
        if (!File.Exists(path))
            return null;

        var originalJson = File.ReadAllText(path);
        var import = LabelTemplateImport.Parse(originalJson);
        if (import.RequiresReview)
            throw new InvalidOperationException("检测到旧版加热垫模板，请先通过模板导入转换并检查字段与打印效果。");
        var template = import.Template;

        if (string.IsNullOrWhiteSpace(template.TemplateCode))
            template.TemplateCode = NormalizeTemplateCode(templateCode);
        if (string.IsNullOrWhiteSpace(template.DisplayName))
            template.DisplayName = template.TemplateCode;
        NormalizeTemplateFields(template);
        MigrateLegacyFieldInFile(path, originalJson);

        return template;
    }

    public void Save(LabelTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.TemplateCode))
            throw new InvalidOperationException("模板编码不能为空。");

        Directory.CreateDirectory(TemplateRoot);
        template.TemplateCode = NormalizeTemplateCode(template.TemplateCode);
        template.DisplayName = string.IsNullOrWhiteSpace(template.DisplayName) ? template.TemplateCode : template.DisplayName.Trim();
        NormalizeTemplateFields(template);
        if (HasConflictingSequenceCodeFields(template))
            throw new InvalidOperationException("同一模板不能同时使用4位顺序号和5位顺序号。");

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
        NormalizeTemplateFields(clone);
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

    public static void NormalizeTemplateFields(LabelTemplate template)
    {
        template.QrPayloadExpression = NormalizeFieldTokens(template.QrPayloadExpression);
        foreach (var element in template.Elements)
            element.ContentTemplate = NormalizeFieldTokens(element.ContentTemplate);
    }

    public static string NormalizeFieldTokens(string? value)
        => (value ?? string.Empty).Replace(LegacySequenceCodeToken, SequenceCodeToken, StringComparison.OrdinalIgnoreCase);

    private static void MigrateLegacyFieldInFile(string path, string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        var elements = root?.FirstOrDefault(x => x.Key.Equals("Elements", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if (root is null)
            return;

        var changed = false;
        void MigrateProperty(JsonObject obj, string name)
        {
            var property = obj.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var content)
                || !content.Contains(LegacySequenceCodeToken, StringComparison.OrdinalIgnoreCase))
                return;

            obj[property.Key] = NormalizeFieldTokens(content);
            changed = true;
        }
        MigrateProperty(root, "QrPayloadExpression");
        if (elements is not null)
            foreach (var element in elements.OfType<JsonObject>())
                MigrateProperty(element, "ContentTemplate");

        if (!changed)
            return;

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, root!.ToJsonString(JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static bool UsesField(IEnumerable<LabelElement> elements, string token)
        => elements.Any(element =>
            (element.Type is LabelElementType.Text or LabelElementType.QrCode or LabelElementType.Barcode)
            && (element.ContentTemplate?.Contains(token, StringComparison.OrdinalIgnoreCase) == true
                || (token.Equals(SequenceCodeToken, StringComparison.OrdinalIgnoreCase)
                    && element.ContentTemplate?.Contains(LegacySequenceCodeToken, StringComparison.OrdinalIgnoreCase) == true)));

    public static bool HasConflictingSequenceCodeFields(IEnumerable<LabelElement> elements)
    {
        var items = elements as IReadOnlyCollection<LabelElement> ?? elements.ToArray();
        return UsesField(items, SequenceCodeToken) && UsesField(items, SequenceCode5Token);
    }

    public static bool HasConflictingSequenceCodeFields(LabelTemplate template)
    {
        var items = template.Elements.ToList();
        if (string.Equals(template.QrPayloadMode, "Custom", StringComparison.OrdinalIgnoreCase))
            items.Add(new LabelElement { Type = LabelElementType.QrCode, ContentTemplate = template.QrPayloadExpression });
        return HasConflictingSequenceCodeFields(items);
    }

    public static string BuildQrPayload(LabelTemplate? template, string fallbackPayload, string originalCode,
        string title, string projectCode, string productName, string sideLabel, string partNumber,
        string revisionCode, string hardwareVersion, string softwareVersion, string drawingVersion,
        string vehicleModelCode, string supplierCode, string sequenceCode, string shiftCode, DateTime productionDate)
    {
        if (template is null || !string.Equals(template.QrPayloadMode, "Custom", StringComparison.OrdinalIgnoreCase))
            return fallbackPayload;
        var context = ProductionLabelContext.Create(originalCode, fallbackPayload, title, projectCode, productName,
            sideLabel, partNumber, revisionCode, hardwareVersion, softwareVersion, drawingVersion, vehicleModelCode,
            supplierCode, sequenceCode, shiftCode, productionDate);
        var fields = context.CreateFields();
        var expression = string.IsNullOrWhiteSpace(template.QrPayloadExpression) ? "{{NewCode}}" : template.QrPayloadExpression;
        var missing = fields.FindMissing(expression);
        if (missing.Count > 0)
            throw new InvalidOperationException($"缺少二维码字段：{string.Join("、", missing)}");
        return fields.Resolve(expression);
    }

}
