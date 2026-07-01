// UserControls/ManualControl.xaml.cs
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Services;

namespace AudioActuatorCanTest.UserControls
{
    public partial class ManualControl : UserControl
    {
        private readonly ConfigService _configService;
        private readonly ILogService _logService;
        private AppConfig? _appConfig;
        private ModbusRtuConfig? _currentModbusConfig;
        private CanBusService? _canBusService;
        private ModbusRtuService? _modbusService;
        private int _currentCanChannel = -1;
        private int _currentCanBaudRate = -1;

        private class BaudOption
        {
            public int Value { get; }
            public string Label { get; }

            public BaudOption(int value)
            {
                Value = value;
                Label = FormatBaudLabel(value);
            }
        }

        public ManualControl(ConfigService configService)
            : this(configService, LogService.Instance)
        {
        }

        public ManualControl(ConfigService configService, ILogService? logService)
        {
            InitializeComponent();
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logService = logService ?? LogService.Instance;

            Loaded += ManualControl_Loaded;
            Unloaded += ManualControl_Unloaded;
        }

        private async void ManualControl_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadConfigAsync();
            await RefreshProbesAsync();
            ApplyDefaultValues();
        }

        private void ManualControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _canBusService?.Dispose();
            _modbusService?.Dispose();
        }

        private async Task LoadConfigAsync()
        {
            _appConfig = await _configService.LoadAppConfigAsync().ConfigureAwait(true);
            TxtFrequency.Text = (_appConfig?.Can?.DefaultFrequencyHz ?? 80).ToString();
            TxtStrength.Text = (_appConfig?.Can?.DefaultStrengthLow ?? 0).ToString();
            TxtLeftStrength.Text = (_appConfig?.Can?.DefaultStrengthLow ?? 0).ToString();
            TxtRightStrength.Text = (_appConfig?.Can?.DefaultStrengthLow ?? 0).ToString();
            TxtModeIndex.Text = "0";

            InitializeCanControls();

            if (_appConfig?.Swd != null)
            {
                string firmwareDir = _appConfig.Swd.FirmwareDirectory;
                if (!Path.IsPathRooted(firmwareDir))
                {
                    firmwareDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, firmwareDir);
                }

                if (Directory.Exists(firmwareDir))
                {
                    var latestHex = Directory.GetFiles(firmwareDir, "*.hex").OrderByDescending(File.GetCreationTime).FirstOrDefault();
                    TxtFirmwarePath.Text = latestHex ?? string.Empty;
                }
                else
                {
                    TxtFirmwarePath.Text = firmwareDir;
                }
            }
        }

        private void ApplyDefaultValues()
        {
            AppendSwdLog("已加载 SWD 与 CAN 默认配置。");
            AppendCanLog("等待 CAN 操作... 如需发送报文会自动初始化设备。");
            HideListenStatusHint();
        }

        private void InitializeCanControls()
        {
            var channelOptions = Enumerable.Range(0, 4).ToList();
            CmbCanChannel.ItemsSource = channelOptions;
            int defaultChannel = _appConfig?.Can?.LeftChannelIndex ?? 0;
            CmbCanChannel.SelectedItem = channelOptions.Contains(defaultChannel) ? defaultChannel : channelOptions.First();

            var baudOptions = new[] { 125000, 250000, 500000, 1000000 }.Select(b => new BaudOption(b)).ToList();
            CmbCanBaud.ItemsSource = baudOptions;
            CmbCanBaud.DisplayMemberPath = nameof(BaudOption.Label);
            CmbCanBaud.SelectedValuePath = nameof(BaudOption.Value);

            int defaultBaud = _appConfig?.Can?.BaudRate ?? baudOptions.First().Value;
            CmbCanBaud.SelectedValue = baudOptions.Any(b => b.Value == defaultBaud) ? defaultBaud : baudOptions.First().Value;
        }

        private async Task RefreshProbesAsync()
        {
            try
            {
                var result = await PyOcdService.ListProbesWithResultAsync();
                CmbProbes.ItemsSource = result.Probes;
                CmbProbes.DisplayMemberPath = nameof(ProbeInfo.DisplayName);
                if (result.Probes.Any())
                {
                    CmbProbes.SelectedIndex = 0;
                }
                AppendSwdLog(string.IsNullOrWhiteSpace(result.ProcessResult.Output)
                    ? "未检测到探头，请检查连接"
                    : result.ProcessResult.Output.Trim());
            }
            catch (Exception ex)
            {
                AppendSwdLog($"刷新探头失败: {ex.Message}");
            }
        }

        private async void RefreshProbes_Click(object sender, RoutedEventArgs e)
        {
            await RefreshProbesAsync();
        }

        private async void Erase_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSwdCommandAsync("擦除", (uid, timeout) => PyOcdService.EraseChip(uid, timeout), _appConfig?.Swd?.EraseTimeoutMs);
        }

        private async void Flash_Click(object sender, RoutedEventArgs e)
        {
            string? uid = GetSelectedProbeUid();
            if (string.IsNullOrWhiteSpace(uid))
            {
                AppendSwdLog("请选择探头后再烧录");
                return;
            }

            string path = TxtFirmwarePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                AppendSwdLog("固件路径无效或文件不存在");
                return;
            }

            int? timeout = _appConfig?.Swd?.ProgramTimeoutMs;
            AppendSwdLog($"开始烧录 {path}...");
            ProcessResult result = await PyOcdService.FlashHex(uid, path, timeout);
            AppendSwdLog(result.Output);
        }

        private async void Halt_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSwdCommandAsync("暂停", (uid, timeout) => PyOcdService.Halt(uid, timeout));
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSwdCommandAsync("运行", (uid, timeout) => PyOcdService.Run(uid, timeout));
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSwdCommandAsync("复位", (uid, timeout) => PyOcdService.Reset(uid, timeout));
        }

        private async Task ExecuteSwdCommandAsync(
            string actionName,
            Func<string, int?, Task<ProcessResult>> command,
            int? timeout = null)
        {
            string? uid = GetSelectedProbeUid();
            if (string.IsNullOrWhiteSpace(uid))
            {
                AppendSwdLog($"请选择探头后再{actionName}");
                return;
            }

            int? effectiveTimeout = timeout ?? _appConfig?.Swd?.ProgramTimeoutMs ?? _appConfig?.Swd?.EraseTimeoutMs;
            AppendSwdLog($"开始{actionName}...");
            ProcessResult result = await command(uid, effectiveTimeout);
            AppendSwdLog(result.Output);
        }

        private async void SendFrequency_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseInt(TxtFrequency.Text, out int freq) || !TryParseInt(TxtStrength.Text, out int strength))
            {
                AppendCanLog("请输入有效的频率和强度");
                return;
            }

            var can = await EnsureCanAsync();
            if (can == null) return;

            bool ok = await can.SendFrequencyStrengthAsync(freq, strength, CancellationToken.None);
            AppendCanLog(ok ? $"已发送频率 {freq}Hz / 强度 {strength} 报文" : "发送失败，请检查设备");
        }

        private async void SendPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseInt(TxtLeftStrength.Text, out int left) || !TryParseInt(TxtRightStrength.Text, out int right))
            {
                AppendCanLog("请输入有效的左右强度");
                return;
            }

            var can = await EnsureCanAsync();
            if (can == null) return;

            bool ok = await can.SendStrengthPresetAsync(left, right, CancellationToken.None);
            AppendCanLog(ok ? $"已发送左右强度报文 L={left}, R={right}" : "发送失败，请检查设备");
        }

        private async void SendMode_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseInt(TxtModeIndex.Text, out int mode) || mode < 0)
            {
                AppendCanLog("请输入有效的模式索引 (0-6)");
                return;
            }

            var can = await EnsureCanAsync();
            if (can == null) return;

            bool ok = await can.SendTestModeAsync((byte)mode, CancellationToken.None);
            AppendCanLog(ok ? $"已发送模式索引 {mode}" : "发送失败，请检查设备");
        }

        private async void ListenStatus_Click(object sender, RoutedEventArgs e)
        {
            var can = await EnsureCanAsync();
            if (can == null) return;

            ShowListenStatusHint("已进入监听状态，等待反馈报文，收到后会提示结果。", Brushes.DarkSlateBlue);
            AppendCanLog("开始监听状态帧，等待反馈...");
            using var cts = new CancellationTokenSource(_appConfig?.Can?.ListenTimeoutMs ?? 5000);
            bool ok = await can.WaitForSeatStatusAsync(_ => true, cts.Token);
            AppendCanLog(ok ? "已收到状态反馈报文" : "监听超时未收到期望报文");
            ShowListenStatusHint(ok ? "监听完成：已收到状态反馈报文。" : "监听超时：未收到期望报文，请确认设备状态后重试。", ok ? Brushes.ForestGreen : Brushes.DarkOrange);
        }

        private async void ReadPowerStatus_Click(object sender, RoutedEventArgs e)
        {
            var modbus = await EnsureModbusAsync();
            if (modbus == null) return;

            try
            {
                double voltage = await modbus.ReadVoltageAsync(CancellationToken.None).ConfigureAwait(true);
                double current = await modbus.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(true);
                UpdatePowerDisplay(voltage, current);
                AppendModbusLog($"读取电压/电流成功: {voltage:F2} V, {current:F3} A");
            }
            catch (Exception ex)
            {
                AppendModbusLog($"读取电压/电流失败: {ex.Message}");
            }
        }

        private async void PowerOn_Click(object sender, RoutedEventArgs e)
        {
            await TogglePowerAsync(true);
        }

        private async void PowerOff_Click(object sender, RoutedEventArgs e)
        {
            await TogglePowerAsync(false);
        }

        private async Task TogglePowerAsync(bool on)
        {
            var modbus = await EnsureModbusAsync();
            if (modbus == null) return;

            try
            {
                await modbus.SetPowerAsync(on, CancellationToken.None).ConfigureAwait(true);
                AppendModbusLog(on ? "已上电" : "已断电");
            }
            catch (Exception ex)
            {
                AppendModbusLog($"设置电源状态失败: {ex.Message}");
            }
        }

        private async void ActivateShortCircuit_Click(object sender, RoutedEventArgs e)
        {
            await ActivateSwitchAsync(true);
        }

        private async void ActivateOpenCircuit_Click(object sender, RoutedEventArgs e)
        {
            await ActivateSwitchAsync(false);
        }

        private async Task ActivateSwitchAsync(bool shortCircuit)
        {
            var modbus = await EnsureModbusAsync();
            if (modbus == null) return;

            try
            {
                if (shortCircuit)
                {
                    await modbus.ActivateShortCircuitAsync(CancellationToken.None).ConfigureAwait(true);
                    AppendModbusLog("已触发短路输出");
                }
                else
                {
                    await modbus.ActivateOpenCircuitAsync(CancellationToken.None).ConfigureAwait(true);
                    AppendModbusLog("已触发断路输出");
                }
            }
            catch (Exception ex)
            {
                AppendModbusLog($"操作输出开关失败: {ex.Message}");
            }
        }

        private async Task<CanBusService?> EnsureCanAsync()
        {
            if (!TryGetCanSelections(out int channel, out int baudRate))
            {
                return null;
            }

            if (_appConfig?.Can == null)
            {
                _appConfig = await _configService.LoadAppConfigAsync().ConfigureAwait(true);
            }

            if (_appConfig?.Can == null)
            {
                AppendCanLog("未能加载 CAN 配置");
                return null;
            }

            if (_canBusService == null || _currentCanChannel != channel || _currentCanBaudRate != baudRate)
            {
                _canBusService?.Dispose();
                _canBusService = new CanBusService(CreateCanConfig(baudRate), _logService, (uint)channel);
                _currentCanChannel = channel;
                _currentCanBaudRate = baudRate;
            }

            if (!_canBusService.Open())
            {
                AppendCanLog("CAN 设备初始化失败");
                return null;
            }

            return _canBusService;
        }

        private async Task<ModbusRtuService?> EnsureModbusAsync()
        {
            try
            {
                _appConfig = await _configService.LoadAppConfigAsync().ConfigureAwait(true);

                ModbusRtuConfig latestConfig = _appConfig.Modbus ?? new ModbusRtuConfig();
                if (_currentModbusConfig == null || !ModbusConfigEquals(_currentModbusConfig, latestConfig))
                {
                    _modbusService?.Dispose();
                    _currentModbusConfig = CloneModbusConfig(latestConfig);
                    _modbusService = new ModbusRtuService(_currentModbusConfig, _logService);
                }

                await _modbusService.OpenAsync(CancellationToken.None).ConfigureAwait(true);
                return _modbusService;
            }
            catch (Exception ex)
            {
                AppendModbusLog($"初始化 Modbus 失败: {ex.Message}");
                return null;
            }
        }

        private static ModbusRtuConfig CloneModbusConfig(ModbusRtuConfig source)
        {
            return new ModbusRtuConfig
            {
                PortName = source.PortName,
                BaudRate = source.BaudRate,
                DataBits = source.DataBits,
                Parity = source.Parity,
                StopBits = source.StopBits,
                PowerDeviceAddress = source.PowerDeviceAddress,
                SwitchDeviceAddress = source.SwitchDeviceAddress,
                DeviceAddress = source.DeviceAddress,
                ReadTimeoutMs = source.ReadTimeoutMs,
                VoltageRegister = source.VoltageRegister,
                CurrentRegister = source.CurrentRegister,
                PowerControlRegister = source.PowerControlRegister,
                ShortCircuitCoil = source.ShortCircuitCoil,
                OpenCircuitCoil = source.OpenCircuitCoil
            };
        }

        private static bool ModbusConfigEquals(ModbusRtuConfig? left, ModbusRtuConfig? right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.PortName, right.PortName, StringComparison.OrdinalIgnoreCase)
                   && left.BaudRate == right.BaudRate
                   && left.DataBits == right.DataBits
                   && string.Equals(left.Parity, right.Parity, StringComparison.OrdinalIgnoreCase)
                   && left.StopBits == right.StopBits
                   && left.PowerDeviceAddress == right.PowerDeviceAddress
                   && left.SwitchDeviceAddress == right.SwitchDeviceAddress
                   && left.DeviceAddress == right.DeviceAddress
                   && left.ReadTimeoutMs == right.ReadTimeoutMs
                   && left.VoltageRegister == right.VoltageRegister
                   && left.CurrentRegister == right.CurrentRegister
                   && left.PowerControlRegister == right.PowerControlRegister
                   && left.ShortCircuitCoil == right.ShortCircuitCoil
                   && left.OpenCircuitCoil == right.OpenCircuitCoil;
        }

        private string? GetSelectedProbeUid()
        {
            if (CmbProbes.SelectedItem is ProbeInfo probe)
            {
                return probe.UniqueId;
            }

            return null;
        }

        private void SelectFirmware_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择固件文件",
                Filter = "固件文件 (*.hex;*.bin)|*.hex;*.bin|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            string currentPath = TxtFirmwarePath.Text.Trim();
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                try
                {
                    string? directory = Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        dialog.InitialDirectory = directory;
                    }
                }
                catch
                {
                    // ignored
                }

                dialog.FileName = Path.GetFileName(currentPath);
            }

            if (dialog.ShowDialog() == true)
            {
                TxtFirmwarePath.Text = dialog.FileName;
                AppendSwdLog($"已选择固件: {dialog.FileName}");
            }
        }

        private void ClearSwdLog_Click(object sender, RoutedEventArgs e)
        {
            TxtSwdLog.Clear();
        }

        private static bool TryParseInt(string text, out int value)
        {
            return int.TryParse(text?.Trim(), out value);
        }

        private bool TryGetCanSelections(out int channelIndex, out int baudRate)
        {
            channelIndex = -1;
            baudRate = -1;

            if (CmbCanChannel.SelectedItem is int channel)
            {
                channelIndex = channel;
            }
            else if (!int.TryParse(CmbCanChannel.Text, out channelIndex))
            {
                AppendCanLog("请输入有效的 CAN 通道");
                return false;
            }

            if (CmbCanBaud.SelectedValue is int baud)
            {
                baudRate = baud;
            }
            else if (CmbCanBaud.SelectedItem is BaudOption baudOption)
            {
                baudRate = baudOption.Value;
            }
            else if (!TryParseBaudRate(CmbCanBaud.Text, out baudRate))
            {
                AppendCanLog("请输入有效的 CAN 波特率");
                return false;
            }

            if (channelIndex < 0)
            {
                AppendCanLog("CAN 通道必须为非负整数");
                return false;
            }

            if (baudRate <= 0)
            {
                AppendCanLog("CAN 波特率必须为正整数");
                return false;
            }

            return true;
        }

        private CanTestConfig CreateCanConfig(int baudRate)
        {
            var source = _appConfig?.Can ?? new CanTestConfig();
            return new CanTestConfig
            {
                BaudRate = baudRate,
                LeftChannelIndex = source.LeftChannelIndex,
                RightChannelIndex = source.RightChannelIndex,
                ListenTimeoutMs = source.ListenTimeoutMs,
                ListenRetryCount = source.ListenRetryCount,
                DefaultFrequencyHz = source.DefaultFrequencyHz,
                DefaultStrengthLow = source.DefaultStrengthLow,
                DefaultStrengthMid = source.DefaultStrengthMid,
                DefaultStrengthHigh = source.DefaultStrengthHigh,
                CurrentTargetLowMa = source.CurrentTargetLowMa,
                CurrentTargetMidMa = source.CurrentTargetMidMa,
                CurrentTargetHighMa = source.CurrentTargetHighMa,
                CurrentToleranceMa = source.CurrentToleranceMa,
                VoltageMin = source.VoltageMin,
                VoltageMax = source.VoltageMax,
                PassengerOnlyShortOpenTest = source.PassengerOnlyShortOpenTest
            };
        }

        private async void OpenCan_Click(object sender, RoutedEventArgs e)
        {
            var can = await EnsureCanAsync();
            if (can != null)
            {
                AppendCanLog($"CAN 设备已打开 (通道 {_currentCanChannel}, 波特率 {FormatBaudLabel(_currentCanBaudRate)})");
            }
        }

        private void CloseCan_Click(object sender, RoutedEventArgs e)
        {
            _canBusService?.Close();
            AppendCanLog("CAN 设备已关闭");
            HideListenStatusHint();
        }

        private void ClearCanLog_Click(object sender, RoutedEventArgs e)
        {
            TxtCanLog.Clear();
        }

        private void ClearModbusLog_Click(object sender, RoutedEventArgs e)
        {
            TxtModbusLog.Clear();
        }

        private void ShowListenStatusHint(string message, Brush brush)
        {
            TxtListenStatusHint.Text = message;
            TxtListenStatusHint.Foreground = brush;
            ListenStatusHint.Visibility = Visibility.Visible;
        }

        private void HideListenStatusHint()
        {
            ListenStatusHint.Visibility = Visibility.Collapsed;
        }

        private void AppendSwdLog(string message)
        {
            AppendLog(TxtSwdLog, message);
        }

        private void AppendCanLog(string message)
        {
            AppendLog(TxtCanLog, message);
        }

        private void AppendModbusLog(string message)
        {
            AppendLog(TxtModbusLog, message);
        }

        private void UpdatePowerDisplay(double voltage, double current)
        {
            TxtVoltageValue.Text = $"{voltage:F2}";
            TxtCurrentValue.Text = $"{current:F3}";
        }

        private static void AppendLog(TextBox textBox, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            textBox.AppendText(line + Environment.NewLine);
            textBox.ScrollToEnd();
        }

        private static bool TryParseBaudRate(string? text, out int baudRate)
        {
            baudRate = -1;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = text.Trim().ToUpperInvariant();
            if (normalized.EndsWith("K", StringComparison.Ordinal))
            {
                if (int.TryParse(normalized[..^1], out int value))
                {
                    baudRate = value * 1000;
                    return true;
                }
            }
            else if (normalized.EndsWith("M", StringComparison.Ordinal))
            {
                if (int.TryParse(normalized[..^1], out int value))
                {
                    baudRate = value * 1_000_000;
                    return true;
                }
            }
            else if (int.TryParse(normalized, out int plainValue))
            {
                baudRate = plainValue;
                return true;
            }

            return false;
        }

        private static string FormatBaudLabel(int baudRate)
        {
            if (baudRate >= 1_000_000)
            {
                return $"{baudRate / 1_000_000}M";
            }

            return $"{baudRate / 1000}K";
        }
    }
}
