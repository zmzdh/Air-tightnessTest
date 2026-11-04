using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelTransPrint.Services;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services;

public sealed class LabelPrintService
{
    private const double DefaultLabelWidthMm = 40;
    private const double DefaultLabelHeightMm = 20;
    private readonly ConfigService _configService;
    private readonly DatabaseService _databaseService;
    private readonly ILogService _logService;
    private readonly LabelTemplateService _templateService = new();
    private readonly SemaphoreSlim _printSequenceLock = new(1, 1);

    public LabelPrintService(ConfigService configService, DatabaseService databaseService, ILogService logService)
    {
        _configService = configService;
        _databaseService = databaseService;
        _logService = logService;
    }

    public async Task<LabelPrintResult> PrintIfEnabledAsync(TestRecord record)
    {
        if (record.Result != TestResult.Pass)
        {
            return LabelPrintResult.Skipped("测试未通过，不执行打印");
        }

        var model = await FindModelAsync(record.ProductModel).ConfigureAwait(false);
        if (model?.LabelPrintSettings?.EnablePrinting != true)
        {
            return LabelPrintResult.Skipped("当前机型未启用打印");
        }

        var settings = model.LabelPrintSettings;
        if (string.IsNullOrWhiteSpace(settings.TemplateCode))
        {
            throw new InvalidOperationException($"机型 {model.ModelName} 已启用打印，但未选择打印模板");
        }

        var systemConfig = await _configService.LoadSystemConfigAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(systemConfig.PrinterName))
        {
            throw new InvalidOperationException("未配置默认打印机，请在打印模板页面选择打印机");
        }

        var template = _templateService.TryLoad(settings.TemplateCode)
            ?? throw new InvalidOperationException($"打印模板不存在：{settings.TemplateCode}");

        var printContext = await AllocatePrintContextAsync(record, model, settings, template).ConfigureAwait(false);
        string sequenceCode = printContext.SequenceCode;
        SequenceCodeValidator.ValidateTemplate(template, sequenceCode, requireSequenceCode: true);
        string originalCode = record.ProductCode ?? string.Empty;
        string fallbackPayload = string.IsNullOrWhiteSpace(originalCode)
            ? $"{settings.PartNumber}-{record.TestTime:yyyyMMdd}-{sequenceCode}"
            : originalCode;
        string title = string.IsNullOrWhiteSpace(template.ProductTitle)
            ? model.ModelName
            : template.ProductTitle;
        string productName = string.IsNullOrWhiteSpace(settings.ProductName)
            ? model.ModelName
            : settings.ProductName;

        string qrPayload = LabelTemplateService.BuildQrPayload(
            template,
            fallbackPayload,
            originalCode,
            title,
            model.ModelName,
            productName,
            settings.SideLabel,
            settings.PartNumber,
            settings.RevisionCode,
            settings.HardwareVersion,
            settings.SoftwareVersion,
            settings.DrawingVersion,
            settings.VehicleModelCode,
            settings.SupplierCode,
            sequenceCode,
            record.ShiftCode,
            record.TestTime.Date);

        var printer = new LabelPrinterHelper(systemConfig.PrinterName)
        {
            LabelWidthMm = template.LabelWidthMm > 0 ? template.LabelWidthMm : DefaultLabelWidthMm,
            LabelHeightMm = template.LabelHeightMm > 0 ? template.LabelHeightMm : DefaultLabelHeightMm
        };

        printer.Print(
            template.Elements,
            originalCode,
            qrPayload,
            title,
            model.ModelName,
            productName,
            settings.SideLabel,
            settings.PartNumber,
            settings.RevisionCode,
            settings.HardwareVersion,
            settings.SoftwareVersion,
            settings.DrawingVersion,
            settings.VehicleModelCode,
            settings.SupplierCode,
            sequenceCode,
            record.ShiftCode,
            record.TestTime.Date);

        _logService.LogInfo($"通道{record.Channel}标签已发送打印：模板={template.TemplateCode}，打印机={systemConfig.PrinterName}");
        return LabelPrintResult.Printed(template.TemplateCode, systemConfig.PrinterName, record.PrintSerialNumber);
    }

    private async Task<ProductModel?> FindModelAsync(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var models = await _configService.LoadProductModelsAsync().ConfigureAwait(false);
        return models.FirstOrDefault(model =>
            string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<PrintContext> AllocatePrintContextAsync(
        TestRecord record,
        ProductModel model,
        LabelPrintSettings settings,
        LabelTransPrint.Models.LabelTemplate template)
    {
        await _printSequenceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var runtime = await _configService.LoadRuntimeConfigAsync().ConfigureAwait(false);
            DateTime printDate = DateTime.Today;
            EnsurePrintSequenceState(runtime, printDate);
            int sequence = Math.Max(1, runtime.PrintSequenceNumber);
            int width = UsesFiveDigitSequence(template) ? 5 : 4;

            while (true)
            {
                string serialNumber = BuildPrintSerialNumber(settings.PartNumber, printDate, sequence, width);
                bool exists = await _databaseService.PrintSerialNumberExistsAsync(serialNumber).ConfigureAwait(false);
                bool reserved = !exists && await _databaseService.TryReservePrintSerialAsync(
                    printDate,
                    model.ModelName,
                    settings.PartNumber,
                    sequence,
                    serialNumber).ConfigureAwait(false);
                if (reserved)
                {
                    runtime.PrintSequenceNumber = sequence + 1;
                    if (!await _configService.SaveRuntimeConfigAsync(runtime).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("保存下一个打印序列号失败，已取消本次打印。");
                    }

                    record.PrintSerialNumber = serialNumber;
                    record.ShiftCode = runtime.CurrentShiftCode?.Trim() ?? string.Empty;
                    return new PrintContext(sequence.ToString($"D{width}"));
                }

                sequence++;
            }
        }
        finally
        {
            _printSequenceLock.Release();
        }
    }

    private static bool UsesFiveDigitSequence(LabelTransPrint.Models.LabelTemplate template)
        => LabelTemplateService.UsesField(template.Elements, LabelTemplateService.SequenceCode5Token)
            || (string.Equals(template.QrPayloadMode, "Custom", StringComparison.OrdinalIgnoreCase)
                && template.QrPayloadExpression.Contains(LabelTemplateService.SequenceCode5Token, StringComparison.OrdinalIgnoreCase));

    private static string BuildPrintSerialNumber(string? partNumber, DateTime printDate, int sequence, int width)
    {
        var segments = new[]
        {
            partNumber?.Trim() ?? string.Empty,
            printDate.ToString("yyyyMMdd"),
            Math.Max(1, sequence).ToString($"D{width}")
        };
        return string.Join("-", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private static void EnsurePrintSequenceState(RuntimeConfig runtime, DateTime printDate)
    {
        string today = printDate.Date.ToString("yyyy-MM-dd");
        if (!string.Equals(runtime.PrintSequenceDate, today, StringComparison.Ordinal))
        {
            runtime.PrintSequenceDate = today;
            runtime.PrintSequenceNumber = 1;
        }

        if (runtime.PrintSequenceNumber < 1)
        {
            runtime.PrintSequenceNumber = 1;
        }
    }

    private sealed record PrintContext(string SequenceCode);

}

public sealed record LabelPrintResult(bool PrintJobSent, string Message)
{
    public static LabelPrintResult Skipped(string message) => new(false, message);

    public static LabelPrintResult Printed(string templateCode, string printerName, string printSerialNumber)
        => new(true, $"已打印标签：{templateCode} -> {printerName}，流水号：{printSerialNumber}");
}
