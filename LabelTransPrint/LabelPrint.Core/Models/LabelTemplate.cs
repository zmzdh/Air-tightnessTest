namespace LabelTransPrint.Models;

public class LabelTemplate
{
    public int Version { get; set; } = 1;
    public string TemplateCode { get; set; } = "Default";
    public string DisplayName { get; set; } = "Default";
    public double LabelWidthMm { get; set; } = 80;
    public double LabelHeightMm { get; set; } = 20;
    public string ProductTitle { get; set; } = "";
    public string QrPayloadMode { get; set; } = "Auto";
    public string QrPayloadExpression { get; set; } = "";
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; set; }
    public List<LabelElement> Elements { get; set; } = new();
}
