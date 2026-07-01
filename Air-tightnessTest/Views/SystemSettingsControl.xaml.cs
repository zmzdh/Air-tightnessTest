using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.UserControls
{
    public partial class SystemSettingsControl : UserControl
    {
        private readonly ConfigService _configService;
        private readonly PLCService _plcService;
        private readonly MesService _mesService;
        private readonly ModbusServerService _modbusService;
        private readonly LicenseService _licenseService;
        private AppConfig? _currentConfig;
        private bool _isLoading;
        private bool _mesEventSubscribed;
        private bool _modbusEventSubscribed;
        private static readonly string[] SupportedMesProtocols = { "TCP", "UDP" };
        private static readonly string[] SupportedParities = { "None", "Odd", "Even", "Mark", "Space" };
        private static readonly string[] SupportedStopBits = { "One", "OnePointFive", "Two" };
        private static readonly int[] SupportedChannelCounts = { 2, 3, 4 };
        private static readonly MesModeOption[] MesModeOptions =
        {
            new MesModeOption(MesIntegrationMode.HttpPush, "HTTP 推送"),
            new MesModeOption(MesIntegrationMode.ModbusServer, "Modbus 服务端")
        };

        public event EventHandler<AppConfig>? ConfigurationSaved;

        public SystemSettingsControl(ConfigService configService, PLCService plcService, MesService mesService, ModbusServerService modbusService, LicenseService licenseService)
        {
            InitializeComponent();

            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _mesService = mesService ?? throw new ArgumentNullException(nameof(mesService));
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));
            _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));

            CmbMesProtocol.ItemsSource = SupportedMesProtocols;
            CmbMesProtocol.SelectionChanged += CmbMesProtocol_SelectionChanged;
            CmbMesProtocol.SelectedItem = SupportedMesProtocols[0];

            CmbMesMode.ItemsSource = MesModeOptions;
            CmbMesMode.DisplayMemberPath = nameof(MesModeOption.Display);
            CmbMesMode.SelectedValuePath = nameof(MesModeOption.Mode);
            CmbMesMode.SelectionChanged += CmbMesMode_SelectionChanged;
            CmbMesMode.SelectedValue = MesIntegrationMode.HttpPush;

            CmbSerial1Parity.ItemsSource = SupportedParities;
            CmbSerial2Parity.ItemsSource = SupportedParities;
            CmbSerial1StopBits.ItemsSource = SupportedStopBits;
            CmbSerial2StopBits.ItemsSource = SupportedStopBits;
            CmbChannelCount.ItemsSource = SupportedChannelCounts;

            Loaded += SystemSettingsControl_Loaded;
            Unloaded += SystemSettingsControl_Unloaded;
        }

        private async void SystemSettingsControl_Loaded(object sender, RoutedEventArgs e)
        {
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

            await LoadConfigurationAsync();
            UpdateIntegrationStatus();
            UpdateLicenseUi();
        }

        private void SystemSettingsControl_Unloaded(object sender, RoutedEventArgs e)
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
        }

        private async Task LoadConfigurationAsync()
        {
            if (_isLoading)
            {
                return;
            }

            _isLoading = true;
            try
            {
                _currentConfig = await _configService.LoadAppConfigAsync();
                if (_currentConfig != null)
                {
                    TxtPlcIp.Text = _currentConfig.PLCIPAddress;
                    TxtPlcPort.Text = _currentConfig.PLCPort.ToString();
                    TxtPlcInputCount.Text = _currentConfig.PlcDiscreteInputCount.ToString();
                    TxtPlcCoilCount.Text = _currentConfig.PlcCoilCount.ToString();
                    TxtMesIp.Text = _currentConfig.MesServerIp;
                    TxtMesPort.Text = _currentConfig.MesServerPort.ToString();
                    ApplyMesProtocolSelection(_currentConfig.MesProtocol);
                    TxtModbusIp.Text = string.IsNullOrWhiteSpace(_currentConfig.ModbusServerIp)
                        ? "0.0.0.0"
                        : _currentConfig.ModbusServerIp;
                    TxtModbusPort.Text = _currentConfig.ModbusServerPort.ToString();
                    ApplyMesModeSelection(_currentConfig.MesIntegrationMode);
                    ApplySerialConfig(_currentConfig);
                    CmbChannelCount.SelectedItem = SupportedChannelCounts.Contains(_currentConfig.ChannelCount) ? _currentConfig.ChannelCount : 4;
                }
                else
                {
                    ApplyMesProtocolSelection(null);
                    ApplyMesModeSelection(MesIntegrationMode.HttpPush);
                    TxtModbusIp.Text = "0.0.0.0";
                    TxtModbusPort.Text = "502";
                    TxtPlcInputCount.Text = "256";
                    TxtPlcCoilCount.Text = "128";
                    ApplySerialConfig(new AppConfig());
                    CmbChannelCount.SelectedItem = 4;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ApplyMesProtocolSelection(null);
                ApplyMesModeSelection(MesIntegrationMode.HttpPush);
                TxtModbusIp.Text = "0.0.0.0";
                TxtModbusPort.Text = "502";
                TxtPlcInputCount.Text = "256";
                TxtPlcCoilCount.Text = "128";
                ApplySerialConfig(new AppConfig());
                CmbChannelCount.SelectedItem = 4;
            }
            finally
            {
                _isLoading = false;
            }

            UpdateIntegrationModeUI();
        }

        private void MesService_OnConnectionChanged(object? sender, bool isConnected)
        {
            Dispatcher.Invoke(UpdateIntegrationStatus);
        }

        private void ModbusService_OnServerStateChanged(object? sender, bool isRunning)
        {
            Dispatcher.Invoke(UpdateIntegrationStatus);
        }

        private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildConfigFromInputs(out var config, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool plcConfigChanged = _currentConfig != null &&
                                     (!string.Equals(_currentConfig.PLCIPAddress, config.PLCIPAddress, StringComparison.OrdinalIgnoreCase) ||
                                      _currentConfig.PLCPort != config.PLCPort);

            try
            {
                bool saved = await _configService.SaveAppConfigAsync(config);
                if (saved)
                {
                    _currentConfig = config;
                    MessageBox.Show("配置保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);

                    ConfigurationSaved?.Invoke(this, config);

                    if (plcConfigChanged && _plcService.IsConnected)
                    {
                        MessageBox.Show("PLC参数已更新，请重新连接PLC以应用新的设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    MessageBox.Show("配置保存失败，请检查磁盘权限", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnReloadConfig_Click(object sender, RoutedEventArgs e)
        {
            await LoadConfigurationAsync();
        }

        private async void BtnMesConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildConfigFromInputs(out var config, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetMesControlsEnabled(false);

            if (config.MesIntegrationMode == MesIntegrationMode.ModbusServer)
            {
                TxtMesStatus.Text = "Modbus 状态：启用中...";
                TxtMesStatus.Foreground = Brushes.DarkOrange;

                try
                {
                    await _mesService.DisconnectAsync();
                    await _modbusService.ApplyConfigurationAsync(config);
                    _currentConfig = config;
                    await _configService.SaveAppConfigAsync(config);
                    MessageBox.Show("Modbus 服务已启用", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"启用 Modbus 服务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    UpdateIntegrationStatus();
            UpdateLicenseUi();
                    SetMesControlsEnabled(true);
                }

                return;
            }

            TxtMesStatus.Text = "MES 状态：启用中...";
            TxtMesStatus.Foreground = Brushes.DarkOrange;

            try
            {
                await _modbusService.StopServerAsync();
                var connected = await _mesService.ConnectAsync(config);
                if (connected)
                {
                    _currentConfig = config;
                    await _configService.SaveAppConfigAsync(config);
                    MessageBox.Show("MES 系统已启用", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
                UpdateIntegrationStatus();
            UpdateLicenseUi();
                SetMesControlsEnabled(true);
            }
        }

        private async void BtnMesDisconnect_Click(object sender, RoutedEventArgs e)
        {
            SetMesControlsEnabled(false);

            if (GetSelectedMode() == MesIntegrationMode.ModbusServer)
            {
                TxtMesStatus.Text = "Modbus 状态：停用中...";
                TxtMesStatus.Foreground = Brushes.DarkOrange;

                try
                {
                    await _modbusService.StopServerAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"停用 Modbus 服务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    UpdateIntegrationStatus();
            UpdateLicenseUi();
                    SetMesControlsEnabled(true);
                }

                return;
            }

            TxtMesStatus.Text = "MES 状态：停用中...";
            TxtMesStatus.Foreground = Brushes.DarkOrange;

            try
            {
                await _mesService.DisconnectAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停用 MES 系统失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateIntegrationStatus();
            UpdateLicenseUi();
                SetMesControlsEnabled(true);
            }
        }

        private async void BtnMesSimulatePush_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedMode() != MesIntegrationMode.HttpPush)
            {
                MessageBox.Show("当前为 Modbus 服务模式，无需进行 MES 推送操作。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_currentConfig == null)
            {
                MessageBox.Show("请先保存配置", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetMesControlsEnabled(false);
            TxtMesStatus.Text = "MES 状态：推送模拟中...";
            TxtMesStatus.Foreground = Brushes.DarkOrange;

            try
            {
                bool result = await _mesService.SimulatePushAsync(_currentConfig);
                if (result)
                {
                    UpdateIntegrationStatus();
            UpdateLicenseUi();
                    MessageBox.Show("模拟推送成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    UpdateIntegrationStatus();
            UpdateLicenseUi();
                    MessageBox.Show("模拟推送失败，请确认 MES 已启用", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateIntegrationStatus();
            UpdateLicenseUi();
                MessageBox.Show("模拟推送被取消", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UpdateIntegrationStatus();
            UpdateLicenseUi();
                MessageBox.Show($"模拟推送失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetMesControlsEnabled(true);
            }
        }

        private bool TryBuildConfigFromInputs(out AppConfig config, out string errorMessage)
        {
            errorMessage = string.Empty;

            string plcIpInput = TxtPlcIp.Text.Trim();
            string plcPortText = TxtPlcPort.Text.Trim();
            string plcInputCountText = TxtPlcInputCount.Text.Trim();
            string plcCoilCountText = TxtPlcCoilCount.Text.Trim();
            string mesIpInput = TxtMesIp.Text.Trim();
            string mesPortText = TxtMesPort.Text.Trim();
            string? selectedProtocol = CmbMesProtocol.SelectedItem as string;
            string modbusIpInput = TxtModbusIp.Text.Trim();
            string modbusPortText = TxtModbusPort.Text.Trim();
            MesIntegrationMode selectedMode = GetSelectedMode();
            string serial1Port = TxtSerial1Port.Text.Trim();
            string serial1BaudText = TxtSerial1Baud.Text.Trim();
            string serial1DataBitsText = TxtSerial1DataBits.Text.Trim();
            string serial1Parity = CmbSerial1Parity.SelectedItem as string ?? string.Empty;
            string serial1StopBits = CmbSerial1StopBits.SelectedItem as string ?? string.Empty;
            string serial2Port = TxtSerial2Port.Text.Trim();
            string serial2BaudText = TxtSerial2Baud.Text.Trim();
            string serial2DataBitsText = TxtSerial2DataBits.Text.Trim();
            string serial2Parity = CmbSerial2Parity.SelectedItem as string ?? string.Empty;
            string serial2StopBits = CmbSerial2StopBits.SelectedItem as string ?? string.Empty;
            var source = _currentConfig ?? new AppConfig();
            int channelCount = CmbChannelCount.SelectedItem is int cc && SupportedChannelCounts.Contains(cc) ? cc : source.ChannelCount;
            if (channelCount != 2 && channelCount != 3 && channelCount != 4)
            {
                channelCount = 4;
            }

            string plcIp = string.IsNullOrWhiteSpace(plcIpInput) ? source.PLCIPAddress : plcIpInput;

            if (string.IsNullOrWhiteSpace(plcIpInput) && !string.IsNullOrWhiteSpace(plcIp))
            {
                TxtPlcIp.Text = plcIp;
            }

            if (string.IsNullOrWhiteSpace(plcIp))
            {
                errorMessage = "PLC IP 地址不能为空";
                config = null!;
                return false;
            }

            int plcPort;
            if (string.IsNullOrWhiteSpace(plcPortText))
            {
                plcPort = source.PLCPort;
                if (plcPort <= 0 || plcPort > 65535)
                {
                    plcPort = 502;
                }

                TxtPlcPort.Text = plcPort.ToString();
            }
            else if (!int.TryParse(plcPortText, out plcPort) || plcPort <= 0 || plcPort > 65535)
            {
                errorMessage = "PLC 端口号必须是 1-65535 之间的数字";
                config = null!;
                return false;
            }

            if (!TryParsePlcCount(plcInputCountText, source.PlcDiscreteInputCount, out ushort plcInputCount))
            {
                errorMessage = "离散输入读取数量必须是 1-2000 之间的数字";
                config = null!;
                return false;
            }

            if (string.IsNullOrWhiteSpace(plcInputCountText))
            {
                TxtPlcInputCount.Text = plcInputCount.ToString();
            }

            if (!TryParsePlcCount(plcCoilCountText, source.PlcCoilCount, out ushort plcCoilCount))
            {
                errorMessage = "线圈读取数量必须是 1-2000 之间的数字";
                config = null!;
                return false;
            }

            if (string.IsNullOrWhiteSpace(plcCoilCountText))
            {
                TxtPlcCoilCount.Text = plcCoilCount.ToString();
            }

            string mesIpValue = string.IsNullOrWhiteSpace(mesIpInput) ? source.MesServerIp : mesIpInput;
            int mesPortValue = source.MesServerPort;
            string selectedProtocolValue = string.IsNullOrWhiteSpace(selectedProtocol)
                ? source.MesProtocol
                : selectedProtocol;

            if (selectedMode == MesIntegrationMode.HttpPush)
            {
                if (string.IsNullOrWhiteSpace(mesIpValue))
                {
                    errorMessage = "MES IP 地址不能为空";
                    config = null!;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(mesIpInput) && !string.IsNullOrWhiteSpace(mesIpValue))
                {
                    TxtMesIp.Text = mesIpValue;
                }

                if (string.IsNullOrWhiteSpace(mesPortText))
                {
                    mesPortValue = source.MesServerPort;
                    if (mesPortValue <= 0 || mesPortValue > 65535)
                    {
                        mesPortValue = 8080;
                    }

                    TxtMesPort.Text = mesPortValue.ToString();
                }
                else if (!int.TryParse(mesPortText, out mesPortValue) || mesPortValue <= 0 || mesPortValue > 65535)
                {
                    errorMessage = "MES 端口号必须是 1-65535 之间的数字";
                    config = null!;
                    return false;
                }

                if (!IsSupportedMesProtocol(selectedProtocolValue))
                {
                    selectedProtocolValue = SupportedMesProtocols[0];
                    ApplyMesProtocolSelection(selectedProtocolValue);
                }
            }
            else if (int.TryParse(mesPortText, out int parsedMesPort) && parsedMesPort > 0 && parsedMesPort <= 65535)
            {
                mesPortValue = parsedMesPort;
            }

            string normalizedProtocol = NormalizeMesProtocol(selectedProtocolValue);

            int modbusPortValue = source.ModbusServerPort;
            string modbusIpValue = string.IsNullOrWhiteSpace(modbusIpInput) ? source.ModbusServerIp : modbusIpInput;

            if (selectedMode == MesIntegrationMode.ModbusServer)
            {
                if (!int.TryParse(modbusPortText, out modbusPortValue) || modbusPortValue <= 0 || modbusPortValue > 65535)
                {
                    errorMessage = "Modbus 端口必须是 1-65535 之间的数字";
                    config = null!;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(modbusIpValue))
                {
                    modbusIpValue = "0.0.0.0";
                }
            }
            else
            {
                if (int.TryParse(modbusPortText, out int parsedModbusPort) && parsedModbusPort > 0 && parsedModbusPort <= 65535)
                {
                    modbusPortValue = parsedModbusPort;
                }

                if (string.IsNullOrWhiteSpace(modbusIpValue))
                {
                    modbusIpValue = source.ModbusServerIp;
                }
            }

            if (string.IsNullOrWhiteSpace(modbusIpValue))
            {
                modbusIpValue = "0.0.0.0";
            }

            var device1 = BuildSerialConfig(source.SerialDevice1 ?? SerialPortConfig.CreateDefaultDevice1(),
                serial1Port, serial1BaudText, serial1DataBitsText, serial1Parity, serial1StopBits);
            var device2 = BuildSerialConfig(source.SerialDevice2 ?? SerialPortConfig.CreateDefaultDevice2(),
                serial2Port, serial2BaudText, serial2DataBitsText, serial2Parity, serial2StopBits);

            config = new AppConfig
            {
                PLCIPAddress = plcIp,
                PLCPort = plcPort,
                PLCStationId = source.PLCStationId,
                PlcDiscreteInputCount = plcInputCount,
                PlcCoilCount = plcCoilCount,
                AutoSave = source.AutoSave,
                LastWorkOrder = source.LastWorkOrder,
                LastProductModel = source.LastProductModel,
                MesIntegrationMode = selectedMode,
                MesServerIp = mesIpValue,
                MesServerPort = mesPortValue,
                MesProtocol = normalizedProtocol,
                ModbusServerIp = modbusIpValue,
                ModbusServerPort = modbusPortValue,
                ChannelCount = channelCount,
                SerialDevice1 = device1,
                SerialDevice2 = device2
            };

            return true;
        }

        private void ApplySerialConfig(AppConfig config)
        {
            var device1 = config.SerialDevice1 ?? SerialPortConfig.CreateDefaultDevice1();
            var device2 = config.SerialDevice2 ?? SerialPortConfig.CreateDefaultDevice2();

            TxtSerial1Port.Text = device1.PortName;
            TxtSerial1Baud.Text = device1.BaudRate.ToString();
            TxtSerial1DataBits.Text = device1.DataBits.ToString();
            ApplyComboSelection(CmbSerial1Parity, SupportedParities, device1.Parity);
            ApplyComboSelection(CmbSerial1StopBits, SupportedStopBits, device1.StopBits);

            TxtSerial2Port.Text = device2.PortName;
            TxtSerial2Baud.Text = device2.BaudRate.ToString();
            TxtSerial2DataBits.Text = device2.DataBits.ToString();
            ApplyComboSelection(CmbSerial2Parity, SupportedParities, device2.Parity);
            ApplyComboSelection(CmbSerial2StopBits, SupportedStopBits, device2.StopBits);
        }

        private static SerialPortConfig BuildSerialConfig(SerialPortConfig fallback, string port, string baudText, string dataBitsText, string parity, string stopBits)
        {
            var config = new SerialPortConfig
            {
                PortName = string.IsNullOrWhiteSpace(port) ? fallback.PortName : port,
                BaudRate = TryParsePositiveInt(baudText, fallback.BaudRate),
                DataBits = TryParsePositiveInt(dataBitsText, fallback.DataBits),
                Parity = string.IsNullOrWhiteSpace(parity) ? fallback.Parity : parity,
                StopBits = string.IsNullOrWhiteSpace(stopBits) ? fallback.StopBits : stopBits
            };

            return config;
        }

        private static int TryParsePositiveInt(string input, int fallback)
        {
            return int.TryParse(input, out int value) && value > 0 ? value : fallback;
        }

        private static bool TryParsePlcCount(string input, ushort fallback, out ushort value)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                value = fallback;
                return value >= 1 && value <= 2000;
            }

            if (ushort.TryParse(input, out value))
            {
                return value >= 1 && value <= 2000;
            }

            value = fallback;
            return false;
        }

        private static void ApplyComboSelection(ComboBox comboBox, string[] items, string value)
        {
            if (comboBox == null)
            {
                return;
            }

            comboBox.SelectedItem = items.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }

        private void SetMesControlsEnabled(bool enabled)
        {
            if (!enabled)
            {
                BtnMesConnect.IsEnabled = false;
                BtnMesDisconnect.IsEnabled = false;
                BtnMesSimulatePush.IsEnabled = false;
                CmbMesProtocol.IsEnabled = false;
                return;
            }

            UpdateMesButtons();
        }

        private void UpdateIntegrationStatus()
        {
            var mode = GetSelectedMode();
            if (mode == MesIntegrationMode.ModbusServer)
            {
                bool running = _modbusService.IsRunning;
                TxtMesStatus.Text = running ? "Modbus 状态：已启用" : "Modbus 状态：未启用";
                TxtMesStatus.Foreground = running ? Brushes.Green : Brushes.Red;
            }
            else
            {
                bool isConnected = _mesService.IsConnected;
                TxtMesStatus.Text = isConnected ? "MES 状态：已启用" : "MES 状态：未启用";
                TxtMesStatus.Foreground = isConnected ? Brushes.Green : Brushes.Red;
            }

            UpdateMesButtons();
        }

        private void UpdateMesButtons()
        {
            var mode = GetSelectedMode();
            if (mode == MesIntegrationMode.ModbusServer)
            {
                BtnMesConnect.IsEnabled = !_modbusService.IsRunning;
                BtnMesDisconnect.IsEnabled = _modbusService.IsRunning;
                BtnMesSimulatePush.IsEnabled = false;
                CmbMesProtocol.IsEnabled = false;
            }
            else
            {
                bool isConnected = _mesService.IsConnected;
                BtnMesConnect.IsEnabled = !isConnected;
                BtnMesDisconnect.IsEnabled = isConnected;
                BtnMesSimulatePush.IsEnabled = isConnected;
                CmbMesProtocol.IsEnabled = true;
            }
        }

        private void UpdateIntegrationModeUI()
        {
            var mode = GetSelectedMode();
            Visibility httpVisibility = mode == MesIntegrationMode.HttpPush ? Visibility.Visible : Visibility.Collapsed;
            Visibility modbusVisibility = mode == MesIntegrationMode.ModbusServer ? Visibility.Visible : Visibility.Collapsed;

            LblMesIp.Visibility = httpVisibility;
            TxtMesIp.Visibility = httpVisibility;
            LblMesPort.Visibility = httpVisibility;
            TxtMesPort.Visibility = httpVisibility;
            LblMesProtocol.Visibility = httpVisibility;
            CmbMesProtocol.Visibility = httpVisibility;
            BtnMesSimulatePush.Visibility = httpVisibility;

            LblModbusIp.Visibility = modbusVisibility;
            TxtModbusIp.Visibility = modbusVisibility;
            LblModbusPort.Visibility = modbusVisibility;
            TxtModbusPort.Visibility = modbusVisibility;

            CmbMesProtocol.IsEnabled = mode == MesIntegrationMode.HttpPush;

            UpdateIntegrationStatus();
            UpdateLicenseUi();
        }

        private MesIntegrationMode GetSelectedMode()
        {
            if (CmbMesMode.SelectedValue is MesIntegrationMode mode)
            {
                return mode;
            }

            return MesIntegrationMode.HttpPush;
        }

        private void ApplyMesModeSelection(MesIntegrationMode mode)
        {
            bool previousLoading = _isLoading;
            _isLoading = true;
            try
            {
                CmbMesMode.SelectedValue = mode;
            }
            finally
            {
                _isLoading = previousLoading;
            }
        }

        private async void CmbMesMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
            {
                return;
            }

            MesIntegrationMode? previousMode = null;
            if (e.RemovedItems.Count > 0)
            {
                object removedItem = e.RemovedItems[0];
                if (removedItem is MesIntegrationMode removedMode)
                {
                    previousMode = removedMode;
                }
                else if (removedItem is MesModeOption removedOption)
                {
                    previousMode = removedOption.Mode;
                }
            }

            UpdateIntegrationModeUI();

            if (!await ApplySelectedMesModeAsync() && previousMode.HasValue)
            {
                ApplyMesModeSelection(previousMode.Value);
                UpdateIntegrationModeUI();
            }
        }

        private async Task<bool> ApplySelectedMesModeAsync()
        {
            if (!TryBuildConfigFromInputs(out var config, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateIntegrationStatus();
            UpdateLicenseUi();
                return false;
            }

            try
            {
                if (config.MesIntegrationMode == MesIntegrationMode.HttpPush && _modbusService.IsRunning)
                {
                    await _modbusService.StopServerAsync();
                }
                else if (config.MesIntegrationMode == MesIntegrationMode.ModbusServer && _mesService.IsConnected)
                {
                    await _mesService.DisconnectAsync();
                }

                _currentConfig = config;
                return true;
            }
            catch (Exception ex)
            {
                LogService.Instance.LogError("更新 MES 集成模式失败", ex);
                MessageBox.Show($"更新 MES 集成模式失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                UpdateIntegrationStatus();
            UpdateLicenseUi();
            }
        }

        private void ApplyMesProtocolSelection(string? protocol)
        {
            bool previousLoading = _isLoading;
            _isLoading = true;
            try
            {
                string normalized = NormalizeMesProtocol(protocol);
                CmbMesProtocol.SelectedItem = normalized;
            }
            finally
            {
                _isLoading = previousLoading;
            }
        }

        private static bool IsSupportedMesProtocol(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return Array.Exists(SupportedMesProtocols, p =>
                string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeMesProtocol(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return SupportedMesProtocols[0];
            }

            var trimmed = value.Trim();
            return IsSupportedMesProtocol(trimmed) ? trimmed.ToUpperInvariant() : SupportedMesProtocols[0];
        }

        private void CmbMesProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
            {
                return;
            }

            if (_currentConfig != null && CmbMesProtocol.SelectedItem is string selected && IsSupportedMesProtocol(selected))
            {
                _currentConfig.MesProtocol = NormalizeMesProtocol(selected);
            }
        }

        private sealed record MesModeOption(MesIntegrationMode Mode, string Display);


        private void UpdateLicenseUi()
        {
            bool isLicensed = _licenseService.IsLicenseValid(out _);
            BtnExportRequest.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
            BtnImportLicense.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
            TxtLicenseStatus.Text = _licenseService.GetLicenseDisplayText();
        }

        private void BtnExportRequest_Click(object sender, RoutedEventArgs e)
        {
            if (_licenseService.ExportRequestFile(out var error))
            {
                MessageBox.Show($"request.dat 导出成功：{_licenseService.RequestFilePath}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"导出 request.dat 失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnImportLicense_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "License 文件 (*.lic)|*.lic|所有文件 (*.*)|*.*",
                Title = "导入授权文件"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (!_licenseService.ImportLicenseFile(dialog.FileName, out var error))
            {
                MessageBox.Show($"导入 license.lic 失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_licenseService.IsLicenseValid(out var reason))
            {
                MessageBox.Show("授权导入成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateLicenseUi();
            }
            else
            {
                MessageBox.Show($"授权文件已导入，但校验失败: {reason}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

}

}
