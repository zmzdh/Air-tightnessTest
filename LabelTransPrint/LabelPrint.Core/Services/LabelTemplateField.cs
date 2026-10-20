namespace LabelTransPrint.Services;

public sealed record LabelTemplateField(
    string Token,
    string DisplayName,
    string Category,
    bool IsQrRuleVisible = true);
