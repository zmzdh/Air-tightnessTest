// UserControls/TestControl.xaml.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows.Threading;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.UserControls
{
    public partial class TestControl : UserControl
    {
        private readonly TestService _testService;
        private readonly ConfigService _configService;
        private readonly DatabaseService _dbService;
        private readonly MesService _mesService;
        private readonly ModbusServerService _modbusService;
        private readonly LicenseService _licenseService;
        private readonly User _currentUser;

        private List<ProductModel> _productModels;
        private ProductModel _selectedModel;
        private AppConfig _appConfig;
        private string _pendingSelectedModelName;

        public ProductModel? SelectedModel => _selectedModel;

        public event Action<ProductModel?>? SelectedModelChanged;

        private int _testCount;
        private int _passCount;
        private int _failCount;
        private int _targetProduction;

        private const string DefaultStatusMessage = "等待开始测试";

        private readonly Dictionary<int, ObservableCollection<TestStageItem>> _channelStages = new();
        private readonly Dictionary<int, bool> _channelRunning = new() { { 1, false }, { 2, false }, { 3, false }, { 4, false } };
        private readonly Dictionary<int, TestResult> _lastChannelResults = new();
        private readonly Dictionary<int, bool> _channelCurrentWasBelowResetThreshold = new();
        private readonly Dictionary<int, ObservableCollection<MassagePointIndicator>> _channelMassagePoints = new();
        private readonly Dictionary<int, ProgressBar> _lumbarBars = new();
        private readonly Dictionary<int, TextBlock> _lumbarValueLabels = new();
        private readonly Dictionary<int, ItemsControl> _massagePointViews = new();
        private readonly Dictionary<int, FrameworkElement> _lumbarPanels = new();
        private readonly Dictionary<int, FrameworkElement> _massagePanels = new();
        private readonly Dictionary<int, FrameworkElement> _graphicPlaceholders = new();
        private readonly Dictionary<int, GraphicRetention> _graphicRetentions = new();
        private readonly Dictionary<int, ChannelDailyProduction> _channelDailyStats = new();
        private readonly Dictionary<int, DateTime> _channelStartTimes = new();
        private readonly Dictionary<int, ChannelInfoWidgets> _channelInfoWidgets = new();
        private readonly DispatcherTimer _elapsedTimer;

        private static readonly TimeSpan BarcodeInputResetThreshold = TimeSpan.FromMilliseconds(500);
        private DateTime _lastProductCodeInputTime = DateTime.MinValue;
        private bool _duplicateBarcodeBlocked;
        private bool _mesEventSubscribed;
        private bool _modbusEventSubscribed;

        public TestControl(TestService testService, ConfigService configService, DatabaseService dbService, MesService mesService, ModbusServerService modbusService, LicenseService licenseService, User currentUser)
        {
            InitializeComponent();

            _testService = testService ?? throw new ArgumentNullException(nameof(testService));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _mesService = mesService ?? throw new ArgumentNullException(nameof(mesService));
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));
            _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
            _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += ElapsedTimer_Tick;

            InitializeControls();
            LoadProductModels();
            SubscribeToEvents();
            Loaded += TestControl_Loaded;
            Unloaded += TestControl_Unloaded;
        }

        private async void TestControl_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= TestControl_Loaded;
            if (!_mesEventSubscribed)
            {
                _mesService.OnConnectionChanged += MesService_OnConnectionChanged;
                _mesEventSubscribed = true;
            }
            if (!_modbusEventSubscribed)
            {
                _modbusService.OnServerStateChanged += ModbusService_OnServerStateChanged;
                _modbusEventSubscribed = true;
            }
            await LoadPersistedSettingsAsync();
            UpdateMesIntegrationUI();
        }

        private void TestControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_mesEventSubscribed)
            {
                _mesService.OnConnectionChanged -= MesService_OnConnectionChanged;
                _mesEventSubscribed = false;
            }

            if (_modbusEventSubscribed)
            {
                _modbusService.OnServerStateChanged -= ModbusService_OnServerStateChanged;
                _modbusEventSubscribed = false;
            }

            Loaded += TestControl_Loaded;
        }

        private void InitializeControls()
        {
            TxtOperator.Text = _currentUser.Username;

            _channelStages[1] = CreateStageCollection();
            _channelStages[2] = CreateStageCollection();
            _channelStages[3] = CreateStageCollection();
            _channelStages[4] = CreateStageCollection();

            LeftStageList.ItemsSource = _channelStages[1];
            RightStageList.ItemsSource = _channelStages[2];
            Ch3StageList.ItemsSource = _channelStages[3];
            Ch4StageList.ItemsSource = _channelStages[4];

            InitializeGraphicPanels();
            InitializeChannelInfoPanels();
            SetChannelStatusMessage(null, DefaultStatusMessage);
            UpdateAllChannelButtons();
            UpdateMesIntegrationUI();
        }

        private void InitializeGraphicPanels()
        {
            _lumbarBars[1] = Ch1LumbarBar;
            _lumbarBars[2] = Ch2LumbarBar;
            _lumbarBars[3] = Ch3LumbarBar;
            _lumbarBars[4] = Ch4LumbarBar;

            _lumbarValueLabels[1] = Ch1LumbarValue;
            _lumbarValueLabels[2] = Ch2LumbarValue;
            _lumbarValueLabels[3] = Ch3LumbarValue;
            _lumbarValueLabels[4] = Ch4LumbarValue;

            _massagePointViews[1] = Ch1MassagePoints;
            _massagePointViews[2] = Ch2MassagePoints;
            _massagePointViews[3] = Ch3MassagePoints;
            _massagePointViews[4] = Ch4MassagePoints;

            _lumbarPanels[1] = Ch1LumbarPanel;
            _lumbarPanels[2] = Ch2LumbarPanel;
            _lumbarPanels[3] = Ch3LumbarPanel;
            _lumbarPanels[4] = Ch4LumbarPanel;

            _massagePanels[1] = Ch1MassagePanel;
            _massagePanels[2] = Ch2MassagePanel;
            _massagePanels[3] = Ch3MassagePanel;
            _massagePanels[4] = Ch4MassagePanel;

            _graphicPlaceholders[1] = Ch1GraphicPlaceholder;
            _graphicPlaceholders[2] = Ch2GraphicPlaceholder;
            _graphicPlaceholders[3] = Ch3GraphicPlaceholder;
            _graphicPlaceholders[4] = Ch4GraphicPlaceholder;

            foreach (int channel in new[] { 1, 2, 3, 4 })
            {
                _graphicRetentions[channel] = GraphicRetention.None;
                var points = new ObservableCollection<MassagePointIndicator>(
                    Enumerable.Range(1, 32).Select(index => new MassagePointIndicator(index)));
                _channelMassagePoints[channel] = points;
                _massagePointViews[channel].ItemsSource = points;
                UpdateGraphicVisibility(channel, showLumbar: false, showMassage: false);
            }
        }

        private void InitializeChannelInfoPanels()
        {
            _channelInfoWidgets[1] = new ChannelInfoWidgets(Ch1ElapsedPanel, Ch1ElapsedText, Ch1CountPanel,
                Ch1TestCount, Ch1PassCount, Ch1FailCount, Ch1PassBarColumn, Ch1FailBarColumn);
            _channelInfoWidgets[2] = new ChannelInfoWidgets(Ch2ElapsedPanel, Ch2ElapsedText, Ch2CountPanel,
                Ch2TestCount, Ch2PassCount, Ch2FailCount, Ch2PassBarColumn, Ch2FailBarColumn);
            _channelInfoWidgets[3] = new ChannelInfoWidgets(Ch3ElapsedPanel, Ch3ElapsedText, Ch3CountPanel,
                Ch3TestCount, Ch3PassCount, Ch3FailCount, Ch3PassBarColumn, Ch3FailBarColumn);
            _channelInfoWidgets[4] = new ChannelInfoWidgets(Ch4ElapsedPanel, Ch4ElapsedText, Ch4CountPanel,
                Ch4TestCount, Ch4PassCount, Ch4FailCount, Ch4PassBarColumn, Ch4FailBarColumn);
        }

        private static string GetChannelDisplayName(int channel) => channel switch
        {
            1 => "通道1",
            2 => "通道2",
            _ => $"通道{channel}"
        };

        private void SetChannelStatusMessage(int? channel, string message)
        {
            var status = string.IsNullOrWhiteSpace(message) ? DefaultStatusMessage : message;

            if (channel == 1)
            {
                TxtLeftCurrentStep.Text = status;
            }
            else if (channel == 2)
            {
                TxtRightCurrentStep.Text = status;
            }
            else if (channel == 3)
            {
                TxtCh3CurrentStep.Text = status;
            }
            else if (channel == 4)
            {
                TxtCh4CurrentStep.Text = status;
            }
            else
            {
                TxtLeftCurrentStep.Text = status;
                TxtRightCurrentStep.Text = status;
                TxtCh3CurrentStep.Text = status;
                TxtCh4CurrentStep.Text = status;
            }
        }

        private async Task LoadPersistedSettingsAsync()
        {
            try
            {
                _appConfig = await _configService.LoadAppConfigAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载应用配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                _appConfig = new AppConfig();
            }

            string workOrder = _appConfig?.LastWorkOrder;
            if (string.IsNullOrWhiteSpace(workOrder))
            {
                workOrder = GenerateDefaultWorkOrder();
                if (_appConfig != null)
                {
                    _appConfig.LastWorkOrder = workOrder;
                    try
                    {
                        await _configService.SaveAppConfigAsync(_appConfig);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存默认工单失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            TxtWorkOrder.Text = workOrder;

            _pendingSelectedModelName = _appConfig?.LastProductModel;
            ApplyPersistedModelSelection();
            InitializeProductionState();
            ApplyTargetProductionAuthorization();

            UpdateMesIntegrationUI();
            ApplyChannelAvailability();

            if (!TxtProductCode.IsFocused)
            {
                TxtProductCode.Focus();
            }
        }

        private void ApplyChannelAvailability()
        {
            int channelCount = GetConfiguredChannelCount();
            bool isTwoChannelMode = channelCount == 2;
            bool isFourChannelMode = channelCount == 4;

            BtnStartLeft.IsEnabled = !_channelRunning[1];
            BtnStopLeft.IsEnabled = _channelRunning[1];
            BtnStartRight.IsEnabled = !_channelRunning[2];
            BtnStopRight.IsEnabled = _channelRunning[2];

            bool ch3Visible = channelCount >= 3;
            bool ch4Visible = channelCount >= 4;

            BtnStartCh3.IsEnabled = ch3Visible && !_channelRunning[3];
            BtnStopCh3.IsEnabled = ch3Visible && _channelRunning[3];
            BtnStartCh4.IsEnabled = ch4Visible && !_channelRunning[4];
            BtnStopCh4.IsEnabled = ch4Visible && _channelRunning[4];

            BtnStartCh3.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            BtnStopCh3.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            BtnStartCh4.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            BtnStopCh4.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch3StatusBanner.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch3StageList.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch4StatusBanner.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch4StageList.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch3CountPanel.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch3ElapsedPanel.Visibility = Visibility.Collapsed;
            Ch4CountPanel.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch4ElapsedPanel.Visibility = Visibility.Collapsed;
            Ch3LumbarPanel.Visibility = Visibility.Collapsed;
            Ch4LumbarPanel.Visibility = Visibility.Collapsed;
            Ch3MassagePanel.Visibility = Visibility.Collapsed;
            Ch4MassagePanel.Visibility = Visibility.Collapsed;
            Ch3GraphicPlaceholder.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch4GraphicPlaceholder.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            ModelImageSection.Visibility = isTwoChannelMode ? Visibility.Collapsed : Visibility.Visible;

            ApplyChannelGridLayout(ChannelActionGrid, channelCount);
            ApplyChannelGridLayout(ChannelStatusGrid, channelCount);
            ApplyChannelGridLayout(ChannelSummaryGrid, channelCount);
            ApplyChannelGridLayout(ChannelGraphicGrid, channelCount);
            ApplyChannelStepGridLayout(channelCount);
        }

        private static void ApplyChannelGridLayout(Grid grid, int channelCount)
        {
            if (grid.ColumnDefinitions.Count < 4) return;

            if (channelCount == 2)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[2].Width = new GridLength(0);
                grid.ColumnDefinitions[3].Width = new GridLength(0);
                return;
            }

            if (channelCount == 3)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[3].Width = new GridLength(0);
                return;
            }

            // 4 channels
            for (int i = 0; i < 4; i++)
            {
                grid.ColumnDefinitions[i].Width = new GridLength(1, GridUnitType.Star);
            }
        }

        private void ApplyChannelStepGridLayout(int channelCount)
        {
            if (ChannelStepGrid.ColumnDefinitions.Count < 7) return;

            bool ch3Visible = channelCount >= 3;
            bool ch4Visible = channelCount >= 4;

            TxtCh3CurrentStep.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            TxtCh4CurrentStep.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;

            ChannelStepGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            ChannelStepGrid.ColumnDefinitions[1].Width = GridLength.Auto;
            ChannelStepGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            ChannelStepGrid.ColumnDefinitions[3].Width = ch3Visible ? GridLength.Auto : new GridLength(0);
            ChannelStepGrid.ColumnDefinitions[4].Width = ch3Visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ChannelStepGrid.ColumnDefinitions[5].Width = ch4Visible ? GridLength.Auto : new GridLength(0);
            ChannelStepGrid.ColumnDefinitions[6].Width = ch4Visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        private int GetConfiguredChannelCount()
        {
            var count = _appConfig?.ChannelCount ?? 4;
            if (count == 2 || count == 3) return count;
            return 4;
        }

        private void ApplyPersistedModelSelection()
        {
            if (_productModels == null || _productModels.Count == 0)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_pendingSelectedModelName))
            {
                var match = _productModels
                    .FirstOrDefault(m => string.Equals(m?.ModelName, _pendingSelectedModelName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    CmbProductModel.SelectedItem = match;
                    return;
                }
            }

            if (CmbProductModel.SelectedIndex < 0)
            {
                CmbProductModel.SelectedIndex = 0;
            }

            _selectedModel = CmbProductModel.SelectedItem as ProductModel;
            UpdateModelImage();
        }

        private void UpdateModelImage()
        {
            if (_selectedModel == null)
            {
                ImgModelPreview.Source = null;
                return;
            }

            var path = _selectedModel.ImagePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                ImgModelPreview.Source = null;
                return;
            }

            string resolvedPath = path;
            if (!Path.IsPathFullyQualified(resolvedPath))
            {
                resolvedPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, resolvedPath));
            }

            if (!File.Exists(resolvedPath))
            {
                ImgModelPreview.Source = null;
                return;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(resolvedPath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();

                ImgModelPreview.Source = image;
            }
            catch (Exception ex)
            {
                ImgModelPreview.Source = null;
                SetChannelStatusMessage(null, $"机型图片加载失败: {ex.Message}");
            }
        }

        private static string GenerateDefaultWorkOrder()
        {
            return $"CSAS{DateTime.Now:yyyyMM}";
        }

        private ObservableCollection<TestStageItem> CreateStageCollection()
        {
            var stages = new ObservableCollection<TestStageItem>();
            foreach (var stage in Enum.GetValues<TestStage>())
            {
                stages.Add(new TestStageItem(stage, GetStageDisplayName(stage)));
            }
            return stages;
        }

        private async void LoadProductModels()
        {
            try
            {
                var models = await _configService.LoadProductModelsAsync();
                _productModels = models?.Where(m => m != null).ToList() ?? new List<ProductModel>();
                ApplyModelDefaults(_productModels);
                CmbProductModel.ItemsSource = _productModels;
                ApplyPersistedModelSelection();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载产品型号失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MesService_OnConnectionChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(UpdateMesIntegrationUI);
        }

        private void ModbusService_OnServerStateChanged(object sender, bool isRunning)
        {
            Dispatcher.Invoke(UpdateMesIntegrationUI);
        }

        private void SubscribeToEvents()
        {
            _testService.OnTestStageChanged += TestService_OnTestStageChanged;
            _testService.OnTestCompleted += TestService_OnTestCompleted;
            _testService.OnTestMessage += TestService_OnTestMessage;
            _testService.OnTestResultDisplay += TestService_OnTestResultDisplay;
        }

        private void TestService_OnTestStageChanged(object sender, TestStageChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.Stage == TestStage.StartTest && e.State == StepExecutionState.Running)
                {
                    ClearGraphicRetention(e.Channel);
                }
                if (e.State == StepExecutionState.Failed)
                {
                    if (e.Stage == TestStage.LumbarTest)
                    {
                        SetGraphicRetention(e.Channel, GraphicRetention.Lumbar);
                    }
                    else if (e.Stage == TestStage.MassageTest || e.Stage == TestStage.MasterModeMassage)
                    {
                        SetGraphicRetention(e.Channel, GraphicRetention.Massage);
                    }
                }

                if (_channelStages.TryGetValue(e.Channel, out var stages))
                {
                    var stageItem = stages.FirstOrDefault(s => s.Stage == e.Stage);
                    if (stageItem != null)
                    {
                        stageItem.State = e.State;
                        stageItem.Message = e.Message;
                    }
                }

                var channelName = GetChannelDisplayName(e.Channel);
                SetChannelStatusMessage(e.Channel,
                    $"{channelName} - {GetStageDisplayName(e.Stage)} ({GetStateDescription(e.State)})");

                bool showLumbar = e.Stage == TestStage.LumbarTest && e.State == StepExecutionState.Running;
                bool showMassage = (e.Stage == TestStage.MassageTest || e.Stage == TestStage.MasterModeMassage)
                    && e.State == StepExecutionState.Running;
                UpdateGraphicVisibility(e.Channel, showLumbar, showMassage);
            });
        }

        private void UpdateGraphicVisibility(int channel, bool showLumbar, bool showMassage)
        {
            if (!_lumbarPanels.TryGetValue(channel, out var lumbarPanel)
                || !_massagePanels.TryGetValue(channel, out var massagePanel)
                || !_graphicPlaceholders.TryGetValue(channel, out var placeholder))
            {
                return;
            }

            if (!showLumbar && !showMassage
                && _graphicRetentions.TryGetValue(channel, out var retention)
                && retention != GraphicRetention.None)
            {
                showLumbar = retention == GraphicRetention.Lumbar;
                showMassage = retention == GraphicRetention.Massage;
            }

            lumbarPanel.Visibility = showLumbar ? Visibility.Visible : Visibility.Collapsed;
            massagePanel.Visibility = showMassage ? Visibility.Visible : Visibility.Collapsed;
            placeholder.Visibility = (!showLumbar && !showMassage) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetGraphicRetention(int channel, GraphicRetention retention)
        {
            _graphicRetentions[channel] = retention;
        }

        private void ClearGraphicRetention(int channel)
        {
            _graphicRetentions[channel] = GraphicRetention.None;
        }

        public void UpdateWithPLCData(PLCData data)
        {
            if (data == null)
            {
                return;
            }

            UpdateChannelGraphics(1, data.Channel1);
            UpdateChannelGraphics(2, data.Channel2);
            UpdateChannelGraphics(3, data.Channel3);
            UpdateChannelGraphics(4, data.Channel4);

            TryResetIdleBannerByCurrentTransition(1, data.Channel1);
            TryResetIdleBannerByCurrentTransition(2, data.Channel2);
            TryResetIdleBannerByCurrentTransition(3, data.Channel3);
            TryResetIdleBannerByCurrentTransition(4, data.Channel4);
        }

        private void UpdateChannelGraphics(int channel, ChannelData channelData)
        {
            if (channelData == null)
            {
                return;
            }

            if (_lumbarBars.TryGetValue(channel, out var bar))
            {
                var (rangeMin, rangeMax) = GetChannelHeightRange(channel);
                if (!rangeMin.Equals(bar.Minimum))
                {
                    bar.Minimum = rangeMin;
                }

                if (!rangeMax.Equals(bar.Maximum))
                {
                    bar.Maximum = rangeMax;
                }

                double displayHeight = ConvertChannelHeightToMillimeters(channel, channelData.HeightRawValue);
                double heightValue = Math.Clamp(displayHeight, rangeMin, rangeMax);
                bar.Value = heightValue;
                if (_lumbarValueLabels.TryGetValue(channel, out var label))
                {
                    label.Text = $"{displayHeight:F1} mm";
                }
            }

            if (_channelMassagePoints.TryGetValue(channel, out var points))
            {
                var states = channelData.MassagePoints ?? Array.Empty<bool>();
                int count = Math.Min(states.Length, points.Count);
                for (int i = 0; i < count; i++)
                {
                    points[i].IsActive = states[i];
                }
            }
        }

        private void TryResetIdleBannerByCurrentTransition(int channel, ChannelData channelData)
        {
            if (channelData == null
                || !_lastChannelResults.ContainsKey(channel)
                || (_channelRunning.TryGetValue(channel, out var isRunning) && isRunning))
            {
                return;
            }

            double currentMilliAmps = channelData.CurrentValue;
            if (currentMilliAmps < 1.0)
            {
                _channelCurrentWasBelowResetThreshold[channel] = true;
                return;
            }

            if (currentMilliAmps <= 10.0
                || !_channelCurrentWasBelowResetThreshold.TryGetValue(channel, out var wasBelowThreshold)
                || !wasBelowThreshold)
            {
                return;
            }

            _lastChannelResults.Remove(channel);
            _channelCurrentWasBelowResetThreshold[channel] = false;

            var (banner, textBlock) = GetChannelBannerElements(channel);
            if (banner != null && textBlock != null)
            {
                ApplyIdleBannerState(channel, banner, textBlock);
            }
        }

        private (double Min, double Max) GetChannelHeightRange(int channel)
        {
            ChannelConfig? config = channel switch
            {
                1 => _selectedModel?.Channel1Config,
                2 => _selectedModel?.Channel2Config,
                3 => _selectedModel?.Channel3Config,
                4 => _selectedModel?.Channel4Config,
                _ => null
            };

            double min = config?.HeightRangeMin ?? 0;
            double max = config?.HeightRangeMax ?? 50;

            if (double.IsNaN(min) || double.IsInfinity(min))
            {
                min = 0;
            }

            if (double.IsNaN(max) || double.IsInfinity(max))
            {
                max = 50;
            }

            return min <= max ? (min, max) : (max, min);
        }

        private double ConvertChannelHeightToMillimeters(int channel, int rawHeight)
        {
            ChannelConfig? config = channel switch
            {
                1 => _selectedModel?.Channel1Config,
                2 => _selectedModel?.Channel2Config,
                3 => _selectedModel?.Channel3Config,
                4 => _selectedModel?.Channel4Config,
                _ => null
            };

            if (config == null || config.HeightCodeMax == config.HeightCodeMin)
            {
                return rawHeight / 100.0;
            }

            double codeSpan = config.HeightCodeMax - config.HeightCodeMin;
            double normalized = (rawHeight - config.HeightCodeMin) / codeSpan;
            double mapped = config.HeightRangeMin + normalized * (config.HeightRangeMax - config.HeightRangeMin);
            double minRange = Math.Min(config.HeightRangeMin, config.HeightRangeMax);
            double maxRange = Math.Max(config.HeightRangeMin, config.HeightRangeMax);
            return Math.Clamp(mapped, minRange, maxRange);
        }

        private void TestService_OnTestCompleted(object sender, TestRecord record)
        {
            Dispatcher.Invoke(() =>
            {
                EnsureDailyProductionState();
                UpdateChannelDailyStats(record);
                UpdateChannelFinalState(record);
                DisplayTestResult(record);

                _channelRunning[record.Channel] = false;
                _channelStartTimes.Remove(record.Channel);
                UpdateChannelButtons(record.Channel);
                UpdateChannelInfoDisplay(record.Channel);
                UpdateOverallProductionDisplay();
                _ = SaveAppConfigAsync();
            });
        }

        private void TestService_OnTestMessage(object sender, TestMessageEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (e.Channel.HasValue)
                {
                    SetChannelStatusMessage(e.Channel.Value, e.Message);
                }
                else
                {
                    SetChannelStatusMessage(null, e.Message);
                }
            });
        }

        private void TestService_OnTestResultDisplay(object sender, ChannelTestResultEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var banner = e.Channel switch
                {
                    1 => LeftStatusBanner,
                    2 => RightStatusBanner,
                    3 => Ch3StatusBanner,
                    4 => Ch4StatusBanner,
                    _ => null
                };
                var textBlock = e.Channel switch
                {
                    1 => TxtLeftStatus,
                    2 => TxtRightStatus,
                    3 => TxtCh3Status,
                    4 => TxtCh4Status,
                    _ => null
                };

                if (banner == null || textBlock == null)
                {
                    return;
                }

                SetBannerState(banner, textBlock, e.IsOk ? "OK" : "NG", e.IsOk ? Colors.ForestGreen : Colors.Firebrick);
            });
        }

        private void CmbProductModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedModel = CmbProductModel.SelectedItem as ProductModel;
            if (_appConfig != null)
            {
                _appConfig.LastProductModel = _selectedModel?.ModelName ?? string.Empty;
            }

            UpdateModelImage();
            SelectedModelChanged?.Invoke(_selectedModel);
        }

        private async void BtnMesConnect_Click(object sender, RoutedEventArgs e)
        {
            AppConfig config;
            try
            {
                config = await _configService.LoadAppConfigAsync();
                _appConfig = config;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载应用配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            config ??= new AppConfig();
            var mode = config.MesIntegrationMode;

            if (!ValidateMesConfiguration(config, mode, out string errorMessage))
            {
                MessageBox.Show(errorMessage, "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateMesIntegrationUI();
                return;
            }

            SetMesButtonsEnabled(false, false);

            if (mode == MesIntegrationMode.ModbusServer)
            {
                UpdateMesStatusDisplay("Modbus 状态：启用中...", Brushes.DarkOrange);

                try
                {
                    await _mesService.DisconnectAsync();
                    await _modbusService.ApplyConfigurationAsync(config);
                    SetChannelStatusMessage(null, "Modbus 服务已启用");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"启用 Modbus 服务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    UpdateMesIntegrationUI();
                }

                return;
            }

            UpdateMesStatusDisplay("MES 状态：启用中...", Brushes.DarkOrange);

            try
            {
                await _modbusService.StopServerAsync();
                bool connected = await _mesService.ConnectAsync(config);
                if (connected)
                {
                    SetChannelStatusMessage(null, "MES 系统已启用");
                }
                else
                {
                    MessageBox.Show("MES 系统启用失败，请检查网络设置。", "启用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("启用操作已取消。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启用 MES 系统失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateMesIntegrationUI();
            }
        }

        private async void BtnMesDisconnect_Click(object sender, RoutedEventArgs e)
        {
            SetMesButtonsEnabled(false, false);

            var mode = _appConfig?.MesIntegrationMode ?? MesIntegrationMode.HttpPush;

            if (mode == MesIntegrationMode.ModbusServer)
            {
                UpdateMesStatusDisplay("Modbus 状态：停用中...", Brushes.DarkOrange);

                try
                {
                    await _modbusService.StopServerAsync();
                    SetChannelStatusMessage(null, "Modbus 服务已停用");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"停用 Modbus 服务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    UpdateMesIntegrationUI();
                }

                return;
            }

            UpdateMesStatusDisplay("MES 状态：停用中...", Brushes.DarkOrange);

            try
            {
                await _mesService.DisconnectAsync();
                SetChannelStatusMessage(null, "MES 系统已停用");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停用 MES 系统失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateMesIntegrationUI();
            }
        }

        private async void TxtProductCode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await ProcessProductCodeAsync();
            }
        }

        private void UpdateMesIntegrationUI()
        {
            var mode = _appConfig?.MesIntegrationMode ?? MesIntegrationMode.HttpPush;
            if (mode == MesIntegrationMode.ModbusServer)
            {
                bool running = _modbusService.IsRunning;
                UpdateMesStatusDisplay(running ? "Modbus 状态：已启用" : "Modbus 状态：未启用",
                    running ? Brushes.Green : Brushes.Red);
                SetMesButtonsEnabled(!running, running);
            }
            else
            {
                bool connected = _mesService.IsConnected;
                UpdateMesStatusDisplay(connected ? "MES 状态：已启用" : "MES 状态：未启用",
                    connected ? Brushes.Green : Brushes.Red);
                SetMesButtonsEnabled(!connected, connected);
            }
        }

        private void UpdateMesStatusDisplay(string message, Brush brush)
        {
            TxtMesStatus.Text = message;
            TxtMesStatus.Foreground = brush;
        }

        private void SetMesButtonsEnabled(bool connectEnabled, bool disconnectEnabled)
        {
            BtnMesConnect.IsEnabled = connectEnabled;
            BtnMesDisconnect.IsEnabled = disconnectEnabled;
        }

        private static bool ValidateMesConfiguration(AppConfig config, MesIntegrationMode mode, out string errorMessage)
        {
            if (config == null)
            {
                errorMessage = "未找到 MES 配置，请前往系统设置完善相关参数。";
                return false;
            }

            if (mode == MesIntegrationMode.ModbusServer)
            {
                if (config.ModbusServerPort <= 0 || config.ModbusServerPort > 65535)
                {
                    errorMessage = "Modbus 端口号配置无效，请前往系统设置。";
                    return false;
                }

                errorMessage = string.Empty;
                return true;
            }

            if (string.IsNullOrWhiteSpace(config.MesServerIp))
            {
                errorMessage = "MES IP 地址未配置，请前往系统设置。";
                return false;
            }

            if (config.MesServerPort <= 0 || config.MesServerPort > 65535)
            {
                errorMessage = "MES 端口号配置无效，请前往系统设置。";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        private void TxtProductCode_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                if (sender is TextBox textBox && !textBox.IsFocused)
                {
                    textBox.Focus();
                }

                e.Handled = true;
            }
        }

        private void TxtProductCode_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            var now = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(textBox.Text) && (now - _lastProductCodeInputTime) > BarcodeInputResetThreshold)
            {
                textBox.Clear();
            }

            _lastProductCodeInputTime = now;
        }

        private async Task ProcessProductCodeAsync()
        {
            var sanitizedCode = CodeScanService.SanitizeBarcode(TxtProductCode.Text);

            if (!string.Equals(TxtProductCode.Text ?? string.Empty, sanitizedCode, StringComparison.Ordinal))
            {
                TxtProductCode.Text = sanitizedCode;
                TxtProductCode.CaretIndex = sanitizedCode.Length;
            }

            if (string.IsNullOrEmpty(sanitizedCode))
            {
                MessageBox.Show("请输入产品代码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_selectedModel == null)
            {
                MessageBox.Show("请选择产品型号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var productCode = sanitizedCode;

            int duplicateCount;
            try
            {
                duplicateCount = await _dbService.CountTestRecordsByProductCodeAsync(productCode);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"条码重复检查失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                SetChannelStatusMessage(null, "条码重复检查失败，请稍后重试");
                return;
            }

            if (duplicateCount > 0)
            {
                if (ChkContinueDuplicate.IsChecked == true)
                {
                    _duplicateBarcodeBlocked = false;
                    UpdateAllChannelButtons();
                    SetChannelStatusMessage(null,
                        $"条码{productCode}已有{duplicateCount}条记录，已自动准备下一次测试");
                }
                else
                {
                    _duplicateBarcodeBlocked = true;
                    UpdateAllChannelButtons();
                    TxtProductCode.Focus();
                    SetChannelStatusMessage(null, $"条码{productCode}重复，未勾选“重复扫码自动继续”，已禁止启动测试");
                }
            }
            else
            {
                _duplicateBarcodeBlocked = false;
                UpdateAllChannelButtons();
                SetChannelStatusMessage(null, "产品已扫码，可以开始测试");
            }
        }

        private async void BtnStartLeft_Click(object sender, RoutedEventArgs e)
        {
            _ = await StartChannelAsync(1);
        }

        private async void BtnStartRight_Click(object sender, RoutedEventArgs e)
        {
            _ = await StartChannelAsync(2);
        }

        private async void BtnStartCh3_Click(object sender, RoutedEventArgs e)
        {
            _ = await StartChannelAsync(3);
        }

        private async void BtnStartCh4_Click(object sender, RoutedEventArgs e)
        {
            _ = await StartChannelAsync(4);
        }

        private void ChkContinueDuplicate_CheckedChanged(object sender, RoutedEventArgs e)
        {
            UpdateAllChannelButtons();
        }

        private async Task<bool> StartChannelAsync(int channel, bool triggeredByPlc = false)
        {
            if (!_licenseService.IsLicenseValid(out var licenseReason))
            {
                ShowValidationMessage($"未授权，无法启动自动测试：{licenseReason}", triggeredByPlc, channel);
                return false;
            }

            if (_channelRunning.TryGetValue(channel, out var isRunning) && isRunning)
            {
                if (!triggeredByPlc)
                {
                    MessageBox.Show($"通道{channel}正在测试中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return false;
            }

            if (_selectedModel == null)
            {
                ShowValidationMessage("请选择产品型号", triggeredByPlc, channel);
                return false;
            }

            await RefreshSelectedModelAsync();

            if (string.IsNullOrWhiteSpace(TxtWorkOrder.Text))
            {
                ShowValidationMessage("请先填写工单号。", triggeredByPlc, channel);
                if (!triggeredByPlc)
                {
                    TxtWorkOrder.Focus();
                }
                return false;
            }

            EnsureModelDefaults(_selectedModel);

            var barcode = CodeScanService.SanitizeBarcode(TxtProductCode.Text);

            if (!string.Equals(TxtProductCode.Text ?? string.Empty, barcode, StringComparison.Ordinal))
            {
                TxtProductCode.Text = barcode;
                TxtProductCode.CaretIndex = barcode.Length;
            }

            if ((_selectedModel.ProcessConfig?.EnableBarcodeCheck ?? false) && string.IsNullOrEmpty(barcode))
            {
                ShowValidationMessage("请先扫描产品条码", triggeredByPlc, channel);
                if (!triggeredByPlc)
                {
                    TxtProductCode.Focus();
                }
                return false;
            }

            if (_duplicateBarcodeBlocked && ChkContinueDuplicate.IsChecked != true)
            {
                ShowValidationMessage("重复条码且未启用自动继续，已禁止启动测试", triggeredByPlc, channel);
                return false;
            }

            await EnsureMessageConfigLoadedAsync(_selectedModel, channel);

            EnsureDailyProductionState();
            _channelRunning[channel] = true;
            UpdateChannelButtons(channel);
            ResetChannelVisual(channel);
            _channelStartTimes[channel] = DateTime.Now;
            UpdateChannelInfoDisplay(channel);
            StartElapsedTimerIfNeeded();
            if (channel == 1)
            {
                SetBannerState(LeftStatusBanner, TxtLeftStatus, "测试中", Colors.Orange);
            }
            else if (channel == 2)
            {
                SetBannerState(RightStatusBanner, TxtRightStatus, "测试中", Colors.Orange);
            }
            else if (channel == 3)
            {
                SetBannerState(Ch3StatusBanner, TxtCh3Status, "测试中", Colors.Orange);
            }
            else if (channel == 4)
            {
                SetBannerState(Ch4StatusBanner, TxtCh4Status, "测试中", Colors.Orange);
            }

            var options = new TestStartOptions
            {
                Channel = channel,
                Model = _selectedModel,
                WorkOrder = TxtWorkOrder.Text,
                Barcode = barcode,
                Operator = _currentUser.Username,
                ContinueOnDuplicate = ChkContinueDuplicate.IsChecked == true
            };

            try
            {
                var startTask = _testService.StartTestAsync(options);

                if (!string.IsNullOrEmpty(barcode))
                {
                    TxtProductCode.Clear();
                    _lastProductCodeInputTime = DateTime.MinValue;
                }
                TxtProductCode.Focus();

                bool result = await startTask;
                return result;
            }
            catch (Exception ex)
            {
                if (triggeredByPlc)
                {
                    SetChannelStatusMessage(channel, $"测试启动失败: {ex.Message}");
                }
                else
                {
                    MessageBox.Show($"测试启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                _channelRunning[channel] = false;
                UpdateChannelButtons(channel);
                _channelStartTimes.Remove(channel);
                UpdateChannelInfoDisplay(channel);
                StopElapsedTimerIfNeeded();
                return false;
            }
        }

        public async Task SavePersistentStateAsync()
        {
            try
            {
                _appConfig ??= await _configService.LoadAppConfigAsync();
            }
            catch (Exception)
            {
                _appConfig = new AppConfig();
            }

            if (_appConfig == null)
            {
                return;
            }

            _appConfig.LastWorkOrder = TxtWorkOrder.Text?.Trim() ?? string.Empty;
            _appConfig.LastProductModel = _selectedModel?.ModelName ?? string.Empty;

            await _configService.SaveAppConfigAsync(_appConfig);
        }

        public Task<bool> StartChannelFromPlcAsync(int channel)
        {
            SetChannelStatusMessage(channel, $"PLC触发通道{channel}测试启动");
            return StartChannelAsync(channel, true);
        }

        public void StopChannelFromPlc(int channel)
        {
            StopChannel(channel, false, $"PLC触发通道{channel}停止测试");
        }

        public bool IsChannelRunning(int channel)
        {
            return _channelRunning.TryGetValue(channel, out bool running) && running;
        }

        private void ShowValidationMessage(string message, bool triggeredByPlc, int channel)
        {
            if (triggeredByPlc)
            {
                SetChannelStatusMessage(channel, message);
            }
            else
            {
                MessageBox.Show(message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task RefreshSelectedModelAsync()
        {
            if (_selectedModel == null)
            {
                return;
            }

            var previousModelName = _selectedModel.ModelName;

            try
            {
                var models = await _configService.LoadProductModelsAsync();
                _productModels = models?.Where(m => m != null).ToList() ?? new List<ProductModel>();
                CmbProductModel.ItemsSource = _productModels;

                if (_productModels.Count == 0)
                {
                    MessageBox.Show("未找到任何型号配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var updatedModel = _productModels
                    .FirstOrDefault(m => string.Equals(m?.ModelName, previousModelName, StringComparison.OrdinalIgnoreCase));

                if (updatedModel != null)
                {
                    _selectedModel = updatedModel;
                    EnsureModelDefaults(_selectedModel);
                    CmbProductModel.SelectedItem = updatedModel;
                }
                else
                {
                    MessageBox.Show($"未找到型号 {previousModelName} 的最新配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"刷新型号配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task EnsureMessageConfigLoadedAsync(ProductModel model, int channel)
        {
            if (model == null)
            {
                return;
            }

            var channelConfig = channel switch
            {
                1 => model.Channel1Config,
                2 => model.Channel2Config,
                3 => model.Channel3Config,
                4 => model.Channel4Config,
                _ => null
            };
            if (channelConfig == null)
            {
                return;
            }

            MessageConfig messageConfig = null;

            if (!string.IsNullOrWhiteSpace(model.ModelName))
            {
                try
                {
                    messageConfig = await _dbService.LoadChannelMessageConfigAsync(model.ModelName, channel);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载报文配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            channelConfig.MessageConfig = CloneMessageConfig(messageConfig ?? channelConfig.MessageConfig);
        }

        private static MessageConfig CloneMessageConfig(MessageConfig source)
        {
            return new MessageConfig
            {
                PowerOnMessage = NormalizeMessageArray(source?.PowerOnMessage),
                SleepMessage = NormalizeMessageArray(source?.SleepMessage),
                StopMessage = NormalizeMessageArray(source?.StopMessage),
                MassageMessage = NormalizeMessageArray(source?.MassageMessage),
                MassageMessage2 = NormalizeMessageArray(source?.MassageMessage2),
                ReadMessage = NormalizeMessageArray(source?.ReadMessage)
            };
        }

        private static ushort[] NormalizeMessageArray(ushort[] source)
        {
            var result = new ushort[20];
            if (source == null)
            {
                return result;
            }

            Array.Copy(source, result, Math.Min(source.Length, result.Length));
            return result;
        }

        private static void ApplyModelDefaults(IEnumerable<ProductModel> models)
        {
            if (models == null)
            {
                return;
            }

            foreach (var model in models)
            {
                EnsureModelDefaults(model);
            }
        }

        private static void EnsureModelDefaults(ProductModel model)
        {
            if (model == null)
            {
                return;
            }

            model.ProcessConfig ??= new TestProcessConfig();
            model.Channel1Config ??= new ChannelConfig { ChannelName = "通道1" };
            model.Channel2Config ??= new ChannelConfig { ChannelName = "通道2" };
            model.Channel3Config ??= new ChannelConfig { ChannelName = "通道3" };
            model.Channel4Config ??= new ChannelConfig { ChannelName = "通道4" };

            model.Channel1Config.MessageConfig ??= new MessageConfig();
            model.Channel2Config.MessageConfig ??= new MessageConfig();
            model.Channel3Config.MessageConfig ??= new MessageConfig();
            model.Channel4Config.MessageConfig ??= new MessageConfig();

            NormalizeChannelConfig(model.Channel1Config);
            NormalizeChannelConfig(model.Channel2Config);
            NormalizeChannelConfig(model.Channel3Config);
            NormalizeChannelConfig(model.Channel4Config);
        }

        private static void NormalizeChannelConfig(ChannelConfig channel)
        {
            if (channel == null)
            {
                return;
            }

            channel.LumbarTestConfigs ??= new List<LumbarTestConfig>();
            channel.MassageConfigs ??= new List<MassageConfig>();

            var orderedActions = channel.LumbarTestConfigs
                .Where(action => action != null)
                .Select(action =>
                {
                    action.MRegisterAddress = action.MRegisterAddress?.Trim() ?? string.Empty;
                    action.SendMessage = NormalizeMessageArray(action.SendMessage);

                    if (double.IsNaN(action.TargetHeight) || double.IsInfinity(action.TargetHeight))
                    {
                        action.TargetHeight = 0;
                    }

                    if (action.TargetTime < 0)
                    {
                        action.TargetTime = 0;
                    }

                    return action;
                })
                .OrderBy(action => action.Order > 0 ? action.Order : int.MaxValue)
                .ThenBy(action => (int)action.Action)
                .ToList();

            for (int i = 0; i < orderedActions.Count; i++)
            {
                if (orderedActions[i].Order <= 0)
                {
                    orderedActions[i].Order = i + 1;
                }
            }

            channel.LumbarTestConfigs = orderedActions;

            foreach (var massage in channel.MassageConfigs.Where(m => m != null))
            {
                massage.HeightSwitchAddress = massage.HeightSwitchAddress?.Trim() ?? string.Empty;
            }

            channel.MessageConfig ??= new MessageConfig();
            channel.MessageConfig.PowerOnMessage = NormalizeMessageArray(channel.MessageConfig.PowerOnMessage);
            channel.MessageConfig.SleepMessage = NormalizeMessageArray(channel.MessageConfig.SleepMessage);
            channel.MessageConfig.StopMessage = NormalizeMessageArray(channel.MessageConfig.StopMessage);
            channel.MessageConfig.MassageMessage = NormalizeMessageArray(channel.MessageConfig.MassageMessage);
            channel.MessageConfig.MassageMessage2 = NormalizeMessageArray(channel.MessageConfig.MassageMessage2);
            channel.MessageConfig.ReadMessage = NormalizeMessageArray(channel.MessageConfig.ReadMessage);
        }

        private void BtnStopLeft_Click(object sender, RoutedEventArgs e)
        {
            StopChannel(1, true);
        }

        private void BtnStopRight_Click(object sender, RoutedEventArgs e)
        {
            StopChannel(2, true);
        }

        private void BtnStopCh3_Click(object sender, RoutedEventArgs e)
        {
            StopChannel(3, true);
        }

        private void BtnStopCh4_Click(object sender, RoutedEventArgs e)
        {
            StopChannel(4, true);
        }

        private void StopChannel(int channel, bool requireConfirmation, string messageOverride = null)
        {
            if (!_channelRunning.TryGetValue(channel, out var running) || !running)
            {
                if (!string.IsNullOrEmpty(messageOverride))
                {
                    SetChannelStatusMessage(channel, messageOverride);
                }
                return;
            }

            if (requireConfirmation)
            {
                var result = MessageBox.Show($"确定要停止通道{channel}测试吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _testService.StopTest(channel);
            _channelRunning[channel] = false;
            UpdateChannelButtons(channel);
            SetChannelStatusMessage(channel, messageOverride ?? $"通道{channel}测试已停止");
            _channelStartTimes.Remove(channel);
            UpdateChannelInfoDisplay(channel);
            StopElapsedTimerIfNeeded();
        }

        private void UpdateChannelButtons(int channel)
        {
            bool allowStart = !_duplicateBarcodeBlocked || ChkContinueDuplicate.IsChecked == true;
            if (channel == 1)
            {
                BtnStartLeft.IsEnabled = !_channelRunning[1] && allowStart;
                BtnStopLeft.IsEnabled = _channelRunning[1];
                if (!_channelRunning[1])
                {
                    ApplyIdleBannerState(1, LeftStatusBanner, TxtLeftStatus);
                    SetChannelStatusMessage(1, DefaultStatusMessage);
                }
            }
            else if (channel == 2)
            {
                BtnStartRight.IsEnabled = !_channelRunning[2] && allowStart;
                BtnStopRight.IsEnabled = _channelRunning[2];
                if (!_channelRunning[2])
                {
                    ApplyIdleBannerState(2, RightStatusBanner, TxtRightStatus);
                    SetChannelStatusMessage(2, DefaultStatusMessage);
                }
            }
            else if (channel == 3)
            {
                BtnStartCh3.IsEnabled = !_channelRunning[3] && allowStart;
                BtnStopCh3.IsEnabled = _channelRunning[3];
                if (!_channelRunning[3])
                {
                    ApplyIdleBannerState(3, Ch3StatusBanner, TxtCh3Status);
                    SetChannelStatusMessage(3, DefaultStatusMessage);
                }
            }
            else if (channel == 4)
            {
                BtnStartCh4.IsEnabled = !_channelRunning[4] && allowStart;
                BtnStopCh4.IsEnabled = _channelRunning[4];
                if (!_channelRunning[4])
                {
                    ApplyIdleBannerState(4, Ch4StatusBanner, TxtCh4Status);
                    SetChannelStatusMessage(4, DefaultStatusMessage);
                }
            }
        }

        private void UpdateAllChannelButtons()
        {
            UpdateChannelButtons(1);
            UpdateChannelButtons(2);
            UpdateChannelButtons(3);
            UpdateChannelButtons(4);
        }

        private void InitializeProductionState()
        {
            EnsureDailyProductionState();
            LoadDailyProductionCounts();
            UpdateTargetProductionText();
            UpdateOverallProductionDisplay();
            UpdateAllChannelInfoDisplays();
        }

        private void ApplyTargetProductionAuthorization()
        {
            bool isAdmin = string.Equals(_currentUser.Role, "Admin", StringComparison.OrdinalIgnoreCase);
            TxtTargetProduction.IsEnabled = isAdmin;
            TxtTargetProduction.IsReadOnly = !isAdmin;
            TxtTargetProduction.ToolTip = isAdmin ? null : "仅管理员可编辑目标产量";
        }

        private void UpdateTargetProductionText()
        {
            _targetProduction = Math.Max(0, _appConfig?.TargetProduction ?? 0);
            TxtTargetProduction.Text = _targetProduction.ToString();
        }

        private void EnsureDailyProductionState()
        {
            if (_appConfig == null)
            {
                return;
            }

            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (!string.Equals(_appConfig.DailyProductionDate, today, StringComparison.Ordinal))
            {
                _appConfig.DailyProductionDate = today;
                _appConfig.DailyTestCount = 0;
                _appConfig.DailyPassCount = 0;
                _appConfig.DailyFailCount = 0;
                ResetChannelDailyStats();
                LoadDailyProductionCounts();
                UpdateOverallProductionDisplay();
                UpdateAllChannelInfoDisplays();
                _ = SaveAppConfigAsync();
            }
        }

        private void ResetChannelDailyStats()
        {
            _appConfig.DailyChannelProductions ??= new List<ChannelDailyProduction>();
            _appConfig.DailyChannelProductions.Clear();

            foreach (int channel in new[] { 1, 2, 3, 4 })
            {
                _appConfig.DailyChannelProductions.Add(new ChannelDailyProduction { Channel = channel });
            }
        }

        private void LoadDailyProductionCounts()
        {
            _testCount = _appConfig?.DailyTestCount ?? 0;
            _passCount = _appConfig?.DailyPassCount ?? 0;
            _failCount = _appConfig?.DailyFailCount ?? 0;

            TxtTestCount.Text = _testCount.ToString();
            TxtPassCount.Text = _passCount.ToString();
            TxtFailCount.Text = _failCount.ToString();

            _channelDailyStats.Clear();
            _appConfig ??= new AppConfig();
            _appConfig.DailyChannelProductions ??= new List<ChannelDailyProduction>();
            var channelStats = _appConfig.DailyChannelProductions;
            foreach (int channel in new[] { 1, 2, 3, 4 })
            {
                var stats = channelStats.FirstOrDefault(s => s.Channel == channel);
                if (stats == null)
                {
                    stats = new ChannelDailyProduction { Channel = channel };
                    channelStats.Add(stats);
                }
                _channelDailyStats[channel] = stats;
            }
        }

        private void UpdateChannelDailyStats(TestRecord record)
        {
            if (_appConfig == null)
            {
                return;
            }

            _appConfig.DailyTestCount = ++_testCount;
            if (record.Result == TestResult.Pass)
            {
                _appConfig.DailyPassCount = ++_passCount;
            }
            else
            {
                _appConfig.DailyFailCount = ++_failCount;
            }

            if (_channelDailyStats.TryGetValue(record.Channel, out var stats))
            {
                stats.TestCount++;
                if (record.Result == TestResult.Pass)
                {
                    stats.PassCount++;
                }
                else
                {
                    stats.FailCount++;
                }
            }
        }

        private void UpdateOverallProductionDisplay()
        {
            TxtTestCount.Text = _testCount.ToString();
            TxtPassCount.Text = _passCount.ToString();
            TxtFailCount.Text = _failCount.ToString();

            int target = Math.Max(0, _targetProduction);
            OverallProductionProgress.Maximum = target > 0 ? target : 1;
            OverallProductionProgress.Value = Math.Min(_testCount, OverallProductionProgress.Maximum);
        }

        private void UpdateAllChannelInfoDisplays()
        {
            foreach (int channel in new[] { 1, 2, 3, 4 })
            {
                UpdateChannelInfoDisplay(channel);
            }
        }

        private void UpdateChannelInfoDisplay(int channel)
        {
            if (!_channelInfoWidgets.TryGetValue(channel, out var widgets))
            {
                return;
            }

            bool running = _channelRunning.TryGetValue(channel, out var isRunning) && isRunning;
            widgets.ElapsedPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            widgets.CountPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

            if (running)
            {
                UpdateChannelElapsedDisplay(channel);
                return;
            }

            if (_channelDailyStats.TryGetValue(channel, out var stats))
            {
                widgets.TestCountText.Text = stats.TestCount.ToString();
                widgets.PassCountText.Text = stats.PassCount.ToString();
                widgets.FailCountText.Text = stats.FailCount.ToString();

                double total = stats.TestCount;
                double passRatio = total > 0 ? stats.PassCount / total : 0;
                double failRatio = total > 0 ? stats.FailCount / total : 0;

                widgets.PassBarColumn.Width = new GridLength(passRatio, GridUnitType.Star);
                widgets.FailBarColumn.Width = new GridLength(failRatio, GridUnitType.Star);
            }
        }

        private void UpdateChannelElapsedDisplay(int channel)
        {
            if (!_channelInfoWidgets.TryGetValue(channel, out var widgets))
            {
                return;
            }

            if (!_channelStartTimes.TryGetValue(channel, out var startTime))
            {
                widgets.ElapsedText.Text = "当前测试已进行 0分00秒";
                return;
            }

            var elapsed = DateTime.Now - startTime;
            widgets.ElapsedText.Text = $"当前测试已进行 {elapsed.Minutes}分{elapsed.Seconds:00}秒";
        }

        private void StartElapsedTimerIfNeeded()
        {
            if (!_elapsedTimer.IsEnabled)
            {
                _elapsedTimer.Start();
            }
        }

        private void StopElapsedTimerIfNeeded()
        {
            if (_channelRunning.Values.Any(r => r))
            {
                return;
            }

            _elapsedTimer.Stop();
        }

        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            EnsureDailyProductionState();
            foreach (var channel in _channelStartTimes.Keys.ToList())
            {
                UpdateChannelElapsedDisplay(channel);
            }

            StopElapsedTimerIfNeeded();
        }

        private async Task SaveAppConfigAsync()
        {
            if (_appConfig == null)
            {
                return;
            }

            try
            {
                await _configService.SaveAppConfigAsync(_appConfig);
            }
            catch (Exception)
            {
                // ignore persistence errors in UI updates
            }
        }

        private void TxtTargetProduction_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private async void TxtTargetProduction_LostFocus(object sender, RoutedEventArgs e)
        {
            await SaveTargetProductionAsync();
        }

        private async void TxtTargetProduction_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            await SaveTargetProductionAsync();
        }

        private async Task SaveTargetProductionAsync()
        {
            if (!string.Equals(_currentUser.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                UpdateTargetProductionText();
                return;
            }

            if (!int.TryParse(TxtTargetProduction.Text?.Trim(), out int target) || target < 0)
            {
                MessageBox.Show("目标产量请输入非负整数。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateTargetProductionText();
                return;
            }

            _targetProduction = target;
            _appConfig ??= new AppConfig();
            _appConfig.TargetProduction = target;
            UpdateOverallProductionDisplay();
            await SaveAppConfigAsync();
        }

        private sealed class ChannelInfoWidgets
        {
            public ChannelInfoWidgets(FrameworkElement elapsedPanel, TextBlock elapsedText, FrameworkElement countPanel,
                TextBlock testCountText, TextBlock passCountText, TextBlock failCountText,
                ColumnDefinition passBarColumn, ColumnDefinition failBarColumn)
            {
                ElapsedPanel = elapsedPanel;
                ElapsedText = elapsedText;
                CountPanel = countPanel;
                TestCountText = testCountText;
                PassCountText = passCountText;
                FailCountText = failCountText;
                PassBarColumn = passBarColumn;
                FailBarColumn = failBarColumn;
            }

            public FrameworkElement ElapsedPanel { get; }
            public TextBlock ElapsedText { get; }
            public FrameworkElement CountPanel { get; }
            public TextBlock TestCountText { get; }
            public TextBlock PassCountText { get; }
            public TextBlock FailCountText { get; }
            public ColumnDefinition PassBarColumn { get; }
            public ColumnDefinition FailBarColumn { get; }
        }

        private enum GraphicRetention
        {
            None,
            Lumbar,
            Massage
        }

        private void ResetChannelVisual(int channel)
        {
            if (_channelStages.TryGetValue(channel, out var stages))
            {
                foreach (var item in stages)
                {
                    item.State = StepExecutionState.Pending;
                    item.Message = string.Empty;
                }
            }

            if (_channelMassagePoints.TryGetValue(channel, out var points))
            {
                foreach (var point in points)
                {
                    point.Reset();
                }
            }
        }

        private void SetBannerState(Border banner, TextBlock textBlock, string text, Color color)
        {
            banner.Background = new SolidColorBrush(color);
            textBlock.Text = text;
        }

        private void UpdateChannelFinalState(TestRecord record)
        {
            var stages = _channelStages[record.Channel];
            foreach (var stageResult in record.StageResults)
            {
                var item = stages.FirstOrDefault(s => s.Stage == stageResult.Stage);
                if (item != null)
                {
                    item.State = stageResult.State;
                    item.Message = stageResult.Message;
                }
            }

            Border banner = record.Channel switch
            {
                1 => LeftStatusBanner,
                2 => RightStatusBanner,
                3 => Ch3StatusBanner,
                4 => Ch4StatusBanner,
                _ => null
            };
            TextBlock textBlock = record.Channel switch
            {
                1 => TxtLeftStatus,
                2 => TxtRightStatus,
                3 => TxtCh3Status,
                4 => TxtCh4Status,
                _ => null
            };

            if (banner == null || textBlock == null)
            {
                return;
            }

            _lastChannelResults[record.Channel] = record.Result;
            _channelCurrentWasBelowResetThreshold[record.Channel] = false;
            if (record.Result == TestResult.Pass)
            {
                SetBannerState(banner, textBlock, "OK", Colors.ForestGreen);
            }
            else if (record.Result == TestResult.Aborted)
            {
                SetBannerState(banner, textBlock, "中止", Colors.DarkOrange);
            }
            else
            {
                SetBannerState(banner, textBlock, "NG", Colors.Firebrick);
            }
        }

        private void ApplyIdleBannerState(int channel, Border banner, TextBlock textBlock)
        {
            if (_lastChannelResults.TryGetValue(channel, out var result))
            {
                switch (result)
                {
                    case TestResult.Pass:
                        SetBannerState(banner, textBlock, "OK", Colors.ForestGreen);
                        return;
                    case TestResult.Aborted:
                        SetBannerState(banner, textBlock, "中止", Colors.DarkOrange);
                        return;
                    case TestResult.Fail:
                        SetBannerState(banner, textBlock, "NG", Colors.Firebrick);
                        return;
                }
            }

            SetBannerState(banner, textBlock, "等待测试", Colors.SlateGray);
        }

        private (Border Banner, TextBlock TextBlock) GetChannelBannerElements(int channel)
        {
            Border banner = channel switch
            {
                1 => LeftStatusBanner,
                2 => RightStatusBanner,
                3 => Ch3StatusBanner,
                4 => Ch4StatusBanner,
                _ => null
            };
            TextBlock textBlock = channel switch
            {
                1 => TxtLeftStatus,
                2 => TxtRightStatus,
                3 => TxtCh3Status,
                4 => TxtCh4Status,
                _ => null
            };

            return (banner, textBlock);
        }

        private void DisplayTestResult(TestRecord record)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10),
                Background = record.Result == TestResult.Pass ? new SolidColorBrush(Color.FromRgb(208, 240, 217)) : new SolidColorBrush(Color.FromRgb(252, 221, 221)),
                CornerRadius = new CornerRadius(6)
            };

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = $"通道{record.Channel} - {record.TestTime:HH:mm:ss} - {(record.Result == TestResult.Pass ? "通过" : record.Result == TestResult.Aborted ? "中止" : "不合格")}",
                FontWeight = FontWeights.Bold,
                FontSize = 16
            });

            AddResultItem(panel, "工单号", record.WorkOrder);
            AddResultItem(panel, "产品码", GetMaskedProductCode(record.ProductCode));
            AddResultItem(panel, "测试序号", record.TestCount.ToString());

            if (record.SleepCurrent.HasValue)
            {
                AddResultItem(panel, "休眠电流", $"{record.SleepCurrent.Value:F2} mA");
            }

            if (record.StaticCurrent.HasValue)
            {
                AddResultItem(panel, "静态电流", $"{record.StaticCurrent.Value:F2} mA");
            }

            if (!string.IsNullOrEmpty(record.FailReason))
            {
                AddResultItem(panel, "失败原因", record.FailReason, Brushes.Firebrick);
            }

            if (record.LumbarResults.Any())
            {
                var summary = string.Join("; ", record.LumbarResults.Select(r =>
                {
                    string target = $"{r.TargetHeight:F1}mm/{r.TargetTime}ms";
                    string actualHeight = r.ActualHeight.HasValue ? $"{r.ActualHeight.Value:F1}mm" : "-";
                    string actualTime = r.ActualTime.HasValue ? $"{r.ActualTime.Value}ms" : "-";
                    string actionLabel = r.Action.ToDisplayName();
                    string orderText = string.IsNullOrWhiteSpace(actionLabel)
                        ? $"动作{r.Order}"
                        : $"动作{r.Order}({actionLabel})";
                    return $"{orderText}({target}->{actualHeight}/{actualTime}):{(r.Passed ? "OK" : "NG")}";
                }));
                AddResultItem(panel, "腰托结果", summary);
            }

            if (record.MassageResults.Any())
            {
                var failedResults = record.MassageResults.Where(r => !r.Passed).ToList();
                if (failedResults.Any())
                {
                    var displayedFailures = failedResults
                        .Take(8)
                        .Select(r => $"点{r.Point}:{(string.IsNullOrWhiteSpace(r.Message) ? "NG" : r.Message)}");
                    string summary = string.Join("; ", displayedFailures);
                    if (failedResults.Count > 8)
                    {
                        summary += $"...等{failedResults.Count}个失败点";
                    }
                    AddResultItem(panel, "按摩结果", summary, Brushes.Firebrick);
                }
                else
                {
                    var summary = string.Join("; ", record.MassageResults.Select(r => $"点{r.Point}:OK"));
                    AddResultItem(panel, "按摩结果", summary);
                }
            }

            var stageSummary = string.Join(" ->", record.StageResults
                .Where(s => s.Stage != TestStage.Aborted || record.Result == TestResult.Aborted)
                .Select(s => $"{GetStageDisplayName(s.Stage)}:{GetStateDescription(s.State)}"));
            AddResultItem(panel, "流程", stageSummary);

            border.Child = panel;
            TestResultPanel.Children.Insert(0, border);

            while (TestResultPanel.Children.Count > 12)
            {
                TestResultPanel.Children.RemoveAt(TestResultPanel.Children.Count - 1);
            }
        }

        private void AddResultItem(StackPanel parent, string label, string value, Brush foreground = null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = $"{label}: ",
                FontWeight = FontWeights.Bold,
                Width = 100
            });
            panel.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = foreground ?? Brushes.Black
            });
            parent.Children.Add(panel);
        }

        private string GetMaskedProductCode(string? productCode)
        {
            if (string.IsNullOrEmpty(productCode))
            {
                return "**";
            }

            string suffix = productCode.Length <= 6 ? productCode : productCode[^6..];
            return $"**{suffix}";
        }

        private void BtnClearCode_Click(object sender, RoutedEventArgs e)
        {
            TxtProductCode.Clear();
            _lastProductCodeInputTime = DateTime.MinValue;
            TxtProductCode.Focus();
        }

        private string GetStageDisplayName(TestStage stage)
        {
            return stage switch
            {
                TestStage.Standby => "待机检查",
                TestStage.ScanBarcode => "扫码",
                TestStage.StartTest => "启动测试",
                TestStage.SleepTest => "休眠测试",
                TestStage.StaticCurrentTest => "静态电流",
                TestStage.StatusMessageCheck => "状态报文",
                TestStage.LumbarTest => "腰托测试",
                TestStage.MassageTest => "按摩测试",
                TestStage.MasterSlaveDecision => "模式切换",
                TestStage.MasterModeMassage => "按摩2测试",
                TestStage.Completed => "测试完成",
                TestStage.Aborted => "测试终止",
                _ => stage.ToString()
            };
        }

        private string GetStateDescription(StepExecutionState state)
        {
            return state switch
            {
                StepExecutionState.Pending => "待执行",
                StepExecutionState.Running => "进行中",
                StepExecutionState.Passed => "通过",
                StepExecutionState.Failed => "失败",
                StepExecutionState.Skipped => "跳过",
                _ => state.ToString()
            };
        }
    }

    public class TestStageItem : INotifyPropertyChanged
    {
        private StepExecutionState _state;
        private string? _message;

        public TestStageItem(TestStage stage, string displayName)
        {
            Stage = stage;
            DisplayName = displayName;
        }

        public TestStage Stage { get; }
        public string DisplayName { get; }

        public StepExecutionState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged(nameof(State));
                    OnPropertyChanged(nameof(IndicatorBrush));
                    OnPropertyChanged(nameof(StateBackground));
                    OnPropertyChanged(nameof(StateDescription));
                    OnPropertyChanged(nameof(StateForeground));
                }
            }
        }

        public string? Message
        {
            get => _message;
            set
            {
                if (_message != value)
                {
                    _message = value;
                    OnPropertyChanged(nameof(StateDescription));
                }
            }
        }

        public Brush IndicatorBrush => State switch
        {
            StepExecutionState.Pending => Brushes.Gray,
            StepExecutionState.Running => Brushes.Goldenrod,
            StepExecutionState.Passed => Brushes.SeaGreen,
            StepExecutionState.Skipped => Brushes.SlateGray,
            StepExecutionState.Failed => Brushes.IndianRed,
            _ => Brushes.Gray
        };

        public Brush StateBackground => State switch
        {
            StepExecutionState.Running => new SolidColorBrush(Color.FromRgb(255, 249, 196)),
            StepExecutionState.Passed => new SolidColorBrush(Color.FromRgb(219, 244, 221)),
            StepExecutionState.Failed => new SolidColorBrush(Color.FromRgb(252, 221, 221)),
            StepExecutionState.Skipped => new SolidColorBrush(Color.FromRgb(236, 239, 241)),
            _ => Brushes.Transparent
        };

        public string StateDescription
        {
            get
            {
                return string.IsNullOrEmpty(Message) ? State switch
                {
                    StepExecutionState.Pending => "待执行",
                    StepExecutionState.Running => "进行中",
                    StepExecutionState.Passed => "通过",
                    StepExecutionState.Failed => "失败",
                    StepExecutionState.Skipped => "跳过",
                    _ => State.ToString()
                } : Message;
            }
        }

        public Brush StateForeground => State == StepExecutionState.Failed ? Brushes.Firebrick : Brushes.Black;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class MassagePointIndicator : INotifyPropertyChanged
    {
        private bool _isActive;
        private bool _wasTriggered;

        public MassagePointIndicator(int index)
        {
            Index = index;
        }

        public int Index { get; }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    if (_isActive)
                    {
                        WasTriggered = true;
                    }
                    OnPropertyChanged(nameof(IsActive));
                    OnPropertyChanged(nameof(IndicatorBrush));
                    OnPropertyChanged(nameof(IndicatorStroke));
                }
            }
        }

        public bool WasTriggered
        {
            get => _wasTriggered;
            set
            {
                if (_wasTriggered != value)
                {
                    _wasTriggered = value;
                    OnPropertyChanged(nameof(WasTriggered));
                    OnPropertyChanged(nameof(IndicatorBrush));
                    OnPropertyChanged(nameof(IndicatorStroke));
                }
            }
        }

        public Brush IndicatorBrush => IsActive
            ? Brushes.DodgerBlue
            : WasTriggered ? Brushes.LightSkyBlue : Brushes.LightGray;
        public Brush IndicatorStroke => (IsActive || WasTriggered) ? Brushes.SteelBlue : Brushes.DarkGray;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Reset()
        {
            _isActive = false;
            _wasTriggered = false;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(WasTriggered));
            OnPropertyChanged(nameof(IndicatorBrush));
            OnPropertyChanged(nameof(IndicatorStroke));
        }
    }
}
