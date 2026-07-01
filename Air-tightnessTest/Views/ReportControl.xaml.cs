// UserControls/ReportControl.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Text;
using System.IO;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.UserControls
{
    public partial class ReportControl : UserControl
    {
        private sealed class ComboBoxOption
        {
            public ComboBoxOption(string display, string value)
            {
                Display = display;
                Value = value;
            }

            public string Display { get; }

            public string Value { get; }
        }

        private readonly DatabaseService _dbService;
        private readonly ConfigService _configService;

        private int _currentPage = 1;
        private int _pageSize = 100;
        private int _totalPages = 1;
        private List<TestRecord> _currentRecords = new List<TestRecord>();

        public ReportControl(DatabaseService dbService, ConfigService configService)
        {
            _dbService = dbService ?? new DatabaseService();
            _configService = configService ?? new ConfigService();

            InitializeComponent();

            InitializeControls();
            LoadInitialData();
        }

        private async void InitializeControls()
        {
            // 设置默认查询日期
            EndDatePicker.SelectedDate = DateTime.Now;
            StartDatePicker.SelectedDate = DateTime.Now.AddDays(-7);

            // 设置默认查询结果
            CmbTestResult.SelectedIndex = 0;

            // 加载产品型号
            try
            {
                var models = await _configService.LoadProductModelsAsync();
                var modelOptions = new List<ComboBoxOption>
                {
                    new ComboBoxOption("全部", string.Empty)
                };
                modelOptions.AddRange(models.Select(m => new ComboBoxOption(m.ModelName, m.ModelName)));
                CmbProductModel.ItemsSource = modelOptions;
                CmbProductModel.SelectedValue = string.Empty;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载产品型号失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // 加载操作员
            try
            {
                var operators = await _dbService.GetOperatorsAsync();
                var operatorOptions = new List<ComboBoxOption>
                {
                    new ComboBoxOption("全部", string.Empty)
                };
                operatorOptions.AddRange(operators
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(o => new ComboBoxOption(o, o)));
                CmbOperator.ItemsSource = operatorOptions;
                CmbOperator.SelectedValue = string.Empty;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载操作员失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadInitialData()
        {
            await QueryData();
        }

        private async Task QueryData()
        {
            try
            {
                var startDate = StartDatePicker.SelectedDate ?? DateTime.Now.AddDays(-7);
                var endDate = EndDatePicker.SelectedDate ?? DateTime.Now;

                var model = CmbProductModel.SelectedValue?.ToString() ?? string.Empty;
                var operatorName = CmbOperator.SelectedValue?.ToString() ?? string.Empty;
                var result = (CmbTestResult.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                var workOrder = TxtWorkOrder.Text.Trim();
                var productCode = TxtProductCode.Text.Trim();

                // 查询统计数据
                await LoadStatistics(startDate, endDate, model, operatorName, result, workOrder, productCode);

                // 查询详细记录
                await LoadTestRecords(startDate, endDate, model, operatorName, result, workOrder, productCode);

                // 加载分析数据
                await LoadAnalysisData(startDate, endDate, model, operatorName, result, workOrder, productCode);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查询数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadStatistics(DateTime startDate, DateTime endDate, string model, string operatorName, string result, string workOrder, string productCode)
        {
            var stats = await _dbService.GetTestStatisticsAsync(startDate, endDate, model, operatorName, result, workOrder, productCode);

            TxtTotalCount.Text = stats.TotalCount.ToString();
            TxtPassCount.Text = stats.PassCount.ToString();
            TxtFailCount.Text = stats.FailCount.ToString();
            TxtPassRate.Text = $"{(stats.TotalCount > 0 ? (double)stats.PassCount / stats.TotalCount * 100 : 0):F1}%";
            TxtAvgTestTime.Text = $"{stats.AvgTestDuration:F1}s";
        }

        private async Task LoadTestRecords(DateTime startDate, DateTime endDate, string model,
                                          string operatorName, string result, string workOrder, string productCode)
        {
            try
            {
                // 获取总记录数
                var totalCount = await _dbService.GetTestRecordCountAsync(startDate, endDate, model, operatorName, result, workOrder, productCode);

                // 处理空数据库情况
                if (totalCount == 0)
                {
                    _totalPages = 1;
                    _currentPage = 1;
                    _currentRecords = new List<TestRecord>();
                    TestRecordsGrid.ItemsSource = _currentRecords;

                    // 显示无数据消息
                    MessageBox.Show("没有找到符合条件的测试记录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _totalPages = (int)Math.Ceiling((double)totalCount / _pageSize);

                if (_currentPage > _totalPages) _currentPage = 1;
                if (_currentPage < 1) _currentPage = 1;

                _currentRecords = await _dbService.GetTestRecordsAsync(startDate, endDate, model, operatorName, result, workOrder, productCode, _currentPage, _pageSize);
                TestRecordsGrid.ItemsSource = _currentRecords;

                UpdatePageInfo();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载测试记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadAnalysisData(DateTime startDate, DateTime endDate, string model, string operatorName, string result, string workOrder, string productCode)
        {
            // 按日期统计
            var dateStats = await _dbService.GetDateStatisticsAsync(startDate, endDate, model, operatorName, result, workOrder, productCode);
            DateStatGrid.ItemsSource = dateStats;

            // 按型号统计
            var modelStats = await _dbService.GetModelStatisticsAsync(startDate, endDate, model, operatorName, result, workOrder, productCode);
            ModelStatGrid.ItemsSource = modelStats;

            // 失效原因统计
            var failReasons = await _dbService.GetFailReasonStatisticsAsync(startDate, endDate, model, operatorName, workOrder, productCode);
            FailReasonGrid.ItemsSource = failReasons;
        }

        private void UpdatePageInfo()
        {
            TxtPageInfo.Text = $"第 {_currentPage} 页 / 共 {_totalPages} 页";

            BtnFirstPage.IsEnabled = _currentPage > 1;
            BtnPrevPage.IsEnabled = _currentPage > 1;
            BtnNextPage.IsEnabled = _currentPage < _totalPages;
            BtnLastPage.IsEnabled = _currentPage < _totalPages;
        }

        private async void BtnQuery_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = 1;
            await QueryData();
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Title = "导出测试记录",
                Filter = "CSV文件|*.csv|Excel文件|*.xlsx",
                FileName = $"测试记录_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await ExportData(saveFileDialog.FileName);
                    MessageBox.Show("导出成功!", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task ExportData(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLower();

            if (extension == ".csv")
            {
                await ExportToCsv(filePath);
            }
            else if (extension == ".xlsx")
            {
                await ExportToExcel(filePath);
            }
        }

        private async Task ExportToCsv(string filePath)
        {
            var startDate = StartDatePicker.SelectedDate ?? DateTime.Now.AddDays(-7);
            var endDate = EndDatePicker.SelectedDate ?? DateTime.Now;
            var model = (CmbProductModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var operatorName = (CmbOperator.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var result = (CmbTestResult.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var workOrder = TxtWorkOrder.Text.Trim();

            var productCode = TxtProductCode.Text.Trim();
            var allRecords = await _dbService.GetAllTestRecordsAsync(startDate, endDate, model, operatorName, result, workOrder, productCode);

            var csv = new StringBuilder();
            csv.AppendLine("序号,测试时间,工单号,产品型号,产品代码,通道,测试结果,失败原因,操作员,测试时长(s)");

            foreach (var record in allRecords)
            {

                csv.AppendLine($"{record.Id},{record.TestTime:yyyy-MM-dd HH:mm:ss},{record.WorkOrder},{record.ProductModel},{record.ProductCode},{record.Channel},{record.Result},\"{record.FailReason}\",{record.Operator},{record.TestDuration:F1}");
            }

            await File.WriteAllTextAsync(filePath, csv.ToString(), Encoding.UTF8);
        }

        private async Task ExportToExcel(string filePath)
        {
            // 这里可以使用 EPPlus 或其他 Excel 库来生成 Excel 文件
            // 为了简化，这里直接生成 CSV 后改扩展名
            await ExportToCsv(filePath.Replace(".xlsx", ".csv"));
        }

        // 分页控制
        private async void BtnFirstPage_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = 1;
            await LoadCurrentPageData();
        }

        private async void BtnPrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await LoadCurrentPageData();
            }
        }

        private async void BtnNextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                await LoadCurrentPageData();
            }
        }

        private async void BtnLastPage_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = _totalPages;
            await LoadCurrentPageData();
        }

        private async void CmbPageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPageSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int newPageSize))
            {
                _pageSize = newPageSize;
                _currentPage = 1;
                await LoadCurrentPageData();
            }
        }

        private async Task LoadCurrentPageData()
        {
            var startDate = StartDatePicker.SelectedDate ?? DateTime.Now.AddDays(-7);
            var endDate = EndDatePicker.SelectedDate ?? DateTime.Now;
            var model = (CmbProductModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var operatorName = (CmbOperator.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var result = (CmbTestResult.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var workOrder = TxtWorkOrder.Text.Trim();
            var productCode = TxtProductCode.Text.Trim();

            await LoadTestRecords(startDate, endDate, model, operatorName, result, workOrder, productCode);
        }
    }

    // 统计数据模型
    public class TestStatistics
    {
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public double AvgTestDuration { get; set; }
    }

    public class DateStatistic
    {
        public DateTime Date { get; set; }
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public double PassRate => TotalCount > 0 ? (double)PassCount / TotalCount : 0;
    }

    public class ModelStatistic
    {
        public string ProductModel { get; set; }
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public double PassRate => TotalCount > 0 ? (double)PassCount / TotalCount : 0;
    }

    public class FailReasonStatistic
    {
        public string FailReason { get; set; }
        public int Count { get; set; }
        public double Percentage { get; set; }
    }
}
