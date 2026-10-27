using CommunityToolkit.Mvvm.ComponentModel;

namespace LabelTransPrint.Models;

public partial class LabelElement : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private LabelElementType type = LabelElementType.Text;
    [ObservableProperty] private double x;
    [ObservableProperty] private double y;
    [ObservableProperty] private double width;
    [ObservableProperty] private double height;
    [ObservableProperty] private double fontSize = 3;
    [ObservableProperty] private string fontFamily = "Microsoft YaHei UI";
    [ObservableProperty] private bool isBold;
    [ObservableProperty] private string horizontalAlignment = "Left";
    [ObservableProperty] private string? qrErrorCorrection;
    [ObservableProperty] private int? qrMargin;
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; set; }
    [ObservableProperty] private string contentTemplate = string.Empty;
    [ObservableProperty] private string imagePath = string.Empty;
    [ObservableProperty] private double lineThickness = 0.2;
    [ObservableProperty] private string lineStyle = "Solid";
    [ObservableProperty] private int rowCount = 2;
    [ObservableProperty] private int columnCount = 2;
    [ObservableProperty] private List<double> rowHeights = new();
    [ObservableProperty] private List<double> columnWidths = new();
    [ObservableProperty] private List<double> tableRowLines = new();
    [ObservableProperty] private List<double> tableColumnLines = new();
    [ObservableProperty] private List<string> hiddenTableRowSegments = new();
    [ObservableProperty] private List<string> hiddenTableColumnSegments = new();
}
