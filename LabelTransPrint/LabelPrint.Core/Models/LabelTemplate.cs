namespace LabelTransPrint.Models;

public class LabelTemplate
{
    public int Version { get; set; } = 1;
    public string TemplateCode { get; set; } = "Default";
    public string DisplayName { get; set; } = "Default";
    public string QrPayloadMode { get; set; } = "Auto";
    public string QrPayloadExpression { get; set; } = "{{NewCode}}";
    public double LabelWidthMm { get; set; } = 40;
    public double LabelHeightMm { get; set; } = 20;
    public string Prefix { get; set; } = "NEW-";
    public string Suffix { get; set; } = "";
    public string ProductTitle { get; set; } = "";
    public List<LabelElement> Elements { get; set; } = new();
}
