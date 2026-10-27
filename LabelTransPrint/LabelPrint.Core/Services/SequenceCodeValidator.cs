using LabelTransPrint.Models;

namespace LabelTransPrint.Services;

/// <summary>Validates sequence numbers against the fields used by a label template.</summary>
public static class SequenceCodeValidator
{
    public static void ValidateTemplate(LabelTemplate template, string? sequenceCode, bool requireSequenceCode = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        var items = template.Elements.ToList();
        if (string.Equals(template.QrPayloadMode, "Custom", StringComparison.OrdinalIgnoreCase))
            items.Add(new LabelElement { Type = LabelElementType.QrCode, ContentTemplate = template.QrPayloadExpression });
        Validate(items, sequenceCode, requireSequenceCode);
    }

    public static void Validate(IEnumerable<LabelElement> elements, string? sequenceCode, bool requireSequenceCode = false)
    {
        var items = elements as IReadOnlyCollection<LabelElement> ?? elements.ToArray();
        var uses4 = LabelTemplateService.UsesField(items, LabelTemplateService.SequenceCodeToken);
        var uses5 = LabelTemplateService.UsesField(items, LabelTemplateService.SequenceCode5Token);
        if (uses4 && uses5)
            throw new InvalidOperationException("同一模板不能同时使用4位顺序号和5位顺序号。");

        var width = uses4 ? 4 : uses5 ? 5 : 0;
        if (width == 0 && !requireSequenceCode)
            return;

        if (string.IsNullOrEmpty(sequenceCode) || sequenceCode.Any(c => c < '0' || c > '9')
            || (width > 0 ? sequenceCode.Length != width : sequenceCode.Length > 10))
        {
            throw new InvalidOperationException(width > 0
                ? $"当前模板要求输入{width}位数字顺序号，且批量打印的所有顺序号都必须为{width}位。"
                : "请输入1至10位数字顺序号。");
        }
    }
}
