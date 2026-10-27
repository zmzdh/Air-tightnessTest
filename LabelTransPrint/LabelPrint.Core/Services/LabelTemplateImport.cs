using System.Text.Json;
using System.Text.Json.Nodes;
using LabelTransPrint.Models;

namespace LabelTransPrint.Services;

/// <summary>Imports supported template JSON without modifying its source file.</summary>
public static class LabelTemplateImport
{
    /// <summary>Creates the legacy heating-pad representation for an adapter, without changing the template.</summary>
    public static string ToHeatingJson(LabelTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var root = JsonSerializer.SerializeToNode(template, LabelTemplateService.JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("模板无法序列化。");
        var items = Find(root, "Elements") as JsonArray
            ?? throw new InvalidOperationException("模板缺少元素。");
        foreach (var node in items)
        {
            if (node is not JsonObject item) throw new InvalidOperationException("模板元素必须是对象。");
            Rename(item, "Type", "ElementType");
            Rename(item, "X", "XMm");
            Rename(item, "Y", "YMm");
            Rename(item, "Width", "WidthMm");
            Rename(item, "Height", "HeightMm");
            Rename(item, "ContentTemplate", "Content");
            var key = Key(item, "FontSize");
            if (key is not null && item[key] is JsonValue size && size.TryGetValue<double>(out var mm))
                item[key] = mm * 72 / 25.4;
        }
        Rename(root, "LabelWidthMm", "PaperWidthMm");
        Rename(root, "LabelHeightMm", "PaperHeightMm");
        Rename(root, "Elements", "Items");
        return root.ToJsonString(LabelTemplateService.JsonOptions);
    }

    public static LabelTemplateImportResult Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("模板 JSON 必须是对象。");
        var elements = Find(root, "Elements");
        var items = Find(root, "Items");
        if (elements is not null && items is not null)
            throw new InvalidOperationException("模板同时包含 Elements 和 Items，无法确定格式。");

        var warnings = new List<string>();
        if (items is not null)
        {
            if (items is not JsonArray sourceItems)
                throw new InvalidOperationException("模板 Items 必须是数组。");
            Rename(root, "PaperWidthMm", "LabelWidthMm");
            Rename(root, "PaperHeightMm", "LabelHeightMm");
            foreach (var node in sourceItems)
            {
                if (node is not JsonObject item)
                    throw new InvalidOperationException("模板元素必须是对象。");
                Rename(item, "ElementType", "Type");
                if (Find(item, "Type") is JsonValue type && type.TryGetValue<string>(out var typeName)
                    && typeName.Equals("QrCode", StringComparison.OrdinalIgnoreCase))
                {
                    if (Key(item, "QrErrorCorrection") is null) item["QrErrorCorrection"] = "Q";
                    if (Key(item, "QrMargin") is null) item["QrMargin"] = 0;
                }
                Rename(item, "XMm", "X");
                Rename(item, "YMm", "Y");
                Rename(item, "WidthMm", "Width");
                Rename(item, "HeightMm", "Height");
                Rename(item, "Content", "ContentTemplate");
                var sizeKey = Key(item, "FontSize");
                if (sizeKey is not null && item[sizeKey] is JsonValue size && size.TryGetValue<double>(out var points))
                    item[sizeKey] = points * 25.4 / 72.0;
            }
            Rename(root, "Items", "Elements");
            warnings.Add("已转换加热垫模板的尺寸和字号。业务字段、二维码规则和图片路径需由目标项目提供；打印前请检查预览及实际标签。");
        }
        else if (elements is not JsonArray)
        {
            throw new InvalidOperationException("模板缺少有效的 Elements 或 Items 数组。");
        }

        var template = root.Deserialize<LabelTemplate>(LabelTemplateService.JsonOptions)
            ?? throw new InvalidOperationException("模板 JSON 无效。");
        LabelTemplateService.NormalizeTemplateFields(template);
        if (LabelTemplateService.HasConflictingSequenceCodeFields(template))
            warnings.Add("模板同时使用4位和5位顺序号，请修改为仅使用一种后保存或打印。");
        return new LabelTemplateImportResult(template, warnings, items is not null);
    }

    private static string? Key(JsonObject value, string name)
        => value.Select(x => x.Key).FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Find(JsonObject value, string name)
        => Key(value, name) is { } key ? value[key] : null;

    private static void Rename(JsonObject value, string source, string target)
    {
        var sourceKey = Key(value, source);
        if (sourceKey is null)
            return;
        if (Key(value, target) is not null)
            throw new InvalidOperationException($"模板同时包含 {source} 和 {target}，无法转换。");
        var node = value[sourceKey];
        value.Remove(sourceKey);
        value[target] = node;
    }
}

public sealed record LabelTemplateImportResult(LabelTemplate Template, IReadOnlyList<string> Warnings, bool RequiresReview);
