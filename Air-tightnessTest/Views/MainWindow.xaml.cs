// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using LumbarMassageTest.UserControls;
using LumbarMassageTest.Services;
using LumbarMassageTest.Models;
using LumbarMassageTest.Views;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Xml.Linq;
using System.Linq;

namespace LumbarMassageTest
{
    public partial class MainWindow : Window
    {
        private readonly PLCService _plcService;
        private readonly DatabaseService _dbService;
        private readonly ConfigService _configService;
        private readonly TestService _testService;
        private readonly MesService _mesService;
        private readonly ModbusServerService _modbusService;
        private readonly ILogService _logService;
        private readonly LicenseService _licenseService;
        private readonly SerialPortService _serialPortService;
        private readonly CommService _commService;
        //private readonly ICodeScanService _codeScanService;

        private User _currentUser;
        private DispatcherTimer _timer;
        private AppConfig? _latestAppConfig;

        // 用户控件
        private TestControl _testControl;
        private ManualControl _manualControl;
        private ModelConfigControl _modelControl;
        private ReportControl _reportControl;
        private UserManagementControl _userControl;
        private SystemSettingsControl _systemSettingsControl;
        private SystemLogControl _systemLogControl;
        private HelpControl _helpControl;

        private CancellationTokenSource _plcReadCts;
        private Task _plcReadTask;
        private bool _plcReadingStarted;
        private readonly object _plcDataLock = new object();
        private PLCData _currentPlcData = new PLCData();
        private ProductModel? _currentModelForMapping;
        private bool _prevCh1StartSignal;
        private bool _prevCh1StopSignal;
        private bool _prevCh2StartSignal;
        private bool _prevCh2StopSignal;
        private bool _prevCh3StartSignal;
        private bool _prevCh3StopSignal;
        private bool _prevCh4StartSignal;
        private bool _prevCh4StopSignal;
        private readonly Dictionary<int, DateTime?> _fullTestHoldStartTimes = new();
        private readonly Dictionary<string, bool> _desiredPlcBits = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _cachedPlcBits = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingPlcBitUpdates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, CancellationTokenSource> _ngPulseTokens = new();

        public MainWindow()
        {
            InitializeComponent();

            // 初始化服务 - 使用单例模式获取PLCService实例
            _logService = LogService.Instance;
            _plcService = PLCService.Instance;
            _dbService = new DatabaseService(_logService);
            _configService = new ConfigService(_logService);
            _testService = new TestService(_plcService, _logService);
            _mesService = new MesService(_logService);
            _modbusService = new ModbusServerService(_logService);
            _licenseService = new LicenseService(_logService);
            _serialPortService = new SerialPortService(SerialPortConfig.CreateDefaultDevice1(), _logService);
            _commService = new CommService(_serialPortService, _logService);
            _mesService.OnConnectionChanged += MesService_OnConnectionChanged;
            _modbusService.OnServerStateChanged += ModbusService_OnServerStateChanged;
            MesService_OnConnectionChanged(_mesService, _mesService.IsConnected);
            _testService.OnTestStageChanged += TestService_OnStageChanged;
            _testService.OnTestCompleted += TestService_OnTestCompleted;
            _testService.OnTestCompleted += TestService_OnTestCompletedSaveRecord;

            foreach (var channel in new[] { 1, 2, 3, 4 })
            {
                _fullTestHoldStartTimes[channel] = null;
            }

            // 订阅事件
            _plcService.OnConnectionChanged += PlcService_OnConnectionChanged;
            _plcService.OnError += PlcService_OnError;

            InitializeTimer();
            ShowLoginDialog();

            _ = InitializeIntegrationModeAsync();
            _testService.ConfigurePressureModule(_latestAppConfig ?? new AppConfig());
        }

        private void InitializeTimer()
        {
            try
            {
                _timer = new DispatcherTimer(DispatcherPriority.Normal, this.Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _timer.Tick += Timer_Tick;
                _timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"定时器初始化失败: {ex.Message}", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            // 更新时间显示
            try
            {
                TxtTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception)
            {
                // 忽略UI更新错误
            }
        }

        private async void TestService_OnTestCompletedSaveRecord(object sender, TestRecord record)
        {
            if (_dbService == null)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtStatus.Text = "数据库服务未初始化，无法保存测试记录";
                });
                return;
            }

            bool isModbusMode = IsModbusMode();
            bool modbusUpdated = true;

            try
            {
                _modbusService.UpdateFromTestRecord(record, isModbusMode);
            }
            catch (Exception ex)
            {
                modbusUpdated = false;
                _logService.LogWarning("更新 Modbus 测试数据失败", ex);
            }

            try
            {
                var saved = await _dbService.SaveTestRecordAsync(record);
                if (!saved)
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtStatus.Text = "保存测试记录失败，请检查数据库";
                    });
                }
                else
                {
                    bool sent = await SendTestRecordToMesAsync(record).ConfigureAwait(false);
                    Dispatcher.Invoke(() =>
                    {
                        if (isModbusMode)
                        {
                            TxtStatus.Text = modbusUpdated
                                ? "测试记录已保存并更新 Modbus 数据"
                                : "测试记录已保存，但更新 Modbus 数据失败";
                        }
                        else
                        {
                            TxtStatus.Text = sent
                                ? "测试记录已保存并推送MES成功"
                                : "测试记录已保存，但推送MES失败";
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtStatus.Text = $"保存测试记录异常: {ex.Message}";
                });
            }
        }

        private void TestService_OnStageChanged(object sender, TestStageChangedEventArgs e)
        {

            if (e.Stage != TestStage.Standby || e.State != StepExecutionState.Running)
            {
                return;
            }

            Dispatcher.InvokeAsync(async () =>
            {
                await ResetResultBitsAsync(e.Channel);
                await SetChannelRunningBitAsync(e.Channel, true);
            });
        }

        private void TestService_OnTestCompleted(object sender, TestRecord record)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await SetChannelRunningBitAsync(record.Channel, false);

                switch (record.Result)
                {
                    case TestResult.Pass:
                        await SetTestResultBitsAsync(record.Channel, true);
                        break;
                    case TestResult.Fail:
                        await SetTestResultBitsAsync(record.Channel, false);
                        break;
                    default:
                        await ResetResultBitsAsync(record.Channel);
                        break;
                }
            });
        }




        private async Task EnsureAppConfigLoadedAsync()
        {
            if (_latestAppConfig != null)
            {
                return;
            }

            try
            {
                _latestAppConfig = await _configService.LoadAppConfigAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logService.LogError("加载初始系统配置失败", ex);
            }
        }

        private async void ShowLoginDialog()
        {
            if (_dbService == null)
            {
                MessageBox.Show("数据库服务未初始化，无法登录。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            var loginDialog = new LoginDialog(_dbService);
            if (loginDialog.ShowDialog() == true)
            {
                _currentUser = loginDialog.CurrentUser;

                // 设置DatabaseService的当前用户
                DatabaseService.CurrentUser = _currentUser;
                TxtCurrentUser.Text = $"当前用户: {_currentUser.Username} ({_currentUser.Role})";

                // 更新用户的登录信息
                await _dbService.UpdateLoginInfoAsync(_currentUser.Username);

                // 根据用户权限设置界面
                SetUserPermissions();

                // 先加载配置，避免界面先按4通道渲染后再切回2通道导致闪烁
                await EnsureAppConfigLoadedAsync();
                ApplySerialConfig(_latestAppConfig ?? new AppConfig());

                // 默认显示测试界面
                ShowTestControl();

                // 连接PLC
                await ConnectPLC();

                EnsurePlcReadingStarted();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }

        private void EnsurePlcReadingStarted()
        {
            if (_plcReadingStarted)
            {
                return;
            }

            _plcReadingStarted = true;
            StartPLCReading();
        }

        private enum Permission
        {
            Test,
            Manual,
            Model,
            Report,
            UserManagement,
            SystemSettings,
            SystemLog
        }

        private void SetUserPermissions()
        {
            ConfigureButtonAccess(BtnTest, HasPermission(Permission.Test));
            ConfigureButtonAccess(BtnManual, HasPermission(Permission.Manual));
            ConfigureButtonAccess(BtnModel, HasPermission(Permission.Model));
            ConfigureButtonAccess(BtnReport, HasPermission(Permission.Report));
            ConfigureButtonAccess(BtnUser, HasPermission(Permission.UserManagement));
            ConfigureButtonAccess(BtnSystemSettings, HasPermission(Permission.SystemSettings));
            ConfigureButtonAccess(BtnHelp, true);
            ConfigureButtonAccess(BtnSystemLog, HasPermission(Permission.SystemLog));
        }

        private void ConfigureButtonAccess(Button button, bool hasPermission)
        {
            if (button == null)
            {
                return;
            }

            button.Visibility = hasPermission ? Visibility.Visible : Visibility.Collapsed;
            button.IsEnabled = hasPermission;
        }

        private bool EnsurePermission(Permission permission)
        {
            if (HasPermission(permission))
            {
                return true;
            }

            ShowAccessDeniedMessage();
            return false;
        }

        private void ShowAccessDeniedMessage()
        {
            MessageBox.Show("当前用户无权限访问该功能。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool HasPermission(Permission permission)
        {
            if (_currentUser == null)
            {
                return false;
            }

            return _currentUser.Role switch
            {
                "Admin" => true,
                "Engineer" => permission != Permission.UserManagement &&
                               permission != Permission.SystemSettings &&
                               permission != Permission.SystemLog,
                "Operator" => permission == Permission.Test || permission == Permission.Report,
                _ => permission == Permission.Test || permission == Permission.Report
            };
        }

        private async Task ConnectPLC()
        {
            try
            {
                AppConfig appConfig = await _configService.LoadAppConfigAsync();
                var result = await _plcService.ConnectAsync(appConfig.PLCIPAddress, appConfig.PLCPort, appConfig.PLCStationId);

                if (result)
                {
                    TxtStatus.Text = "PLC连接成功";
                }
                else
                {
                    TxtStatus.Text = "PLC连接失败";
                    MessageBox.Show("PLC连接失败，请检查网络设置", "连接错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"PLC连接异常: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlcService_OnConnectionChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                TxtPLCStatus.Text = $"PLC: {(isConnected ? "已连接" : "未连接")}";
                TxtPLCStatus.Foreground = isConnected ? Brushes.Green : Brushes.Red;

                if (isConnected)
                {
                    TxtStatus.Text = "系统就绪 - PLC已连接";
                    _ = ApplyPendingPlcBitsInternalAsync();
                }
                else
                {
                    TxtStatus.Text = "系统就绪 - PLC未连接";
                }
            });
        }

        private void MesService_OnConnectionChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(UpdateMesStatusBar);
        }

        private void ModbusService_OnServerStateChanged(object? sender, bool isRunning)
        {
            Dispatcher.Invoke(UpdateMesStatusBar);
        }


        private async Task InitializeIntegrationModeAsync()
        {
            try
            {
                var config = await _configService.LoadAppConfigAsync().ConfigureAwait(false);
                await ApplyMesIntegrationAsync(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.LogError("初始化MES集成配置失败", ex);
                Dispatcher.Invoke(UpdateMesStatusBar);
            }
        }


        private async Task ApplyMesIntegrationAsync(AppConfig config)
        {
            if (config == null)
            {
                return;
            }

            _latestAppConfig = config;

            try
            {
                await _modbusService.ApplyConfigurationAsync(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.LogError("应用MES集成配置失败", ex);
            }
            finally
            {
                Dispatcher.Invoke(UpdateMesStatusBar);
            }
        }

        private void ApplySerialConfig(AppConfig config)
        {
            if (config == null)
            {
                return;
            }

            _serialPortService.UpdateConfig(config.SerialDevice1 ?? SerialPortConfig.CreateDefaultDevice1());
            _testService.ConfigurePressureModule(config);
        }

        private void UpdateMesStatusBar()
        {
            var mode = _latestAppConfig?.MesIntegrationMode ?? MesIntegrationMode.HttpPush;
            if (mode == MesIntegrationMode.ModbusServer)
            {
                bool running = _modbusService.IsRunning;
                TxtMesStatusBar.Text = $"MES: Modbus({(running ? "已启用" : "未启用")})";
                TxtMesStatusBar.Foreground = running ? Brushes.Green : Brushes.Red;
            }
            else
            {
                bool connected = _mesService.IsConnected;
                TxtMesStatusBar.Text = $"MES: {(connected ? "已启用" : "未启用")}";
                TxtMesStatusBar.Foreground = connected ? Brushes.Green : Brushes.Red;
            }
        }

        private bool IsModbusMode()
        {
            return (_latestAppConfig?.MesIntegrationMode ?? MesIntegrationMode.HttpPush) == MesIntegrationMode.ModbusServer;
        }

        private void StartPLCReading()
        {
            _plcReadCts = new CancellationTokenSource();
            _plcReadTask = Task.Run(async () => await ReadPLCContinuously(_plcReadCts.Token), _plcReadCts.Token);
        }

        private async Task StopPLCReadingAsync()
        {
            var cts = _plcReadCts;
            var readTask = _plcReadTask;

            if (cts == null && readTask == null)
            {
                return;
            }

            try
            {
                cts?.Cancel();

                if (readTask != null)
                {
                    try
                    {
                        await readTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 预期的取消
                    }
                    catch (ObjectDisposedException)
                    {
                        // 任务已被清理
                    }
                }
            }
            finally
            {
                _plcReadTask = null;
                if (cts != null)
                {
                    cts.Dispose();
                }

                _plcReadCts = null;
                _plcReadingStarted = false;
            }
        }

        private async Task ReadPLCContinuously(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_plcService.IsConnected)
                    {
                        // 读取Modbus离散输入(10000-10255)与线圈(00000-00127)
                        var config = _latestAppConfig ?? new AppConfig();
                        var inputResults = await _plcService.ReadBitsAsync("1x0000", config.PlcDiscreteInputCount);
                        var coilResults = await _plcService.ReadBitsAsync("0x0000", config.PlcCoilCount);

                        bool ch1Start = false;
                        bool ch1Stop = false;
                        bool ch2Start = false;
                        bool ch2Stop = false;
                        bool ch3Start = false;
                        bool ch3Stop = false;
                        bool ch4Start = false;
                        bool ch4Stop = false;

                        lock (_plcDataLock)
                        {
                            // 使用PLCAddressMapper将原始数据映射到结构化数据
                            PLCAddressMapper.MapModbusBitsToStructuredData(inputResults, coilResults, _currentPlcData, _currentModelForMapping);

                            _currentPlcData.LastUpdate = DateTime.Now;

                            ch1Start = (_currentPlcData.Channel1?.AirLeakStartButton ?? false) || (_currentPlcData.Channel1?.FullTestStart ?? false);
                            ch1Stop = _currentPlcData.Channel1?.StopButton ?? false;
                            ch2Start = (_currentPlcData.Channel2?.AirLeakStartButton ?? false) || (_currentPlcData.Channel2?.FullTestStart ?? false);
                            ch2Stop = _currentPlcData.Channel2?.StopButton ?? false;
                            ch3Start = (_currentPlcData.Channel3?.AirLeakStartButton ?? false) || (_currentPlcData.Channel3?.FullTestStart ?? false);
                            ch3Stop = _currentPlcData.Channel3?.StopButton ?? false;
                            ch4Start = (_currentPlcData.Channel4?.AirLeakStartButton ?? false) || (_currentPlcData.Channel4?.FullTestStart ?? false);
                            ch4Stop = _currentPlcData.Channel4?.StopButton ?? false;
                        }

                        HandlePlcSignals(ch1Start, ch1Stop, ch2Start, ch2Stop, ch3Start, ch3Stop, ch4Start, ch4Stop);
                        HandleFullTestHoldAbort(ch1Start, ch2Start, ch3Start, ch4Start);

                        // 通知UI更新
                        Dispatcher.Invoke(() =>
                        {
                            UpdateUIWithPLCData();
                        });
                    }

                    await Task.Delay(100, cancellationToken); // 恢复到100ms延迟
                }
                catch (Exception ex)
                {
                    _logService.LogError("PLC读取错误", ex);
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }


        private void HandlePlcSignals(
            bool ch1Start,
            bool ch1Stop,
            bool ch2Start,
            bool ch2Stop,
            bool ch3Start,
            bool ch3Stop,
            bool ch4Start,
            bool ch4Stop)
        {

            if (!_prevCh1StartSignal && ch1Start)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStartSignalAsync(1));
            }

            if (!_prevCh1StopSignal && ch1Stop)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStopSignalAsync(1));
            }

            if (!_prevCh2StartSignal && ch2Start)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStartSignalAsync(2));
            }

            if (!_prevCh2StopSignal && ch2Stop)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStopSignalAsync(2));
            }

            _prevCh1StartSignal = ch1Start;
            _prevCh1StopSignal = ch1Stop;
            _prevCh2StartSignal = ch2Start;
            _prevCh2StopSignal = ch2Stop;

            int chCount = GetConfiguredChannelCount();

            if (!_prevCh3StartSignal && ch3Start && chCount >= 3)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStartSignalAsync(3));
            }

            if (!_prevCh3StopSignal && ch3Stop && chCount >= 3)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStopSignalAsync(3));
            }

            if (!_prevCh4StartSignal && ch4Start && chCount >= 4)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStartSignalAsync(4));
            }

            if (!_prevCh4StopSignal && ch4Stop && chCount >= 4)
            {
                Dispatcher.InvokeAsync(async () => await OnChannelStopSignalAsync(4));
            }

            _prevCh3StartSignal = ch3Start;
            _prevCh3StopSignal = ch3Stop;
            _prevCh4StartSignal = ch4Start;
            _prevCh4StopSignal = ch4Stop;

            if (chCount < 3)
            {
                _prevCh3StartSignal = false;
                _prevCh3StopSignal = false;
            }
            if (chCount < 4)
            {
                _prevCh4StartSignal = false;
                _prevCh4StopSignal = false;
            }
        }

        private void HandleFullTestHoldAbort(bool ch1Start, bool ch2Start, bool ch3Start, bool ch4Start)
        {
            var now = DateTime.UtcNow;
            int chCount = GetConfiguredChannelCount();
            HandleFullTestHoldAbortForChannel(1, ch1Start, now);
            HandleFullTestHoldAbortForChannel(2, ch2Start, now);
            if (chCount >= 3)
                HandleFullTestHoldAbortForChannel(3, ch3Start, now);
            if (chCount >= 4)
                HandleFullTestHoldAbortForChannel(4, ch4Start, now);
        }

        private int GetConfiguredChannelCount()
        {
            var count = _latestAppConfig?.ChannelCount ?? 4;
            if (count == 2 || count == 3) return count;
            return 4;
        }

        private void HandleFullTestHoldAbortForChannel(int channel, bool startSignal, DateTime now)
        {
            bool isRunning = false;
            if (_testControl != null)
            {
                Dispatcher.Invoke(() =>
                {
                    isRunning = _testControl.IsChannelRunning(channel);
                });
            }

            if (!isRunning || !startSignal)
            {
                _fullTestHoldStartTimes[channel] = null;
                return;
            }

            if (!_fullTestHoldStartTimes.TryGetValue(channel, out var holdStart) || holdStart == null)
            {
                _fullTestHoldStartTimes[channel] = now;
                return;
            }

            if (now - holdStart.Value >= TimeSpan.FromSeconds(2))
            {
                _fullTestHoldStartTimes[channel] = null;
                Dispatcher.InvokeAsync(async () =>
                {
                    await ResetResultBitsAsync(channel);
                    await SetChannelRunningBitAsync(channel, false);
                    _testControl?.StopChannelFromPlc(channel);
                });
            }
        }

        private async Task OnChannelStartSignalAsync(int channel)
        {
            try
            {
                if (_currentUser == null)
                {
                    return;
                }


                ShowTestControl();

                if (_testControl == null || _testControl.IsChannelRunning(channel))
                {
                    return;
                }

                await ResetResultBitsAsync(channel);
                _ = _testControl.StartChannelFromPlcAsync(channel);
            }
            catch (Exception ex)
            {
                _logService.LogError($"处理通道{channel}启动信号失败", ex);
            }
        }

        private async Task OnChannelStopSignalAsync(int channel)
        {
            try
            {

                await ResetResultBitsAsync(channel);
                await SetChannelRunningBitAsync(channel, false);

                if (_testControl != null && _testControl.IsChannelRunning(channel))
                {
                    _testControl.StopChannelFromPlc(channel);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError($"处理通道{channel}停止信号失败", ex);
            }
        }


        private Task SetChannelRunningBitAsync(int channel, bool isRunning)
        {
            var manual = GetManualControlConfig(channel);
            if (manual == null)
            {
                return Task.CompletedTask;
            }

            return UpdateConfiguredPlcBitAsync(manual.FullTestLightAddress, isRunning);
        }

        private async Task SetTestResultBitsAsync(int channel, bool? pass)
        {
            var manual = GetManualControlConfig(channel);
            if (manual == null)
            {
                return;
            }

            string passAddress = manual.TestOkLightAddress;
            string failAddress = manual.TestNgLightAddress;

            CancelNgPulse(channel);

            if (pass == true)
            {
                await Task.WhenAll(
                    UpdateConfiguredPlcBitAsync(passAddress, true),
                    UpdateConfiguredPlcBitAsync(failAddress, false));
                return;
            }

            if (pass == false)
            {
                await Task.WhenAll(
                    UpdateConfiguredPlcBitAsync(passAddress, false),
                    UpdateConfiguredPlcBitAsync(failAddress, true));

                var cts = new CancellationTokenSource();
                _ngPulseTokens[channel] = cts;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                    await UpdateConfiguredPlcBitAsync(failAddress, false);
                }
                catch (TaskCanceledException)
                {
                    // 已取消，无需处理
                }
                finally
                {
                    if (_ngPulseTokens.TryGetValue(channel, out var current) && current == cts)
                    {
                        current.Dispose();
                        _ngPulseTokens.Remove(channel);
                    }
                }
                return;
            }

            await Task.WhenAll(
                UpdateConfiguredPlcBitAsync(passAddress, false),
                UpdateConfiguredPlcBitAsync(failAddress, false));
        }

        private Task ResetResultBitsAsync(int channel)
        {
            return SetTestResultBitsAsync(channel, null);
        }

        private void CancelNgPulse(int channel)
        {
            if (_ngPulseTokens.TryGetValue(channel, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _ngPulseTokens.Remove(channel);
            }
        }

        private ManualControlAddressConfig? GetManualControlConfig(int channel)
        {
            var model = _currentModelForMapping ?? _testControl?.SelectedModel;
            if (model == null)
            {
                return null;
            }

            return channel switch
            {
                1 => model.Channel1Config?.ManualControl,
                2 => model.Channel2Config?.ManualControl,
                3 => model.Channel3Config?.ManualControl,
                4 => model.Channel4Config?.ManualControl,
                _ => null
            };
        }

        private Task UpdateConfiguredPlcBitAsync(string address, bool value)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return Task.CompletedTask;
            }

            if (!IsModbusBitAddress(address))
            {
                return Task.CompletedTask;
            }

            return UpdatePlcBitAsync(address, value);
        }

        private static bool IsModbusBitAddress(string address)
        {
            return address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                   || address.StartsWith("1x", StringComparison.OrdinalIgnoreCase);
        }

        private async Task UpdatePlcBitAsync(string address, bool value)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            _desiredPlcBits[address] = value;

            if (_cachedPlcBits.TryGetValue(address, out bool current) && current == value && !_pendingPlcBitUpdates.Contains(address))
            {
                return;
            }

            if (!_plcService.IsConnected)
            {
                _pendingPlcBitUpdates.Add(address);
                return;
            }

            try
            {
                await _plcService.WriteBitAsync(address, value);
                _cachedPlcBits[address] = value;
                _pendingPlcBitUpdates.Remove(address);
            }
            catch (Exception ex)
            {
                _logService.LogError($"写入PLC位{address}失败", ex);
                _pendingPlcBitUpdates.Add(address);
            }
        }

        private async Task ApplyPendingPlcBitsInternalAsync()
        {
            var snapshot = _desiredPlcBits.ToArray();
            foreach (var kvp in snapshot)
            {
                await UpdatePlcBitAsync(kvp.Key, kvp.Value);
            }
        }

        private void UpdateUIWithPLCData()
        {
            try
            {
                var uiSnapshot = BuildUiSnapshot();

                if (MainContentControl.Content is TestControl testControl)
                {
                    testControl.UpdateWithPLCData(uiSnapshot);
                }
                else if (MainContentControl.Content is ManualControl manualControl)
                {
                    manualControl.UpdateWithPLCData(uiSnapshot);
                }

                // 更新状态显示
                TxtPLCStatus.Text = $"PLC: {(_plcService.IsConnected ? "已连接" : "未连接")}";
                TxtPLCStatus.Foreground = _plcService.IsConnected ? Brushes.Green : Brushes.Red;
            }
            catch (Exception ex)
            {
                _logService.LogError("UI更新错误", ex);
            }
        }

        private PLCData BuildUiSnapshot()
        {
            PLCData snapshot;
            lock (_plcDataLock)
            {
                snapshot = DeepCopyPLCData(_currentPlcData);
            }


            return snapshot;
        }

        private ChannelData? GetChannelData(PLCData data, int channel)
        {
            return channel switch
            {
                1 => data.Channel1,
                2 => data.Channel2,
                3 => data.Channel3,
                4 => data.Channel4,
                _ => null
            };
        }


        public async Task<bool> WritePLCBit(string address, bool value)
        {
            try
            {
                await _plcService.WriteBitAsync(address, value);

                // 写入成功后，更新本地数据结构
                lock (_plcDataLock)
                {
                    UpdateLocalDataAfterWrite(address, value);
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"PLC写入错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void UpdateLocalDataAfterWrite(string address, bool value)
        {
            if (!address.StartsWith("M"))
            {
                return;
            }

            if (!int.TryParse(address.Substring(1), out int index))
            {
                return;
            }

            if (PLCAddressMapper.TryUpdateChannelBit(_currentPlcData.Channel1, PLCAddressMapper.Channel1Addresses, index, value))
            {
                return;
            }

            if (PLCAddressMapper.TryUpdateChannelBit(_currentPlcData.Channel2, PLCAddressMapper.Channel2Addresses, index, value))
            {
                return;
            }

            if (PLCAddressMapper.TryUpdateChannelBit(_currentPlcData.Channel3, PLCAddressMapper.Channel3Addresses, index, value))
            {
                return;
            }

            PLCAddressMapper.TryUpdateChannelBit(_currentPlcData.Channel4, PLCAddressMapper.Channel4Addresses, index, value);
        }

        public PLCData GetCurrentPLCData()
        {
            lock (_plcDataLock)
            {
                return DeepCopyPLCData(_currentPlcData);
            }
        }


        public ProductModel? GetCurrentModelForManual()
        {
            return _currentModelForMapping ?? _testControl?.SelectedModel;
        }

        public int GetConfiguredChannelCountForUi()
        {
            return GetConfiguredChannelCount();
        }

        private PLCData DeepCopyPLCData(PLCData source)
        {
            if (source == null) return null;

            return new PLCData
            {
                Channel1 = DeepCopyChannelData(source.Channel1),
                Channel2 = DeepCopyChannelData(source.Channel2),
                Channel3 = DeepCopyChannelData(source.Channel3),
                Channel4 = DeepCopyChannelData(source.Channel4),
                System = DeepCopySystemData(source.System),
                LastUpdate = source.LastUpdate
            };
        }

        private ChannelData DeepCopyChannelData(ChannelData source)
        {
            if (source == null) return null;

            return new ChannelData
            {
                MassagePoints = (bool[])source.MassagePoints.Clone(),
                StopButton = source.StopButton,
                FullTestStart = source.FullTestStart,
                MassageStart = source.MassageStart,
                SideWingStart = source.SideWingStart,
                SparePoints = (bool[])source.SparePoints.Clone(),
                PowerOff = source.PowerOff,
                CylinderOpen = source.CylinderOpen,
                CylinderClose = source.CylinderClose,
                DriverSwitch = source.DriverSwitch,
                MassageKey = source.MassageKey,
                FullTestLight = source.FullTestLight,
                MassageLight = source.MassageLight,
                SideWingLight = source.SideWingLight,
                TestOKLight = source.TestOKLight,
                TestNGLight = source.TestNGLight,
                UpInflateDownDeflate = source.UpInflateDownDeflate,
                DownInflateUpDeflate = source.DownInflateUpDeflate,
                BothInflate = source.BothInflate,
                BothDeflate = source.BothDeflate,
                AirLeakStartButton = source.AirLeakStartButton,
                HighPressureInletValve = source.HighPressureInletValve,
                HighPressureExhaustValve = source.HighPressureExhaustValve,
                LowPressureInletValve = source.LowPressureInletValve,
                LowPressureExhaustValve = source.LowPressureExhaustValve,
                CommSingleSend = source.CommSingleSend,
                CommContinuousSend = source.CommContinuousSend,
                OutputSpare1 = source.OutputSpare1,
                OutputSpare2 = source.OutputSpare2,
                HeightRawValue = source.HeightRawValue,
                CurrentRawValue = source.CurrentRawValue,
                CommSendData = (ushort[])source.CommSendData.Clone(),
                CommRecvData = (ushort[])source.CommRecvData.Clone()
            };
        }

        private SystemData DeepCopySystemData(SystemData source)
        {
            if (source == null) return null;

            return new SystemData
            {
                TestStep = source.TestStep,
                TestResult = source.TestResult
            };
        }

        private void PlcService_OnError(object sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"PLC错误: {error}", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.Test))
            {
                return;
            }

            ShowTestControl();
        }


        private void BtnManual_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.Manual))
            {
                return;
            }

            ShowManualControl();
        }

        private void BtnModel_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.Model))
            {
                return;
            }

            ShowModelControl();
        }

        private void BtnReport_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.Report))
            {
                return;
            }

            ShowReportControl();
        }

        private void BtnUser_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.UserManagement))
            {
                return;
            }

            ShowUserControl();
        }

        private void BtnSystemSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.SystemSettings))
            {
                return;
            }

            ShowSystemSettingsControl();
        }

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            ShowHelpControl();
        }

        private void BtnSystemLog_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsurePermission(Permission.SystemLog))
            {
                return;
            }

            ShowSystemLogControl();
        }

        private void ShowTestControl()
        {
            try
            {
                if (_testControl == null)
                {
                    _testControl = new TestControl(_testService, _configService, _dbService, _mesService, _modbusService, _licenseService, _currentUser);
                    _testControl.SelectedModelChanged += model =>
                    {
                        _currentModelForMapping = model;
                    };
                }
                MainContentControl.Content = _testControl;
                ResetButtonStyles();
                BtnTest.Background = GetNavBrush("NavButtonSelectedBrush");
                _currentModelForMapping = _testControl.SelectedModel;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载测试控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void ShowManualControl()
        {
            try
            {
                if (_manualControl == null)
                {
                    _manualControl = new ManualControl(_commService, _logService);
                }

                _manualControl.ApplyManualConfig(GetCurrentModelForManual());
                _manualControl.UpdateWithPLCData(GetCurrentPLCData());
                MainContentControl.Content = _manualControl;
                ResetButtonStyles();
                BtnManual.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载手动调试控件失败", ex);
                MessageBox.Show($"加载手动调试控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowModelControl()
        {
            try
            {
                if (_modelControl == null)
                {
                    _modelControl = new ModelConfigControl(_configService);
                }
                MainContentControl.Content = _modelControl;
                ResetButtonStyles();
                BtnModel.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载机型控件失败", ex);
                MessageBox.Show($"加载机型控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowReportControl()
        {
            try
            {
                if (_reportControl == null)
                {
                    _reportControl = new ReportControl(_dbService, _configService);
                }
                MainContentControl.Content = _reportControl;
                ResetButtonStyles();
                BtnReport.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载报表控件失败", ex);
                MessageBox.Show($"加载报表控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowUserControl()
        {
            try
            {
                if (_userControl == null)
                {
                    _userControl = new UserManagementControl(_dbService, _logService);
                }
                MainContentControl.Content = _userControl;
                ResetButtonStyles();
                BtnUser.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载用户控件失败", ex);
                MessageBox.Show($"加载用户控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowSystemSettingsControl()
        {
            try
            {
                if (_systemSettingsControl == null)
                {
                    _systemSettingsControl = new SystemSettingsControl(_configService, _plcService, _mesService, _modbusService, _licenseService);
                    _systemSettingsControl.ConfigurationSaved += SystemSettingsControl_ConfigurationSaved;
                }
                MainContentControl.Content = _systemSettingsControl;
                ResetButtonStyles();
                BtnSystemSettings.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载系统设置控件失败", ex);
                MessageBox.Show($"加载系统设置控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowSystemLogControl()
        {
            try
            {
                _systemLogControl ??= new SystemLogControl();
                MainContentControl.Content = _systemLogControl;
                ResetButtonStyles();
                BtnSystemLog.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载系统日志控件失败", ex);
                MessageBox.Show($"加载系统日志控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SystemSettingsControl_ConfigurationSaved(object? sender, AppConfig config)
        {
            await ApplyMesIntegrationAsync(config).ConfigureAwait(false);
            ApplySerialConfig(config);
        }


        private void ShowHelpControl()
        {
            try
            {
                _helpControl ??= new HelpControl();
                MainContentControl.Content = _helpControl;
                ResetButtonStyles();
                BtnHelp.Background = GetNavBrush("NavButtonSelectedBrush");
            }
            catch (Exception ex)
            {
                _logService.LogError("加载使用帮助控件失败", ex);
                MessageBox.Show($"加载使用帮助控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetButtonStyles()
        {
            var defaultBrush = GetNavBrush("NavButtonDefaultBrush");
            BtnTest.Background = defaultBrush;
            BtnManual.Background = defaultBrush;
            BtnModel.Background = defaultBrush;
            BtnReport.Background = defaultBrush;
            BtnUser.Background = defaultBrush;
            BtnSystemSettings.Background = defaultBrush;
            BtnHelp.Background = defaultBrush;
            if (BtnSystemLog != null)
            {
                BtnSystemLog.Background = defaultBrush;
            }
        }

        private Brush GetNavBrush(string resourceKey)
        {
            return (Brush)FindResource(resourceKey);
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要注销当前用户吗？", "注销确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _currentUser = null;
                ShowLoginDialog();
            }
        }

        protected override async void OnClosed(EventArgs e)
        {
            try
            {
                if (_testControl != null)
                {
                    await _testControl.SavePersistentStateAsync();
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("保存测试界面状态失败", ex);
            }

            if (_plcService != null)
            {
                _plcService.OnConnectionChanged -= PlcService_OnConnectionChanged;
                _plcService.OnError -= PlcService_OnError;
            }

            if (_mesService != null)
            {
                _mesService.OnConnectionChanged -= MesService_OnConnectionChanged;
            }

            if (_modbusService != null)
            {
                _modbusService.OnServerStateChanged -= ModbusService_OnServerStateChanged;
            }

            if (_testService != null)
            {
                _testService.OnTestStageChanged -= TestService_OnStageChanged;
                _testService.OnTestCompleted -= TestService_OnTestCompleted;
                _testService.OnTestCompleted -= TestService_OnTestCompletedSaveRecord;
            }

            // 清理资源
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _timer = null;
            }

            // 断开PLC连接
            await StopPLCReadingAsync().ConfigureAwait(false);

            try
            {
                _testService?.StopAllTests();
                _testService?.Dispose();
                _serialPortService?.Dispose();
            }
            catch (Exception ex)
            {
                _logService.LogError("释放测试服务失败", ex);
            }

            if (_plcService != null)
            {
                try
                {
                    await _plcService.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logService.LogError("断开PLC连接失败", ex);
                }
            }

            try
            {
                _mesService?.Dispose();
            }
            catch (Exception ex)
            {
                _logService.LogError("释放MES服务失败", ex);
            }

            try
            {
                _modbusService?.Dispose();
            }
            catch (Exception ex)
            {
                _logService.LogError("释放Modbus服务失败", ex);
            }



            //_plcService?.Disconnect();

            base.OnClosed(e);

            Application.Current.Shutdown();
        }

        private async Task<bool> SendTestRecordToMesAsync(TestRecord record)
        {
            try
            {
                var config = _latestAppConfig ?? await _configService.LoadAppConfigAsync().ConfigureAwait(false);
                if (config == null)
                {
                    return false;
                }

                _latestAppConfig = config;

                if (config.MesIntegrationMode == MesIntegrationMode.ModbusServer)
                {
                    Dispatcher.Invoke(UpdateMesStatusBar);
                    return true;
                }

                bool result = await _mesService.SendTestRecordAsync(record, config).ConfigureAwait(false);
                if (!result)
                {
                    _logService.LogWarning("MES推送未成功，请检查配置或网络状态。");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logService.LogError("推送MES测试记录失败", ex);
                return false;
            }
        }
    }
}

