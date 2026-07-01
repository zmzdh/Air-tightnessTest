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
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Services;

namespace AudioActuatorCanTest.UserControls
{
    public partial class TestControl : UserControl
    {
        private readonly TestService _testService;
        private readonly ConfigService _configService;
        private readonly DatabaseService _dbService;
        private readonly MesService _mesService;
        private readonly ModbusServerService _modbusService;
        private readonly User _currentUser;

        private List<ProductModel> _productModels;
        private ProductModel _selectedModel;
        private AppConfig _appConfig;
        private string _pendingSelectedModelName;

        private int _testCount;
        private int _passCount;
        private int _failCount;

        private const string DefaultStatusMessage = "等待开始测试";

        private readonly Dictionary<int, ObservableCollection<TestStageItem>> _channelStages = new();
        private readonly Dictionary<int, bool> _channelRunning = new() { { 1, false }, { 2, false } };

        private static readonly TimeSpan BarcodeInputResetThreshold = TimeSpan.FromMilliseconds(500);
        private DateTime _lastProductCodeInputTime = DateTime.MinValue;
        private bool _mesEventSubscribed;
        private bool _modbusEventSubscribed;

        public TestControl(TestService testService, ConfigService configService, DatabaseService dbService, MesService mesService, ModbusServerService modbusService, User currentUser)
        {
            InitializeComponent();

            _testService = testService ?? throw new ArgumentNullException(nameof(testService));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _mesService = mesService ?? throw new ArgumentNullException(nameof(mesService));
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));
            _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));

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

            LeftStageList.ItemsSource = _channelStages[1];
            RightStageList.ItemsSource = _channelStages[2];

            SetChannelStatusMessage(null, DefaultStatusMessage);
            UpdateMesIntegrationUI();
        }

        private static string GetChannelDisplayName(int channel) => channel switch
        {
            1 => "左通道",
            2 => "右通道",
            _ => $"通道{channel}"
        };

        private void SetChannelStatusMessage(int? channel, string message)
        {
            var status = string.IsNullOrWhiteSpace(message) ? DefaultStatusMessage : message;
            status = status
                .Replace("通道1", "左通道")
                .Replace("通道2", "右通道");

            if (channel == 1)
            {
                TxtLeftCurrentStep.Text = status;
            }
            else if (channel == 2)
            {
                TxtRightCurrentStep.Text = status;
            }
            else
            {
                TxtLeftCurrentStep.Text = status;
                TxtRightCurrentStep.Text = status;
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

            UpdateMesIntegrationUI();

            if (!TxtProductCode.IsFocused)
            {
                TxtProductCode.Focus();
            }
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
            });
        }

        private void TestService_OnTestCompleted(object sender, TestRecord record)
        {
            Dispatcher.Invoke(() =>
            {
                _testCount++;
                TxtTestCount.Text = _testCount.ToString();

                if (record.Result == TestResult.Pass)
                {
                    _passCount++;
                    TxtPassCount.Text = _passCount.ToString();
                }
                else
                {
                    _failCount++;
                    TxtFailCount.Text = _failCount.ToString();
                }

                UpdateChannelFinalState(record);
                DisplayTestResult(record);

                _channelRunning[record.Channel] = false;
                UpdateChannelButtons(record.Channel);
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
                if (e.Channel == 1)
                {
                    SetBannerState(LeftStatusBanner, TxtLeftStatus, e.IsOk ? "OK" : "NG", e.IsOk ? Colors.ForestGreen : Colors.Firebrick);
                }
                else
                {
                    SetBannerState(RightStatusBanner, TxtRightStatus, e.IsOk ? "OK" : "NG", e.IsOk ? Colors.ForestGreen : Colors.Firebrick);
                }
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
                    MessageBox.Show("MES 系统启用失败，请检查网络设置", "启用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("启用操作已取消", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show("请输入产品代码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    SetChannelStatusMessage(null,
                        $"条码{productCode}已有{duplicateCount}条记录，已自动准备下一次测试");
                }
                else
                {
                    var result = MessageBox.Show(
                        $"条码 {productCode} 已存在 {duplicateCount} 条生产记录，是否继续测试？",
                        "重复条码提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result != MessageBoxResult.Yes)
                    {
                        TxtProductCode.Clear();
                        TxtProductCode.Focus();
                        SetChannelStatusMessage(null, $"条码{productCode}重复，测试已取消");
                        return;
                    }

                    TxtProductCode.Text = sanitizedCode;
                    TxtProductCode.CaretIndex = sanitizedCode.Length;
                    TxtProductCode.Focus();
                    SetChannelStatusMessage(null, $"条码{productCode}重复，已确认继续测试");
                }
            }
            else
            {
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

        private async Task<bool> StartChannelAsync(int channel, bool triggeredByPlc = false)
        {
            if (_channelRunning[channel])
            {
                if (!triggeredByPlc)
                {
                    MessageBox.Show($"通道{channel}正在测试中", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
                ShowValidationMessage("请先填写工单号", triggeredByPlc, channel);
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

            await EnsureMessageConfigLoadedAsync(_selectedModel, channel);

            _channelRunning[channel] = true;
            UpdateChannelButtons(channel);
            ResetChannelVisual(channel);
            SetBannerState(channel == 1 ? LeftStatusBanner : RightStatusBanner,
                           channel == 1 ? TxtLeftStatus : TxtRightStatus,
                           "测试中", Colors.Orange);

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
            SetChannelStatusMessage(channel, $"外部触发通道{channel}测试启动");
            return StartChannelAsync(channel, true);
        }

        public void StopChannelFromPlc(int channel)
        {
            StopChannel(channel, false, $"外部触发通道{channel}停止测试");
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
                    MessageBox.Show("未找到任何型号配置", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    MessageBox.Show($"未找到型号 {previousModelName} 的最新配置", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            var channelConfig = channel == 1 ? model.Channel1Config : model.Channel2Config;
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
            model.Channel1Config ??= new ChannelConfig { ChannelName = "左通道" };
            model.Channel2Config ??= new ChannelConfig { ChannelName = "右通道" };

            model.Channel1Config.MessageConfig ??= new MessageConfig();
            model.Channel2Config.MessageConfig ??= new MessageConfig();

            NormalizeChannelConfig(model.Channel1Config);
            NormalizeChannelConfig(model.Channel2Config);
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
                massage.PressureSwitchAddress = massage.PressureSwitchAddress?.Trim() ?? string.Empty;
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

        private void StopChannel(int channel, bool requireConfirmation, string messageOverride = null)
        {
            if (!_channelRunning[channel])
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
        }

        private void UpdateChannelButtons(int channel)
        {
            if (channel == 1)
            {
                BtnStartLeft.IsEnabled = !_channelRunning[1];
                BtnStopLeft.IsEnabled = _channelRunning[1];
                if (!_channelRunning[1])
                {
                    SetBannerState(LeftStatusBanner, TxtLeftStatus, "等待测试", Colors.SlateGray);
                    SetChannelStatusMessage(1, DefaultStatusMessage);
                }
            }
            else
            {
                BtnStartRight.IsEnabled = !_channelRunning[2];
                BtnStopRight.IsEnabled = _channelRunning[2];
                if (!_channelRunning[2])
                {
                    SetBannerState(RightStatusBanner, TxtRightStatus, "等待测试", Colors.SlateGray);
                    SetChannelStatusMessage(2, DefaultStatusMessage);
                }
            }
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

            var banner = record.Channel == 1 ? LeftStatusBanner : RightStatusBanner;
            var textBlock = record.Channel == 1 ? TxtLeftStatus : TxtRightStatus;

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
                    return $"{orderText}({target}→{actualHeight}/{actualTime}):{(r.Passed ? "OK" : "NG")}";
                }));
                AddResultItem(panel, "腰托结果", summary);
            }

            if (record.MassageResults.Any())
            {
                var summary = string.Join("; ", record.MassageResults.Select(r => $"点{r.Point}:{(r.Passed ? "OK" : "NG")}"));
                AddResultItem(panel, "按摩结果", summary);
            }

            var stageSummary = string.Join(" → ", record.StageResults
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
}
