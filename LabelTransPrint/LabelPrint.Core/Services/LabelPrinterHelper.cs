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
    private BatchPrintContext? _batchPrintContext;
    private int _batchPageIndex;

    private const float DefaultDpi = 300f;
    private const float MmToInch = 25.4f;
    private const float PointsPerInch = 72f;
    private const float LogoImageScale = 1.18f;
    private const string DefaultFontFamily = "Microsoft YaHei UI";
    private float _renderDpiX = DefaultDpi;
    private float _renderDpiY = DefaultDpi;
    private static readonly HashSet<string> InstalledFontNames = new InstalledFontCollection()
        .Families
        .Select(f => f.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string PrinterName { get; set; }
    public double LabelWidthMm { get; set; } = 40;
    public double LabelHeightMm { get; set; } = 20;
    public double TextScale { get; set; } = 1.0;
    public double ImageScale { get; set; } = LogoImageScale;
    public double OffsetXmm { get; set; }
    public double OffsetYmm { get; set; }
    public int? DpiOverride { get; set; }

    public LabelPrinterHelper(string printerName)
    {
        PrinterName = printerName;
        _printDocument = new PrintDocument();
        _printDocument.OriginAtMargins = false;
        _printDocument.PrintPage += PrintDocument_PrintPage;
    }

    public void Print(
        IEnumerable<LabelElement> elements,
        string title,
        string partNumber,
        string backupField1,
        string backupField2,
        string sequenceCode,
        DateTime productionDate,
        string operatorName)
    {
        var items = elements.ToList();
        SequenceCodeValidator.Validate(items, sequenceCode);
        ConfigurePrintDocument();
        try
        {
            _preparedBitmap = GenerateLabelBitmap(
                items,
                title,
                partNumber,
                backupField1,
                backupField2,
                sequenceCode,
                productionDate,
                operatorName);

            _printDocument.Print();
        }
        finally
        {
            _preparedBitmap?.Dispose();
            _preparedBitmap = null;
        }
    }

    public void PrintBatch(
        IEnumerable<LabelElement> elements,
        string title,
        string partNumber,
        string backupField1,
        string backupField2,
        IReadOnlyList<string> sequenceCodes,
        DateTime productionDate,
        string operatorName)
    {
        if (sequenceCodes.Count == 0)
            return;

        var items = elements.ToList();
        foreach (var sequenceCode in sequenceCodes)
            SequenceCodeValidator.Validate(items, sequenceCode);

        ConfigurePrintDocument();
        _batchPrintContext = new BatchPrintContext(
            items,
            title,
            partNumber,
            backupField1,
            backupField2,
            sequenceCodes.ToArray(),
            productionDate,
            operatorName);
        _batchPageIndex = 0;

        try
        {
            _printDocument.Print();
        }
        finally
        {
            _batchPrintContext = null;
            _batchPageIndex = 0;
        }
    }

    public Bitmap GenerateLabelBitmap(
        IEnumerable<LabelElement> elements,
        string title,
        string partNumber,
        string backupField1,
        string backupField2,
        string sequenceCode,
        DateTime productionDate,
        string operatorName)
        => GenerateLabelBitmapCore(elements, text => TemplateEngine.Resolve(text, title, partNumber, backupField1,
            backupField2, sequenceCode, productionDate, operatorName));

    /// <summary>Renders application-defined field values without applying a sequence-number policy.</summary>
    public Bitmap GenerateLabelBitmap(IEnumerable<LabelElement> elements, LabelFieldValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return GenerateLabelBitmapCore(elements, values.Resolve);
    }

    /// <summary>Prints using application-defined fields after validating required fields and sequence numbers.</summary>
    public void Print(IEnumerable<LabelElement> elements, LabelFieldValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = elements.ToList();
        var missing = items.Where(item => item.Type is LabelElementType.Text or LabelElementType.QrCode or LabelElementType.Barcode or LabelElementType.Image)
            .SelectMany(item => values.FindMissing(item.Type == LabelElementType.Image && !string.IsNullOrWhiteSpace(item.ImagePath)
                ? item.ImagePath : item.ContentTemplate)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"缺少打印字段：{string.Join("、", missing)}");
        var sequenceToken = LabelTemplateService.UsesField(items, LabelTemplateService.SequenceCode5Token)
            ? LabelTemplateService.SequenceCode5Token : LabelTemplateService.SequenceCodeToken;
        values.TryGetValue(sequenceToken, out var sequenceCode);
        if (sequenceCode is null && sequenceToken == LabelTemplateService.SequenceCodeToken)
            values.TryGetValue(LabelTemplateService.LegacySequenceCodeToken, out sequenceCode);
        SequenceCodeValidator.Validate(items, sequenceCode);
        ConfigurePrintDocument();
        try
        {
            _preparedBitmap = GenerateLabelBitmap(items, values);
            _printDocument.Print();
        }
        finally
        {
            _preparedBitmap?.Dispose();
            _preparedBitmap = null;
        }
    }

    private Bitmap GenerateLabelBitmapCore(IEnumerable<LabelElement> elements, Func<string?, string> resolve)
    {
        ApplyRenderResolution(requireValidPrinter: false);

        int widthPx = (int)Math.Round(LabelWidthMm / MmToInch * _renderDpiX);
        int heightPx = (int)Math.Round(LabelHeightMm / MmToInch * _renderDpiY);
        float scaleX = _renderDpiX / MmToInch;
        float scaleY = _renderDpiY / MmToInch;
        float lineScale = (scaleX + scaleY) / 2f;

        var bitmap = new Bitmap(widthPx, heightPx);
        bitmap.SetResolution(_renderDpiX, _renderDpiY);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.White);

        foreach (var element in elements)
        {
            float x = (float)(element.X * scaleX);
            float y = (float)(element.Y * scaleY);
            float w = (float)(element.Width * scaleX);
            float h = (float)(element.Height * scaleY);

            if (element.Type == LabelElementType.Text)
            {
                string text = resolve(element.ContentTemplate);
                float baseEm = Math.Max(4.0f, (float)(element.FontSize / MmToInch * PointsPerInch * TextScale));
                using var brush = new SolidBrush(Color.Black);
                using var format = CreateStringFormatForElement(element);
                float em = CalculateFittingFontSize(g, element, text, format, w, h, baseEm);
                using var font = CreateFontForElement(element, em);
                g.DrawString(text, font, brush, new RectangleF(x, y, w, h), format);
            }
            else if (element.Type == LabelElementType.QrCode)
            {
                string qrContent = resolve(element.ContentTemplate);
                // 生成尺寸固定为参考值,与渲染 DPI 解耦,保证 200/300dpi 下二维码物理尺寸一致
                int qrGenSize = Math.Max(300, Math.Min(1200, qrContent.Length * 24));
                using var qr = QrBitmapService.GenerateQRCode(qrContent, qrGenSize, element.QrErrorCorrection, element.QrMargin);
                if (qr != null)
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(qr, x, y, w, h);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                }
            }
            else if (element.Type == LabelElementType.Barcode)
            {
                string barcodeContent = resolve(element.ContentTemplate);
                if (string.IsNullOrWhiteSpace(barcodeContent))
                    continue;

                float textHeight = h * 0.25f;
                float barcodeAreaHeight = Math.Max(10f, h - textHeight);
                // 生成宽度固定为参考值,与渲染 DPI 解耦;否则 ZXing 按整数模块缩放并居中,
                // 200/300dpi 下条码物理宽度会明显不一致
                int barcodeGenWidth = Math.Max(600, barcodeContent.Length * 22);
                using var barcode = BarcodeBitmapService.GenerateCode128(
                    barcodeContent,
                    barcodeGenWidth,
                    Math.Max(16, (int)Math.Round(barcodeAreaHeight)));
                if (barcode != null)
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(barcode, x, y, w, barcodeAreaHeight);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                }

                float baseEm = Math.Max(4.0f, (float)(element.FontSize / MmToInch * PointsPerInch * TextScale));
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
                var resolvedImagePath = resolve(rawImagePath);
                var imagePath = ResolveImagePath(resolvedImagePath);
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    continue;

                using var image = Image.FromFile(imagePath);
                var imageBounds = CreateCenteredScaledBounds(x, y, w, h, (float)ImageScale);
                g.DrawImage(image, imageBounds);
            }
            else if (element.Type == LabelElementType.Line)
            {
                using var pen = CreatePenForElement(element, lineScale);
                g.DrawLine(pen, x, y, x + w, y + h);
            }
            else if (element.Type == LabelElementType.Table)
            {
                DrawTable(g, element, x, y, w, h, scaleX, scaleY, lineScale);
            }
        }

        return bitmap;
    }

    private void ApplyRenderResolution(bool requireValidPrinter)
    {
        if (DpiOverride is > 0)
        {
            _renderDpiX = DpiOverride.Value;
            _renderDpiY = DpiOverride.Value;
            return;
        }

        if (!string.IsNullOrWhiteSpace(PrinterName))
            _printDocument.PrinterSettings.PrinterName = PrinterName;

        if (!_printDocument.PrinterSettings.IsValid)
        {
            if (requireValidPrinter)
                throw new InvalidOperationException($"找不到打印机: {PrinterName}");

            _renderDpiX = DefaultDpi;
            _renderDpiY = DefaultDpi;
            return;
        }

        var resolution = _printDocument.DefaultPageSettings.PrinterResolution;
        _renderDpiX = NormalizeDpi(resolution.X);
        _renderDpiY = NormalizeDpi(resolution.Y);
    }

    private static float NormalizeDpi(int dpi)
        => dpi > 0 ? dpi : DefaultDpi;

    private void ConfigurePrintDocument()
    {
        _printDocument.PrinterSettings.PrinterName = PrinterName;
        if (!_printDocument.PrinterSettings.IsValid)
            throw new InvalidOperationException($"找不到打印机: {PrinterName}");

        ApplyRenderResolution(requireValidPrinter: true);

        var paperSize = new PaperSize("Custom",
            (int)(LabelWidthMm / MmToInch * 100),
            (int)(LabelHeightMm / MmToInch * 100));

        _printDocument.DefaultPageSettings.PaperSize = paperSize;
        _printDocument.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
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

    private static void DrawTable(Graphics graphics, LabelElement element, float x, float y, float width, float height, float scaleX, float scaleY, float lineScale)
    {
        using var pen = CreatePenForElement(element, lineScale);
        graphics.DrawRectangle(pen, x, y, width, height);

        var columns = NormalizeTableLines(element.TableColumnLines, element.ColumnWidths, element.ColumnCount, element.Width);
        var rows = NormalizeTableLines(element.TableRowLines, element.RowHeights, element.RowCount, element.Height);
        var columnBands = CreateTableBands(columns, element.Width);
        var rowBands = CreateTableBands(rows, element.Height);

        for (var lineIndex = 0; lineIndex < columns.Count; lineIndex++)
        {
            var currentX = x + (float)(columns[lineIndex] * scaleX);
            for (var segmentIndex = 0; segmentIndex < rowBands.Count; segmentIndex++)
            {
                if (IsHiddenSegment(element.HiddenTableColumnSegments, lineIndex, segmentIndex))
                    continue;

                var segment = rowBands[segmentIndex];
                graphics.DrawLine(pen, currentX, y + (float)(segment.Start * scaleY), currentX, y + (float)(segment.End * scaleY));
            }
        }

        for (var lineIndex = 0; lineIndex < rows.Count; lineIndex++)
        {
            var currentY = y + (float)(rows[lineIndex] * scaleY);
            for (var segmentIndex = 0; segmentIndex < columnBands.Count; segmentIndex++)
            {
                if (IsHiddenSegment(element.HiddenTableRowSegments, lineIndex, segmentIndex))
                    continue;

                var segment = columnBands[segmentIndex];
                graphics.DrawLine(pen, x + (float)(segment.Start * scaleX), currentY, x + (float)(segment.End * scaleX), currentY);
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
        if (e.Graphics == null)
        {
            return;
        }

        if (_batchPrintContext is not null)
        {
            PrintBatchPage(e);
            return;
        }

        if (_preparedBitmap == null)
            return;

        DrawPreparedBitmap(e, _preparedBitmap);
    }

    private void PrintBatchPage(PrintPageEventArgs e)
    {
        var context = _batchPrintContext;
        if (context is null || _batchPageIndex >= context.SequenceCodes.Count)
        {
            e.HasMorePages = false;
            return;
        }

        using var bitmap = GenerateLabelBitmap(
            context.Elements,
            context.Title,
            context.PartNumber,
            context.BackupField1,
            context.BackupField2,
            context.SequenceCodes[_batchPageIndex],
            context.ProductionDate,
            context.OperatorName);

        DrawPreparedBitmap(e, bitmap);
        _batchPageIndex++;
        e.HasMorePages = _batchPageIndex < context.SequenceCodes.Count;
    }

    private void DrawPreparedBitmap(PrintPageEventArgs e, Bitmap bitmap)
    {
        var graphics = e.Graphics;
        if (graphics is null)
            return;

        // PrintDocument 的 Graphics 默认单位不是像素，直接传入位图像素会导致缩放异常（常见现象：内容被放大并在右下角截断）。
        // 统一切到 Pixel，并抵消打印机硬边距，保证位图坐标与模板坐标一一对应。
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.TranslateTransform(
            -(e.PageSettings.HardMarginX / 100f * _renderDpiX) + (float)(OffsetXmm / MmToInch * _renderDpiX),
            -(e.PageSettings.HardMarginY / 100f * _renderDpiY) + (float)(OffsetYmm / MmToInch * _renderDpiY));

        graphics.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
    }

    private static Font CreateFontForElement(LabelElement element, float em)
    {
        var style = element.IsBold ? FontStyle.Bold : FontStyle.Regular;
        var fontName = ResolveFontFamilyName(element.FontFamily);
        return TryCreateFont(fontName, em, style)
            ?? TryCreateFont(fontName, em, FontStyle.Regular)
            ?? TryCreateFont(DefaultFontFamily, em, style)
            ?? new Font(System.Drawing.FontFamily.GenericSansSerif, em, FontStyle.Regular, GraphicsUnit.Point);
    }

    private static Font? TryCreateFont(string fontFamily, float em, FontStyle style)
    {
        try
        {
            return new Font(fontFamily, em, style, GraphicsUnit.Point);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ResolveFontFamilyName(string? fontFamily)
    {
        var normalized = fontFamily?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && InstalledFontNames.Contains(normalized)
            ? normalized
            : DefaultFontFamily;
    }

    private static StringFormat CreateStringFormatForElement(LabelElement element)
    {
        var format = new StringFormat(StringFormat.GenericDefault)
        {
            Trimming = StringTrimming.None,
            Alignment = element.HorizontalAlignment?.Trim().ToLowerInvariant() switch
            {
                "center" => StringAlignment.Center,
                "right" => StringAlignment.Far,
                _ => StringAlignment.Near
            }
        };
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        return format;
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
        var isSingleLine = !text.Contains('\n') && !text.Contains('\r');

        var current = initialEm;
        while (current > minEm)
        {
            using var testFont = CreateFontForElement(element, current);
            var measured = graphics.MeasureString(text, testFont, new SizeF(width, height), format);
            if (isSingleLine && measured.Height <= height + 0.5f)
                return current;

            if (!isSingleLine && measured.Width <= width + 0.5f && measured.Height <= height + 0.5f)
                return current;

            current -= step;
        }

        return minEm;
    }

    private sealed record BatchPrintContext(
        IReadOnlyList<LabelElement> Elements,
        string Title,
        string PartNumber,
        string BackupField1,
        string BackupField2,
        IReadOnlyList<string> SequenceCodes,
        DateTime ProductionDate,
        string OperatorName);
}
