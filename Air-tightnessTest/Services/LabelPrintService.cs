using System;
using System.Linq;
using System.Threading.Tasks;
using LabelTransPrint.Services;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services;

public sealed class LabelPrintService
{
    private const double DefaultLabelWidthMm = 40;
    private const double DefaultLabelHeightMm = 20;
    private readonly ConfigService _configService;
    private readonly ILogService _logService;
    private readonly LabelTemplateService _templateService = new();

    public LabelPrintService(ConfigService configService, ILogService logService)
    {
        _configService = configService;
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

        string sequenceCode = BuildSequenceCode(record);
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
            settings.ShiftCode,
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
            settings.ShiftCode,
            record.TestTime.Date);

        _logService.LogInfo($"通道{record.Channel}标签已发送打印：模板={template.TemplateCode}，打印机={systemConfig.PrinterName}");
        return LabelPrintResult.Printed(template.TemplateCode, systemConfig.PrinterName);
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

    private static string BuildSequenceCode(TestRecord record)
    {
        var source = record.TestCount > 0 ? record.TestCount : 1;
        return source.ToString("D5");
    }
}

public sealed record LabelPrintResult(bool PrintJobSent, string Message)
{
    public static LabelPrintResult Skipped(string message) => new(false, message);

    public static LabelPrintResult Printed(string templateCode, string printerName)
        => new(true, $"已打印标签：{templateCode} -> {printerName}");
}
