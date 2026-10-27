namespace LabelTransPrint.Services;

public sealed class PrinterConfiguration
{
    public double OffsetXmm { get; set; }
    public double OffsetYmm { get; set; }
    public double TextScale { get; set; } = 0.85;
    public double ImageScale { get; set; } = 1.18;
    public int DpiOverride { get; set; }

    public PrinterConfiguration Normalize()
    {
        TextScale = TextScale > 0 ? TextScale : 0.85;
        ImageScale = ImageScale > 0 ? ImageScale : 1.18;
        DpiOverride = DpiOverride is 203 or 300 ? DpiOverride : 0;
        return this;
    }
}
