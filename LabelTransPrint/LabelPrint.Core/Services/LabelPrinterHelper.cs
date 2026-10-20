using LabelTransPrint.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.IO;

namespace LabelTransPrint.Services;

/// <summary>
/// 已可稳定落地的打印类：基于 Bitmap 生成标签，再通过 Windows 打印机驱动输出。
/// 适合 Argox CP-3140L Pro PPLB，也便于做模板化与可视编辑。
/// </summary>
public class LabelPrinterHelper
{
    private readonly PrintDocument _printDocument;
    private Bitmap? _preparedBitmap;

    private const float Dpi = 300f;
    private const float MmToInch = 25.4f;
    private const float LogoImageScale = 1.18f;
    private static readonly HashSet<string> InstalledFontNames = new InstalledFontCollection()
        .Families
        .Select(f => f.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string PrinterName { get; set; }
    public double LabelWidthMm { get; set; } = 40;
    public double LabelHeightMm { get; set; } = 20;
    public double TextScale { get; set; } = 1.0;

    public LabelPrinterHelper(string printerName)
    {
        PrinterName = printerName;
        _printDocument = new PrintDocument();
        _printDocument.OriginAtMargins = false;
        _printDocument.PrintPage += PrintDocument_PrintPage;
    }

    public void Print(
        IEnumerable<LabelElement> elements,
        string originalCode,
        string newCode,
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
        _preparedBitmap = GenerateLabelBitmap(
            elements,
            originalCode,
            newCode,
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

        _printDocument.PrinterSettings.PrinterName = PrinterName;
        if (!_printDocument.PrinterSettings.IsValid)
            throw new InvalidOperationException($"找不到打印机: {PrinterName}");

        var paperSize = new PaperSize("Custom",
            (int)(LabelWidthMm / MmToInch * 100),
            (int)(LabelHeightMm / MmToInch * 100));

        _printDocument.DefaultPageSettings.PaperSize = paperSize;
        _printDocument.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        _printDocument.Print();
    }

    public Bitmap GenerateLabelBitmap(
        IEnumerable<LabelElement> elements,
        string originalCode,
        string newCode,
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
        int widthPx = (int)Math.Round(LabelWidthMm / MmToInch * Dpi);
        int heightPx = (int)Math.Round(LabelHeightMm / MmToInch * Dpi);
        float scale = Dpi / MmToInch;

        var bitmap = new Bitmap(widthPx, heightPx);
        bitmap.SetResolution(Dpi, Dpi);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.White);

        foreach (var element in elements)
        {
            float x = (float)(element.X * scale);
            float y = (float)(element.Y * scale);
            float w = (float)(element.Width * scale);
            float h = (float)(element.Height * scale);

            if (element.Type == LabelElementType.Text)
            {
                string text = TemplateEngine.Resolve(
                    element.ContentTemplate,
                    originalCode,
                    newCode,
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
                float baseEm = Math.Max(4.5f, (float)(element.FontSize * scale / 4.0 * TextScale));
                using var brush = new SolidBrush(Color.Black);
                using var format = CreateStringFormatForElement(element);
                float em = CalculateFittingFontSize(g, element, text, format, w, h, baseEm);
                using var font = CreateFontForElement(element, em);
                g.DrawString(text, font, brush, new RectangleF(x, y, w, h), format);
            }
            else if (element.Type == LabelElementType.QrCode)
            {
                string qrContent = TemplateEngine.Resolve(
                    element.ContentTemplate,
                    originalCode,
                    newCode,
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
                using var qr = QrBitmapService.GenerateQRCode(qrContent, Math.Max(64, (int)Math.Max(w, h)));
                if (qr != null)
                {
                    g.DrawImage(qr, x, y, w, h);
                }
            }
            else if (element.Type == LabelElementType.Barcode)
            {
                string barcodeContent = TemplateEngine.Resolve(
                    element.ContentTemplate,
                    originalCode,
                    newCode,
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
                if (string.IsNullOrWhiteSpace(barcodeContent))
                    continue;

                float textHeight = h * 0.25f;
                float barcodeAreaHeight = Math.Max(10f, h - textHeight);
                using var barcode = BarcodeBitmapService.GenerateCode128(
                    barcodeContent,
                    Math.Max(64, (int)Math.Round(w)),
                    Math.Max(16, (int)Math.Round(barcodeAreaHeight)));
                if (barcode != null)
                {
                    g.DrawImage(barcode, x, y, w, barcodeAreaHeight);
                }

                float baseEm = Math.Max(4.5f, (float)(element.FontSize * scale / 4.0 * TextScale));
                using var brush = new SolidBrush(Color.Black);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                var textRect = new RectangleF(x, y + barcodeAreaHeight, w, Math.Max(1f, h - barcodeAreaHeight));
                float fittedEm = CalculateFittingFontSize(g, element, barcodeContent, format, textRect.Width, textRect.Height, baseEm);
                using var font = CreateFontForElement(element, fittedEm);
                g.DrawString(barcodeContent, font, brush, textRect, format);
            }
            else if (element.Type == LabelElementType.Image)
            {
                var rawImagePath = string.IsNullOrWhiteSpace(element.ImagePath) ? element.ContentTemplate : element.ImagePath;
                var resolvedImagePath = TemplateEngine.Resolve(
                    rawImagePath,
                    originalCode,
                    newCode,
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
                var imagePath = ResolveImagePath(resolvedImagePath);
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    continue;

                using var image = Image.FromFile(imagePath);
                var imageBounds = CreateCenteredScaledBounds(x, y, w, h, LogoImageScale);
                g.DrawImage(image, imageBounds);
            }
            else if (element.Type == LabelElementType.Line)
            {
                using var pen = CreatePenForElement(element, scale);
                g.DrawLine(pen, x, y, x + w, y + h);
            }
            else if (element.Type == LabelElementType.Table)
            {
                DrawTable(g, element, x, y, w, h, scale);
            }
        }

        return bitmap;
    }

    private static Pen CreatePenForElement(LabelElement element, float scale)
    {
        var thicknessPx = Math.Max(1f, (float)element.LineThickness * scale);
        var pen = new Pen(Color.Black, thicknessPx);
        pen.DashStyle = element.LineStyle?.Trim().ToLowerInvariant() switch
        {
            "dash" or "dashed" => DashStyle.Dash,
            "dot" or "dotted" => DashStyle.Dot,
            "dashdot" or "dash-dot" => DashStyle.DashDot,
            _ => DashStyle.Solid
        };
        return pen;
    }

    private static void DrawTable(Graphics graphics, LabelElement element, float x, float y, float width, float height, float scale)
    {
        using var pen = CreatePenForElement(element, scale);
        graphics.DrawRectangle(pen, x, y, width, height);

        var columns = NormalizeTableLines(element.TableColumnLines, element.ColumnWidths, element.ColumnCount, element.Width);
        var rows = NormalizeTableLines(element.TableRowLines, element.RowHeights, element.RowCount, element.Height);
        var columnBands = CreateTableBands(columns, element.Width);
        var rowBands = CreateTableBands(rows, element.Height);

        for (var lineIndex = 0; lineIndex < columns.Count; lineIndex++)
        {
            var currentX = x + (float)(columns[lineIndex] * scale);
            for (var segmentIndex = 0; segmentIndex < rowBands.Count; segmentIndex++)
            {
                if (IsHiddenSegment(element.HiddenTableColumnSegments, lineIndex, segmentIndex))
                    continue;

                var segment = rowBands[segmentIndex];
                graphics.DrawLine(pen, currentX, y + (float)(segment.Start * scale), currentX, y + (float)(segment.End * scale));
            }
        }

        for (var lineIndex = 0; lineIndex < rows.Count; lineIndex++)
        {
            var currentY = y + (float)(rows[lineIndex] * scale);
            for (var segmentIndex = 0; segmentIndex < columnBands.Count; segmentIndex++)
            {
                if (IsHiddenSegment(element.HiddenTableRowSegments, lineIndex, segmentIndex))
                    continue;

                var segment = columnBands[segmentIndex];
                graphics.DrawLine(pen, x + (float)(segment.Start * scale), currentY, x + (float)(segment.End * scale), currentY);
            }
        }
    }

    private static bool IsHiddenSegment(IReadOnlyList<string>? hiddenSegments, int lineIndex, int segmentIndex)
        => hiddenSegments?.Contains($"{lineIndex}:{segmentIndex}", StringComparer.OrdinalIgnoreCase) == true;

    private static List<TableBand> CreateTableBands(IReadOnlyList<double> lines, double total)
    {
        var points = new List<double> { 0 };
        points.AddRange(lines);
        points.Add(total);
        return points.Zip(points.Skip(1), (start, end) => new TableBand(start, end)).ToList();
    }

    private static List<double> NormalizeTableLines(IReadOnlyList<double>? linePositions, IReadOnlyList<double>? segments, int count, double total)
    {
        var directLines = linePositions?
            .Where(x => x > 0.2 && x < total - 0.2)
            .OrderBy(x => x)
            .DistinctBy(x => Math.Round(x, 3))
            .ToList() ?? [];

        if (directLines.Count > 0)
            return directLines;

        var normalizedSegments = NormalizeSegments(segments, count, total);
        var result = new List<double>();
        var current = 0.0;
        for (var i = 0; i < normalizedSegments.Count - 1; i++)
        {
            current += normalizedSegments[i];
            if (current > 0.2 && current < total - 0.2)
                result.Add(current);
        }

        return result;
    }

    private static List<double> NormalizeSegments(IReadOnlyList<double>? values, int count, double total)
    {
        var actualCount = Math.Max(1, count);
        var result = values?
            .Take(actualCount)
            .Select(x => Math.Max(0.2, x))
            .ToList() ?? new List<double>();

        while (result.Count < actualCount)
            result.Add(Math.Max(0.2, total / actualCount));

        var sum = result.Sum();
        if (sum <= 0)
            return Enumerable.Repeat(Math.Max(0.2, total / actualCount), actualCount).ToList();

        var ratio = total / sum;
        return result.Select(x => x * ratio).ToList();
    }

    private sealed record TableBand(double Start, double End);

    private static RectangleF CreateCenteredScaledBounds(float x, float y, float width, float height, float scale)
    {
        if (scale <= 0)
            return new RectangleF(x, y, width, height);

        var scaledWidth = width * scale;
        var scaledHeight = height * scale;
        return new RectangleF(
            x - ((scaledWidth - width) / 2f),
            y - ((scaledHeight - height) / 2f),
            scaledWidth,
            scaledHeight);
    }

    private static string ResolveImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return string.Empty;

        var trimmed = imagePath.Trim();
        if (Path.IsPathRooted(trimmed))
            return File.Exists(trimmed) ? trimmed : string.Empty;

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, trimmed),
            Path.Combine(Environment.CurrentDirectory, trimmed),
            Path.Combine(AppContext.BaseDirectory, trimmed)
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private void PrintDocument_PrintPage(object? sender, PrintPageEventArgs e)
    {
        if (_preparedBitmap == null || e.Graphics == null)
        {
            return;
        }

        // PrintDocument 的 Graphics 默认单位不是像素，直接传入位图像素会导致缩放异常（常见现象：内容被放大并在右下角截断）。
        // 统一切到 Pixel，并抵消打印机硬边距，保证位图坐标与模板坐标一一对应。
        e.Graphics.PageUnit = GraphicsUnit.Pixel;
        e.Graphics.TranslateTransform(
            -(e.PageSettings.HardMarginX / 100f * Dpi),
            -(e.PageSettings.HardMarginY / 100f * Dpi));

        e.Graphics.DrawImage(_preparedBitmap, 0, 0, _preparedBitmap.Width, _preparedBitmap.Height);
    }

    private static Font CreateFontForElement(LabelElement element, float em)
    {
        var style = element.IsBold ? FontStyle.Bold : FontStyle.Regular;
        if (string.Equals(element.Name, "安装位置大字", StringComparison.Ordinal))
        {
            return new Font("Microsoft YaHei", em, style);
        }

        return new Font("Microsoft YaHei UI", em, style);
    }

    private static StringFormat CreateStringFormatForElement(LabelElement element)
    {
        if (string.Equals(element.Name, "安装位置大字", StringComparison.Ordinal))
        {
            return new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        if (string.Equals(element.Name, "产品", StringComparison.Ordinal))
        {
            return new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        if (string.Equals(element.Name, "日期顺序版本班次", StringComparison.Ordinal)
            || element.Name.StartsWith("T1K", StringComparison.Ordinal))
        {
            return new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        return new StringFormat(StringFormat.GenericDefault);
    }

    private static float CalculateFittingFontSize(
        Graphics graphics,
        LabelElement element,
        string text,
        StringFormat format,
        float width,
        float height,
        float initialEm)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0)
            return initialEm;

        const float minEm = 4.0f;
        const float step = 0.35f;

        var current = initialEm;
        while (current > minEm)
        {
            using var testFont = CreateFontForElement(element, current);
            var measured = graphics.MeasureString(text, testFont, new SizeF(width, height), format);
            if (measured.Width <= width + 0.5f && measured.Height <= height + 0.5f)
                return current;

            current -= step;
        }

        return minEm;
    }
}
