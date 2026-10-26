using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LabelTransPrint;
using LabelTransPrint.Models;
using LabelTransPrint.Services;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;
using Microsoft.Win32;

namespace LumbarMassageTest.Views;

public partial class PrintTemplateControl : UserControl
{
    private const double DefaultLabelWidthMm = 40;
    private const double DefaultLabelHeightMm = 20;
    private const double WpfDipPerMillimeter = 96.0 / 25.4;
    private const double PreviewScale = 2.0;

    private static readonly JsonSerializerOptions TemplateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConfigService _configService;
    private readonly ILogService _logService;
    private readonly LabelTemplateService _templateService = new();
    private readonly ObservableCollection<string> _templateCodes = new();
    private ProductModel? _previewModel;
    private int _previewSequenceNumber = 1;

    public event EventHandler? TemplatesChanged;

    public PrintTemplateControl(ConfigService configService, ILogService logService)
    {
        InitializeComponent();
        _configService = configService;
        _logService = logService;
        TemplateListBox.ItemsSource = _templateCodes;
        Loaded += PrintTemplateControl_Loaded;
    }

    private async void PrintTemplateControl_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= PrintTemplateControl_Loaded;
        await LoadPrintersAsync();
        await LoadPreviewStateAsync();
        RefreshTemplates();
    }

    public void SetPreviewModel(ProductModel? model, bool selectConfiguredTemplate = false)
    {
        _previewModel = model;
        if (selectConfiguredTemplate)
        {
            SelectConfiguredTemplate();
        }

        RefreshTemplateInfo();
    }

    private async Task LoadPrintersAsync()
    {
        var printers = PrinterDiscoveryService.GetInstalledPrinters();
        CmbPrinters.ItemsSource = printers;

        var config = await _configService.LoadSystemConfigAsync();
        if (!string.IsNullOrWhiteSpace(config.PrinterName) && printers.Contains(config.PrinterName))
        {
            CmbPrinters.SelectedItem = config.PrinterName;
        }
        else if (printers.Count > 0)
        {
            CmbPrinters.SelectedIndex = 0;
        }
    }

    private void RefreshTemplates(string? preferredCode = null)
    {
        var selected = preferredCode ?? TemplateListBox.SelectedItem as string;
        _templateCodes.Clear();
        foreach (var code in _templateService.GetTemplateCodes())
        {
            _templateCodes.Add(code);
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            TemplateListBox.SelectedItem = _templateCodes.FirstOrDefault(code =>
                string.Equals(code, selected, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            SelectConfiguredTemplate();
        }

        if (TemplateListBox.SelectedItem == null && _templateCodes.Count > 0)
        {
            TemplateListBox.SelectedIndex = 0;
        }

        RefreshTemplateInfo();
    }

    private async void BtnSavePrinter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var printerName = CmbPrinters.SelectedItem as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(printerName))
            {
                MessageBox.Show("请先选择打印机。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = await _configService.LoadSystemConfigAsync();
            config.PrinterName = printerName;
            await _configService.SaveSystemConfigAsync(config);
            TxtStatus.Text = $"默认打印机已保存：{printerName}";
        }
        catch (Exception ex)
        {
            _logService.LogError("保存默认打印机失败", ex);
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCreateTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = TxtNewTemplateCode.Text?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show("请输入模板编码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_templateService.TryLoad(code) != null)
            {
                MessageBox.Show("模板编码已存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var template = CreateDefaultTemplate(code);
            var result = LabelTemplateEditor.Edit(
                Window.GetWindow(this),
                template,
                _templateService,
                CreatePreviewContext(template));

            if (!result.Accepted)
            {
                return;
            }

            _templateService.Save(result.Template);
            TxtNewTemplateCode.Clear();
            RefreshTemplates(result.Template.TemplateCode);
            TemplatesChanged?.Invoke(this, EventArgs.Empty);
            TxtStatus.Text = $"模板已创建：{result.Template.TemplateCode}";
        }
        catch (Exception ex)
        {
            _logService.LogError("创建打印模板失败", ex);
            MessageBox.Show(ex.Message, "创建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnEditTemplate_Click(object sender, RoutedEventArgs e)
    {
        var template = GetSelectedTemplate();
        if (template == null)
        {
            return;
        }

        try
        {
            var result = LabelTemplateEditor.Edit(
                Window.GetWindow(this),
                template,
                _templateService,
                CreatePreviewContext(template));

            if (!result.Accepted)
            {
                return;
            }

            _templateService.Save(result.Template);
            RefreshTemplates(result.Template.TemplateCode);
            TemplatesChanged?.Invoke(this, EventArgs.Empty);
            TxtStatus.Text = $"模板已保存：{result.Template.TemplateCode}";
        }
        catch (Exception ex)
        {
            _logService.LogError("编辑打印模板失败", ex);
            MessageBox.Show(ex.Message, "编辑失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        var code = TemplateListBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var confirm = MessageBox.Show($"确定删除打印模板 [{code}] 吗？", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _templateService.Delete(code);
            RefreshTemplates();
            TemplatesChanged?.Invoke(this, EventArgs.Empty);
            TxtStatus.Text = $"模板已删除：{code}";
        }
        catch (Exception ex)
        {
            _logService.LogError("删除打印模板失败", ex);
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入打印模板",
            Filter = "JSON模板|*.json|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var imported = LabelTemplateImport.Parse(File.ReadAllText(dialog.FileName));
            var template = imported.Template;
            if (imported.Warnings.Count > 0 && MessageBox.Show(string.Join("\n", imported.Warnings) + "\n\n继续导入？",
                    "模板兼容提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            if (string.IsNullOrWhiteSpace(template.TemplateCode))
            {
                template.TemplateCode = Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            _templateService.Save(template);
            RefreshTemplates(template.TemplateCode);
            TemplatesChanged?.Invoke(this, EventArgs.Empty);
            TxtStatus.Text = $"模板已导入：{template.TemplateCode}";
        }
        catch (Exception ex)
        {
            _logService.LogError("导入打印模板失败", ex);
            MessageBox.Show(ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var template = GetSelectedTemplate();
        if (template == null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出打印模板",
            Filter = "JSON模板|*.json",
            FileName = $"{template.TemplateCode}_打印模板.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(template, TemplateJsonOptions));
            TxtStatus.Text = $"模板已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            _logService.LogError("导出打印模板失败", ex);
            MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnTestPrint_Click(object sender, RoutedEventArgs e)
    {
        var template = GetSelectedTemplate();
        if (template == null)
        {
            return;
        }

        var printerName = CmbPrinters.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(printerName))
        {
            MessageBox.Show("请先选择打印机。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var context = CreatePreviewContext(template);
            var payload = LabelTemplateService.BuildQrPayload(
                template,
                context.NewCode,
                context.OriginalCode,
                context.Title,
                context.ProjectCode,
                context.ProductName,
                context.SideLabel,
                context.PartNumber,
                context.RevisionCode,
                context.HardwareVersion,
                context.SoftwareVersion,
                context.DrawingVersion,
                context.VehicleModelCode,
                context.SupplierCode,
                context.SequenceCode,
                context.ShiftCode,
                context.ProductionDate);

            new LabelPrinterHelper(printerName)
            {
                LabelWidthMm = template.LabelWidthMm > 0 ? template.LabelWidthMm : 40,
                LabelHeightMm = template.LabelHeightMm > 0 ? template.LabelHeightMm : 20
            }.Print(
                template.Elements,
                context.OriginalCode,
                payload,
                context.Title,
                context.ProjectCode,
                context.ProductName,
                context.SideLabel,
                context.PartNumber,
                context.RevisionCode,
                context.HardwareVersion,
                context.SoftwareVersion,
                context.DrawingVersion,
                context.VehicleModelCode,
                context.SupplierCode,
                context.SequenceCode,
                context.ShiftCode,
                context.ProductionDate);

            TxtStatus.Text = $"测试打印已发送：{template.TemplateCode} -> {printerName}";
        }
        catch (Exception ex)
        {
            _logService.LogError("测试打印失败", ex);
            MessageBox.Show(ex.Message, "测试打印失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadPreviewStateAsync(reloadModel: true);
        RefreshTemplates(_previewModel?.LabelPrintSettings?.TemplateCode);
        _ = LoadPrintersAsync();
    }

    private void TemplateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshTemplateInfo();
    }

    private LabelTemplate? GetSelectedTemplate()
    {
        var code = TemplateListBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("请先选择打印模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        return _templateService.TryLoad(code);
    }

    private void RefreshTemplateInfo()
    {
        var code = TemplateListBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            TxtTemplateTitle.Text = "请选择模板";
            ClearTemplatePreview("请选择模板");
            return;
        }

        var template = _templateService.TryLoad(code);
        TxtTemplateTitle.Text = template == null ? $"模板不存在：{code}" : $"模板：{template.TemplateCode}";
        if (template == null)
        {
            ClearTemplatePreview("模板不存在");
            return;
        }

        RefreshTemplatePreview(template);
    }

    private void RefreshTemplatePreview(LabelTemplate template)
    {
        try
        {
            var context = CreatePreviewContext(template);
            var payload = LabelTemplateService.BuildQrPayload(
                template,
                context.NewCode,
                context.OriginalCode,
                context.Title,
                context.ProjectCode,
                context.ProductName,
                context.SideLabel,
                context.PartNumber,
                context.RevisionCode,
                context.HardwareVersion,
                context.SoftwareVersion,
                context.DrawingVersion,
                context.VehicleModelCode,
                context.SupplierCode,
                context.SequenceCode,
                context.ShiftCode,
                context.ProductionDate);

            double labelWidthMm = template.LabelWidthMm > 0 ? template.LabelWidthMm : DefaultLabelWidthMm;
            double labelHeightMm = template.LabelHeightMm > 0 ? template.LabelHeightMm : DefaultLabelHeightMm;
            using var bitmap = new LabelPrinterHelper(string.Empty)
            {
                LabelWidthMm = labelWidthMm,
                LabelHeightMm = labelHeightMm
            }.GenerateLabelBitmap(
                template.Elements,
                context.OriginalCode,
                payload,
                context.Title,
                context.ProjectCode,
                context.ProductName,
                context.SideLabel,
                context.PartNumber,
                context.RevisionCode,
                context.HardwareVersion,
                context.SoftwareVersion,
                context.DrawingVersion,
                context.VehicleModelCode,
                context.SupplierCode,
                context.SequenceCode,
                context.ShiftCode,
                context.ProductionDate);

            var source = WpfBitmapConverter.ToBitmapImage(bitmap);
            ImgTemplatePreview.Source = source;
            ImgTemplatePreview.Width = labelWidthMm * WpfDipPerMillimeter * PreviewScale;
            ImgTemplatePreview.Height = labelHeightMm * WpfDipPerMillimeter * PreviewScale;
            PreviewHost.Visibility = Visibility.Visible;
            TxtPreviewEmpty.Visibility = Visibility.Collapsed;
            TxtPreviewEmpty.Text = string.Empty;
            TxtPreviewSize.Text = $"{labelWidthMm:0.##}mm x {labelHeightMm:0.##}mm，预览 2x";
        }
        catch (Exception ex)
        {
            _logService.LogError("刷新打印模板预览失败", ex);
            ClearTemplatePreview($"预览失败: {ex.Message}");
        }
    }

    private void ClearTemplatePreview(string message)
    {
        ImgTemplatePreview.Source = null;
        ImgTemplatePreview.Width = double.NaN;
        ImgTemplatePreview.Height = double.NaN;
        PreviewHost.Visibility = Visibility.Collapsed;
        TxtPreviewSize.Text = string.Empty;
        TxtPreviewEmpty.Text = message;
        TxtPreviewEmpty.Visibility = Visibility.Visible;
    }

    private static LabelTemplate CreateDefaultTemplate(string code)
    {
        return new LabelTemplate
        {
            TemplateCode = code,
            DisplayName = code,
            LabelWidthMm = 40,
            LabelHeightMm = 20,
            ProductTitle = "{{ProductName}}",
            Elements =
            {
                new LabelElement
                {
                    Name = "零件号",
                    Type = LabelElementType.Text,
                    X = 2,
                    Y = 2,
                    Width = 24,
                    Height = 5,
                    FontSize = 2.4,
                    IsBold = true,
                    ContentTemplate = "{{PartNumber}}"
                },
                new LabelElement
                {
                    Name = "生产信息",
                    Type = LabelElementType.Text,
                    X = 2,
                    Y = 8,
                    Width = 24,
                    Height = 8,
                    FontSize = 2.1,
                    IsBold = false,
                    ContentTemplate = "{{ProductionDateCompact}}\n{{SequenceCode5}}"
                },
                new LabelElement
                {
                    Name = "二维码",
                    Type = LabelElementType.QrCode,
                    X = 27,
                    Y = 2,
                    Width = 11,
                    Height = 11,
                    ContentTemplate = "{{NewCode}}"
                }
            }
        };
    }

    private async Task LoadPreviewStateAsync(bool reloadModel = false)
    {
        try
        {
            var runtime = await _configService.LoadRuntimeConfigAsync();
            _previewSequenceNumber = 1;
            if (!reloadModel && _previewModel != null)
            {
                return;
            }

            var modelName = _previewModel?.ModelName;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = runtime.LastProductModel;
            }

            if (string.IsNullOrWhiteSpace(modelName))
            {
                if (reloadModel)
                {
                    _previewModel = null;
                }

                return;
            }

            var models = await _configService.LoadProductModelsAsync();
            _previewModel = models.FirstOrDefault(model =>
                string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logService.LogWarning("加载打印模板预览机型失败", ex);
        }
    }

    private void SelectConfiguredTemplate()
    {
        var templateCode = _previewModel?.LabelPrintSettings?.TemplateCode;
        if (string.IsNullOrWhiteSpace(templateCode) || _templateCodes.Count == 0)
        {
            return;
        }

        TemplateListBox.SelectedItem = _templateCodes.FirstOrDefault(code =>
            string.Equals(code, templateCode, StringComparison.OrdinalIgnoreCase));
    }

    private LabelPrintContext CreatePreviewContext(LabelTemplate? template = null)
    {
        var context = CreateSampleContext();
        var model = _previewModel;
        var settings = model?.LabelPrintSettings;

        if (model != null)
        {
            context.ProjectCode = string.IsNullOrWhiteSpace(model.ModelName) ? context.ProjectCode : model.ModelName;
            context.Title = template != null && !string.IsNullOrWhiteSpace(template.ProductTitle)
                ? template.ProductTitle
                : context.ProjectCode;
        }

        if (settings != null)
        {
            context.ProductName = UseConfiguredOrSample(settings.ProductName, context.ProductName);
            context.SideLabel = UseConfiguredOrSample(settings.SideLabel, context.SideLabel);
            context.PartNumber = UseConfiguredOrSample(settings.PartNumber, context.PartNumber);
            context.RevisionCode = UseConfiguredOrSample(settings.RevisionCode, context.RevisionCode);
            context.HardwareVersion = UseConfiguredOrSample(settings.HardwareVersion, context.HardwareVersion);
            context.SoftwareVersion = UseConfiguredOrSample(settings.SoftwareVersion, context.SoftwareVersion);
            context.DrawingVersion = UseConfiguredOrSample(settings.DrawingVersion, context.DrawingVersion);
            context.VehicleModelCode = UseConfiguredOrSample(settings.VehicleModelCode, context.VehicleModelCode);
            context.SupplierCode = UseConfiguredOrSample(settings.SupplierCode, context.SupplierCode);
            context.ShiftCode = UseConfiguredOrSample(settings.ShiftCode, context.ShiftCode);
        }

        var uses5 = template != null && (LabelTemplateService.UsesField(template.Elements, LabelTemplateService.SequenceCode5Token)
            || (string.Equals(template.QrPayloadMode, "Custom", StringComparison.OrdinalIgnoreCase)
                && template.QrPayloadExpression.Contains(LabelTemplateService.SequenceCode5Token, StringComparison.OrdinalIgnoreCase)));
        context.SequenceCode = Math.Max(1, _previewSequenceNumber).ToString(uses5 ? "D5" : "D4");
        context.ProductionDate = DateTime.Today;
        context.FieldValues = context.ToProductionContext().CreateFields();
        context.Fields = _templateService.Fields.Concat(ProductionLabelContext.Fields).ToArray();
        return context;
    }

    private static string UseConfiguredOrSample(string? configured, string sample)
    {
        return string.IsNullOrWhiteSpace(configured) ? sample : configured.Trim();
    }

    private static LabelPrintContext CreateSampleContext()
    {
        return new LabelPrintContext
        {
            OriginalCode = "TEST-CODE",
            NewCode = "TEST-CODE",
            Title = "标签测试",
            ProjectCode = "MODEL",
            ProductName = "测试零件",
            SideLabel = "LH",
            PartNumber = "PART-NO",
            RevisionCode = "AA",
            HardwareVersion = "HW",
            SoftwareVersion = "SW",
            DrawingVersion = "DWG",
            VehicleModelCode = "CAR",
            SupplierCode = "SUP",
            SequenceCode = "00001",
            ShiftCode = "A",
            ProductionDate = DateTime.Today
        };
    }
}
