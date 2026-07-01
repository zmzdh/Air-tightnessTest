// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using AudioActuatorCanTest.UserControls;
using AudioActuatorCanTest.Services;
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Views;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Xml.Linq;
using System.Linq;

namespace AudioActuatorCanTest
{
    public partial class MainWindow : Window
    {
        private readonly PLCService _plcService;
        private readonly DatabaseService _dbService;
        private readonly ConfigService _configService;
        private readonly TestService _testService;
        private readonly CommService _commService;
        private readonly MesService _mesService;
        private readonly ModbusServerService _modbusService;
        private readonly ILogService _logService;
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
        private bool _prevCh1StartSignal;
        private bool _prevCh1StopSignal;
        private bool _prevCh2StartSignal;
        private bool _prevCh2StopSignal;
        private readonly Dictionary<string, bool> _desiredPlcBits = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _cachedPlcBits = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingPlcBitUpdates = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();

            // 初始化服务 - 使用单例模式获取PLCService实例
            _logService = LogService.Instance;
            _plcService = PLCService.Instance;
            _dbService = new DatabaseService(_logService);
            _configService = new ConfigService(_logService);
            _commService = new CommService(_plcService, _logService);
            _testService = new TestService(_plcService, _commService, _logService);
            _mesService = new MesService(_logService);
            _modbusService = new ModbusServerService(_logService);
            _mesService.OnConnectionChanged += MesService_OnConnectionChanged;
            _modbusService.OnServerStateChanged += ModbusService_OnServerStateChanged;
            MesService_OnConnectionChanged(_mesService, _mesService.IsConnected);
            _testService.OnTestStageChanged += TestService_OnStageChanged;
            _testService.OnTestCompleted += TestService_OnTestCompleted;
            _testService.OnTestCompleted += TestService_OnTestCompletedSaveRecord;

            // 订阅事件
            _plcService.OnConnectionChanged += PlcService_OnConnectionChanged;
            _plcService.OnError += PlcService_OnError;

            InitializeTimer();
            ShowLoginDialog();

            _ = InitializeIntegrationModeAsync();
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
                await _plcService.ConnectAsync(appConfig.PLCIPAddress, appConfig.PLCPort, appConfig.PLCStationId);
            }
            catch (Exception ex)
            {
                _logService.LogError("PLC 连接初始化失败", ex);
            }

            TxtStatus.Text = "系统就绪";
        }

        private void PlcService_OnConnectionChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = "系统就绪";
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
                        // 读取M寄存器 (M0-M127)
                        var mResults = await _plcService.ReadBitsAsync("M0", 128);

                        HandlePlcSignals(mResults);

                        // 读取到D299的寄存器数据
                        ushort[] allDResults = new ushort[300];

                        // 分批读取D寄存器
                        await ReadDRegistersInBatches(allDResults);

                        lock (_plcDataLock)
                        {
                            // 使用PLCAddressMapper将原始数据映射到结构化数据
                            PLCAddressMapper.MapMRegistersToStructuredData(mResults, _currentPlcData);
                            PLCAddressMapper.MapDRegistersToStructuredData(allDResults, _currentPlcData);

                            _currentPlcData.LastUpdate = DateTime.Now;
                        }

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

        private async Task ReadDRegistersInBatches(ushort[] allDResults)
        {
            try
            {
                await ReadRangeIntoArray(10, 8, allDResults);
                await ReadRangeIntoArray(160, 20, allDResults);
                await ReadRangeIntoArray(180, 20, allDResults);
                await ReadRangeIntoArray(200, 20, allDResults);

                for (int start = 260; start <= 292; start += 8)
                {
                    int count = Math.Min(8, allDResults.Length - start);
                    if (count <= 0)
                    {
                        break;
                    }

                    await ReadRangeIntoArray(start, count, allDResults);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("分批读取D寄存器错误", ex);
                // 出错时，将数组清零
                Array.Clear(allDResults, 0, allDResults.Length);
            }
        }

        private async Task ReadRangeIntoArray(int startAddress, int count, ushort[] target)
        {
            if (count <= 0 || target == null)
            {
                return;
            }

            try
            {
                short[] batch = await _plcService.ReadWordsAsync($"D{startAddress}", (ushort)count);
                if (batch == null)
                {
                    return;
                }

                int length = Math.Min(batch.Length, count);

                for (int i = 0; i < length; i++)
                {
                    int index = startAddress + i;
                    if (index >= 0 && index < target.Length)
                    {
                        target[index] = unchecked((ushort)batch[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError($"读取D{startAddress}起{count}个寄存器失败", ex);
            }
        }

        private void HandlePlcSignals(bool[] mRegisters)
        {
            if (mRegisters == null)
            {
                return;
            }

            bool ch1Start = IsBitSet(mRegisters, PLCAddressMapper.Channel1Addresses.FullTestStart);
            bool ch1Stop = IsBitSet(mRegisters, PLCAddressMapper.Channel1Addresses.StopButton);
            bool ch2Start = IsBitSet(mRegisters, PLCAddressMapper.Channel2Addresses.FullTestStart);
            bool ch2Stop = IsBitSet(mRegisters, PLCAddressMapper.Channel2Addresses.StopButton);

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
        }

        private static bool IsBitSet(bool[] source, int index)
        {
            return index >= 0 && index < source.Length && source[index];
        }

        private async Task OnChannelStartSignalAsync(int channel)
        {
            try
            {
                if (_currentUser == null)
                {
                    return;
                }

                if (IsManualControlActive())
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
                if (IsManualControlActive())
                {
                    return;
                }

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

        private bool IsManualControlActive()
        {
            return _manualControl != null && MainContentControl?.Content == _manualControl;
        }

        private Task SetChannelRunningBitAsync(int channel, bool isRunning)
        {
            string runningAddress = channel == 1 ? "M49" : "M65";
            string indicatorAddress = channel == 1 ? "M52" : "M68";

            return Task.WhenAll(
                UpdatePlcBitAsync(runningAddress, isRunning),
                UpdatePlcBitAsync(indicatorAddress, isRunning));
        }

        private Task SetTestResultBitsAsync(int channel, bool? pass)
        {
            string passAddress = channel == 1 ? "M55" : "M71";
            string failAddress = channel == 1 ? "M56" : "M72";

            if (pass == true)
            {
                return Task.WhenAll(UpdatePlcBitAsync(passAddress, true), UpdatePlcBitAsync(failAddress, false));
            }

            if (pass == false)
            {
                return Task.WhenAll(UpdatePlcBitAsync(passAddress, false), UpdatePlcBitAsync(failAddress, true));
            }

            return Task.WhenAll(UpdatePlcBitAsync(passAddress, false), UpdatePlcBitAsync(failAddress, false));
        }

        private Task ResetResultBitsAsync(int channel)
        {
            return SetTestResultBitsAsync(channel, null);
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

        private void UpdateUIWithPLCData()
        {
            try
            {
                // 更新状态显示
                // PLC 状态信息已隐藏
            }
            catch (Exception ex)
            {
                _logService.LogError("UI更新错误", ex);
            }
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
                FullTestLight = source.FullTestLight,
                MassageLight = source.MassageLight,
                SideWingLight = source.SideWingLight,
                TestOKLight = source.TestOKLight,
                TestNGLight = source.TestNGLight,
                UpInflateDownDeflate = source.UpInflateDownDeflate,
                DownInflateUpDeflate = source.DownInflateUpDeflate,
                BothInflate = source.BothInflate,
                BothDeflate = source.BothDeflate,
                CommSingleSend = source.CommSingleSend,
                CommContinuousSend = source.CommContinuousSend,
                OutputSpare1 = source.OutputSpare1,
                HeightValue = source.HeightValue,
                CurrentRawValue = source.CurrentRawValue,
                CommSendData = (ushort[])source.CommSendData.Clone(),
                CommRecvData = (ushort[])source.CommRecvData.Clone()
            };
        }


        private void PlcService_OnError(object sender, string error)
        {
            _logService.LogError("PLC 服务提示", new Exception(error));
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
                    _testControl = new TestControl(_testService, _configService, _dbService, _mesService, _modbusService, _currentUser);
                }
                MainContentControl.Content = _testControl;
                ResetButtonStyles();
                BtnTest.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
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
                    _manualControl = new ManualControl(_configService, _logService);
                }
                MainContentControl.Content = _manualControl;
                ResetButtonStyles();
                BtnManual.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载手动控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                BtnModel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
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
                BtnReport.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
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
                BtnUser.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
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
                    _systemSettingsControl = new SystemSettingsControl(_configService, _plcService, _mesService, _modbusService);
                    _systemSettingsControl.ConfigurationSaved += SystemSettingsControl_ConfigurationSaved;
                }
                MainContentControl.Content = _systemSettingsControl;
                ResetButtonStyles();
                BtnSystemSettings.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
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
                BtnSystemLog.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
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
        }

        private void ShowHelpControl()
        {
            try
            {
                _helpControl ??= new HelpControl();
                MainContentControl.Content = _helpControl;
                ResetButtonStyles();
                BtnHelp.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434C5E"));
            }
            catch (Exception ex)
            {
                _logService.LogError("加载使用帮助控件失败", ex);
                MessageBox.Show($"加载使用帮助控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetButtonStyles()
        {
            var defaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E3440"));
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
