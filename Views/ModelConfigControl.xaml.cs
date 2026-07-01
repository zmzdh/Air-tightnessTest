// UserControls/ModelConfigControl.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Services;
using System.IO;
using Newtonsoft.Json;

namespace AudioActuatorCanTest.UserControls
{
    public partial class ModelConfigControl : UserControl
    {
        private readonly ConfigService _configService;
        private List<ProductModel> _productModels = new List<ProductModel>();
        private ProductModel _currentModel;

        public ModelConfigControl(ConfigService configService)
        {
            InitializeComponent();
            _configService = configService;
            LoadProductModels();
            DataContext = this;
        }

        private async void LoadProductModels()
        {
            try
            {
                _productModels = await _configService.LoadProductModelsAsync();
                ModelListBox.ItemsSource = _productModels.Select(m => m.ModelName).ToList();

                if (_productModels.Count > 0)
                {
                    ModelListBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载产品型号失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ModelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModelListBox.SelectedIndex >= 0 && ModelListBox.SelectedIndex < _productModels.Count)
            {
                _currentModel = _productModels[ModelListBox.SelectedIndex];
                await LoadModelConfigAsync();
            }
        }

        private async System.Threading.Tasks.Task LoadModelConfigAsync()
        {
            if (_currentModel == null)
            {
                return;
            }

            TxtModelName.Text = _currentModel.ModelName;
            TxtModelDesc.Text = _currentModel.Description;
            TxtImagePath.Text = _currentModel.ImagePath;

            if (!string.IsNullOrEmpty(_currentModel.ImagePath) && File.Exists(_currentModel.ImagePath))
            {
                ModelImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_currentModel.ImagePath));
            }
            else
            {
                ModelImage.Source = null;
            }

            var process = _currentModel.ProcessConfig ?? new TestProcessConfig();
            ChkEnableBarcode.IsChecked = process.EnableBarcodeCheck;
            ChkEnableCurrentMonitor.IsChecked = process.EnableCurrentMonitoring;
            ChkRecordCurrentBeforeStart.IsChecked = process.RecordCurrentBeforeStart;
            ChkEnableLumbarTest.IsChecked = process.EnableLumbarTest;
            ChkEnableMassageTest.IsChecked = process.EnableMassageTest;
            ChkCheckSameModel.IsChecked = process.CheckSameModel;
            ChkPromptOnDuplicate.IsChecked = process.PromptOnDuplicateBarcode;
            ChkMeasureSleepCurrent.IsChecked = process.MeasureSleepCurrent;
            ChkMeasureStaticCurrent.IsChecked = process.MeasureStaticCurrent;
            TxtMaxTestCount.Text = process.MaxTestCount.ToString();
            TxtPowerOffDuration.Text = process.ModeSwitchPowerOffDuration.ToString();

            _currentModel.Channel1Config ??= new ChannelConfig { ChannelName = "左通道" };
            var left = _currentModel.Channel1Config;
            TxtLeftStaticMin.Text = left.StaticCurrentMin.ToString();
            TxtLeftStaticMax.Text = left.StaticCurrentMax.ToString();
            TxtLeftWorkMin.Text = left.WorkCurrentMin.ToString();
            TxtLeftWorkMax.Text = left.WorkCurrentMax.ToString();
            TxtLeftCurrentOver.Text = left.CurrentOverLimit.ToString();
            TxtLeftSleepThreshold.Text = left.SleepCurrentThreshold.ToString();
            TxtLeftSleepTimeout.Text = left.SleepTestTimeout.ToString();

            _currentModel.Channel2Config ??= new ChannelConfig { ChannelName = "右通道" };
            var right = _currentModel.Channel2Config;
            TxtRightStaticMin.Text = right.StaticCurrentMin.ToString();
            TxtRightStaticMax.Text = right.StaticCurrentMax.ToString();
            TxtRightWorkMin.Text = right.WorkCurrentMin.ToString();
            TxtRightWorkMax.Text = right.WorkCurrentMax.ToString();
            TxtRightCurrentOver.Text = right.CurrentOverLimit.ToString();
            TxtRightSleepThreshold.Text = right.SleepCurrentThreshold.ToString();
            TxtRightSleepTimeout.Text = right.SleepTestTimeout.ToString();

            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void SaveCurrentModel()
        {
            if (_currentModel == null) return;

            try
            {
                _currentModel.ModelName = TxtModelName.Text;
                _currentModel.Description = TxtModelDesc.Text;
                _currentModel.ImagePath = TxtImagePath.Text;

                var process = _currentModel.ProcessConfig ?? new TestProcessConfig();
                process.EnableBarcodeCheck = ChkEnableBarcode.IsChecked == true;
                process.EnableCurrentMonitoring = ChkEnableCurrentMonitor.IsChecked == true;
                process.RecordCurrentBeforeStart = ChkRecordCurrentBeforeStart.IsChecked == true;
                process.EnableLumbarTest = ChkEnableLumbarTest.IsChecked != false;
                process.EnableMassageTest = ChkEnableMassageTest.IsChecked != false;
                process.CheckSameModel = ChkCheckSameModel.IsChecked == true;
                process.PromptOnDuplicateBarcode = ChkPromptOnDuplicate.IsChecked == true;
                process.MeasureSleepCurrent = ChkMeasureSleepCurrent.IsChecked == true;
                process.MeasureStaticCurrent = ChkMeasureStaticCurrent.IsChecked == true;
                process.MaxTestCount = int.Parse(TxtMaxTestCount.Text);
                process.ModeSwitchPowerOffDuration = int.Parse(TxtPowerOffDuration.Text);
                _currentModel.ProcessConfig = process;

                _currentModel.Channel1Config ??= new ChannelConfig { ChannelName = "左通道" };
                _currentModel.Channel2Config ??= new ChannelConfig { ChannelName = "右通道" };

                var left = _currentModel.Channel1Config;
                left.StaticCurrentMin = double.Parse(TxtLeftStaticMin.Text);
                left.StaticCurrentMax = double.Parse(TxtLeftStaticMax.Text);
                left.WorkCurrentMin = double.Parse(TxtLeftWorkMin.Text);
                left.WorkCurrentMax = double.Parse(TxtLeftWorkMax.Text);
                left.CurrentOverLimit = double.Parse(TxtLeftCurrentOver.Text);
                left.SleepCurrentThreshold = double.Parse(TxtLeftSleepThreshold.Text);
                left.SleepTestTimeout = int.Parse(TxtLeftSleepTimeout.Text);
                left.LumbarTestConfigs = new List<LumbarTestConfig>();
                left.MassageConfigs = new List<MassageConfig>();
                left.MassageTestSettings = new MassageTestSettings();
                left.MessageConfig = new MessageConfig();

                var right = _currentModel.Channel2Config;
                right.StaticCurrentMin = double.Parse(TxtRightStaticMin.Text);
                right.StaticCurrentMax = double.Parse(TxtRightStaticMax.Text);
                right.WorkCurrentMin = double.Parse(TxtRightWorkMin.Text);
                right.WorkCurrentMax = double.Parse(TxtRightWorkMax.Text);
                right.CurrentOverLimit = double.Parse(TxtRightCurrentOver.Text);
                right.SleepCurrentThreshold = double.Parse(TxtRightSleepThreshold.Text);
                right.SleepTestTimeout = int.Parse(TxtRightSleepTimeout.Text);
                right.LumbarTestConfigs = new List<LumbarTestConfig>();
                right.MassageConfigs = new List<MassageConfig>();
                right.MassageTestSettings = new MassageTestSettings();
                right.MessageConfig = new MessageConfig();
            }
            catch (Exception ex)
            {
                throw new Exception($"保存配置数据失败: {ex.Message}");
            }
        }

        private async void BtnSaveModel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("请先选择型号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SaveCurrentModel();
                await _configService.SaveProductModelsAsync(_productModels);
                MessageBox.Show("型号配置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnReloadModel_Click(object sender, RoutedEventArgs e)
        {
            await LoadModelConfigAsync();
        }

        private void BtnAddModel_Click(object sender, RoutedEventArgs e)
        {
            var newModel = new ProductModel
            {
                ModelName = $"新型号{_productModels.Count + 1}",
                Description = string.Empty,
                Channel1Config = new ChannelConfig { ChannelName = "左通道" },
                Channel2Config = new ChannelConfig { ChannelName = "右通道" },
                ProcessConfig = new TestProcessConfig()
            };

            _productModels.Add(newModel);
            ModelListBox.ItemsSource = _productModels.Select(m => m.ModelName).ToList();
            ModelListBox.SelectedIndex = _productModels.Count - 1;
        }

        private async void BtnDeleteModel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("请先选择型号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show("确定删除该型号?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _productModels.Remove(_currentModel);
                await _configService.SaveProductModelsAsync(_productModels);
                LoadProductModels();
            }
        }

        private void BtnSelectImage_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "选择型号图片",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtImagePath.Text = openFileDialog.FileName;
                ModelImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(openFileDialog.FileName));
            }
        }

        private void BtnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("请先选择型号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SaveCurrentModel();

                var saveFileDialog = new SaveFileDialog
                {
                    Title = "导出配置文件",
                    Filter = "JSON文件|*.json",
                    FileName = $"{_currentModel.ModelName}_配置.json"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    var json = JsonConvert.SerializeObject(_currentModel, Formatting.Indented);
                    File.WriteAllText(saveFileDialog.FileName, json);
                    MessageBox.Show("配置文件导出成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("请先选择型号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "导入配置文件",
                    Filter = "JSON文件|*.json"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(openFileDialog.FileName);
                    var importedModel = JsonConvert.DeserializeObject<ProductModel>(json);

                    if (importedModel != null)
                    {
                        _currentModel.ModelName = importedModel.ModelName;
                        _currentModel.Description = importedModel.Description;
                        _currentModel.ImagePath = importedModel.ImagePath;
                        _currentModel.ProcessConfig = importedModel.ProcessConfig;
                        _currentModel.Channel1Config = importedModel.Channel1Config;
                        _currentModel.Channel2Config = importedModel.Channel2Config;

                        await LoadModelConfigAsync();
                        MessageBox.Show("配置文件导入成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("导入的配置文件格式不正确", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
