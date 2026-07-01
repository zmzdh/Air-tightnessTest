using System;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Services;

namespace AudioActuatorCanTest.UserControls
{
    public partial class SystemSettingsControl : UserControl
    {
        private readonly ConfigService _configService;
        private readonly PLCService _plcService;
        private readonly MesService _mesService;
        private readonly ModbusServerService _modbusService;
        private AppConfig? _currentConfig;
        private bool _isLoading;
        private bool _mesEventSubscribed;
        private bool _modbusEventSubscribed;
        private static readonly string[] SupportedMesProtocols = { "TCP", "UDP" };
        private static readonly MesModeOption[] MesModeOptions =
        {
            new MesModeOption(MesIntegrationMode.HttpPush, "HTTP 推送"),
            new MesModeOption(MesIntegrationMode.ModbusServer, "Modbus 服务端")
        };

        public event EventHandler<AppConfig>? ConfigurationSaved;

        public SystemSettingsControl(ConfigService configService, PLCService plcService, MesService mesService, ModbusServerService modbusService)
        {
            InitializeComponent();

            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _mesService = mesService ?? throw new ArgumentNullException(nameof(mesService));
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));

            CmbMesProtocol.ItemsSource = SupportedMesProtocols;
            CmbMesProtocol.SelectionChanged += CmbMesProtocol_SelectionChanged;
            CmbMesProtocol.SelectedItem = SupportedMesProtocols[0];

            CmbMesMode.ItemsSource = MesModeOptions;
            CmbMesMode.DisplayMemberPath = nameof(MesModeOption.Display);
            CmbMesMode.SelectedValuePath = nameof(MesModeOption.Mode);
            CmbMesMode.SelectionChanged += CmbMesMode_SelectionChanged;
            CmbMesMode.SelectedValue = MesIntegrationMode.HttpPush;

            Loaded += SystemSettingsControl_Loaded;
            Unloaded += SystemSettingsControl_Unloaded;
        }

        private void RefreshSerialPorts(string? preferredPort = null)
        {
            var ports = SerialPort.GetPortNames()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CmbModbusPortName.ItemsSource = ports;

            string? matchedPort = ports.FirstOrDefault(p => string.Equals(p, preferredPort, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(matchedPort))
            {
                CmbModbusPortName.SelectedItem = matchedPort;
                return;
            }

            if (ports.Count > 0)
            {
                CmbModbusPortName.SelectedIndex = 0;
            }
            else
            {
                CmbModbusPortName.Text = preferredPort ?? string.Empty;
            }
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

            RefreshSerialPorts(_currentConfig?.Modbus?.PortName);
            await LoadConfigurationAsync();
            UpdateIntegrationStatus();
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
                    RefreshSerialPorts(_currentConfig.Modbus?.PortName);
                    TxtMesIp.Text = _currentConfig.MesServerIp;
                    TxtMesPort.Text = _currentConfig.MesServerPort.ToString();
                    ApplyMesProtocolSelection(_currentConfig.MesProtocol);
                    PopulateModbusInputs(_currentConfig.Modbus);
                    TxtModbusIp.Text = string.IsNullOrWhiteSpace(_currentConfig.ModbusServerIp)
                        ? "0.0.0.0"
                        : _currentConfig.ModbusServerIp;
                    TxtModbusPort.Text = _currentConfig.ModbusServerPort.ToString();
                    ApplyMesModeSelection(_currentConfig.MesIntegrationMode);
                }
                else
                {
                    ApplyMesProtocolSelection(null);
                    ApplyMesModeSelection(MesIntegrationMode.HttpPush);
                    PopulateModbusInputs(null);
                    TxtModbusIp.Text = "0.0.0.0";
                    TxtModbusPort.Text = "502";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ApplyMesProtocolSelection(null);
                ApplyMesModeSelection(MesIntegrationMode.HttpPush);
                PopulateModbusInputs(null);
                TxtModbusIp.Text = "0.0.0.0";
                TxtModbusPort.Text = "502";
            }
            finally
            {
                _isLoading = false;
            }

            UpdateIntegrationModeUI();
        }

        private void PopulateModbusInputs(ModbusRtuConfig? config)
        {
            var modbus = config ?? new ModbusRtuConfig();
            RefreshSerialPorts(modbus.PortName);
            TxtModbusBaudRate.Text = modbus.BaudRate.ToString();
            TxtModbusDataBits.Text = modbus.DataBits.ToString();
            TxtModbusParity.Text = modbus.Parity;
            TxtModbusStopBits.Text = modbus.StopBits.ToString();
            TxtModbusPowerDeviceAddress.Text = modbus.PowerDeviceAddress.ToString();
            TxtModbusSwitchDeviceAddress.Text = modbus.SwitchDeviceAddress.ToString();
            TxtModbusReadTimeout.Text = modbus.ReadTimeoutMs.ToString();
            TxtModbusVoltageRegister.Text = modbus.VoltageRegister.ToString();
            TxtModbusCurrentRegister.Text = modbus.CurrentRegister.ToString();
            TxtModbusPowerControlRegister.Text = modbus.PowerControlRegister.ToString();
            TxtModbusShortCircuitCoil.Text = modbus.ShortCircuitCoil.ToString();
            TxtModbusOpenCircuitCoil.Text = modbus.OpenCircuitCoil.ToString();
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
                    MessageBox.Show("模拟推送成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    UpdateIntegrationStatus();
                    MessageBox.Show("模拟推送失败，请确认 MES 已启用", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateIntegrationStatus();
                MessageBox.Show("模拟推送被取消", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UpdateIntegrationStatus();
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

            string mesIpInput = TxtMesIp.Text.Trim();
            string mesPortText = TxtMesPort.Text.Trim();
            string? selectedProtocol = CmbMesProtocol.SelectedItem as string;
            string modbusIpInput = TxtModbusIp.Text.Trim();
            string modbusPortText = TxtModbusPort.Text.Trim();
            string modbusPortNameInput = CmbModbusPortName.Text.Trim();
            string modbusBaudRateText = TxtModbusBaudRate.Text.Trim();
            string modbusDataBitsText = TxtModbusDataBits.Text.Trim();
            string modbusParityInput = TxtModbusParity.Text.Trim();
            string modbusStopBitsText = TxtModbusStopBits.Text.Trim();
            string modbusPowerDeviceAddressText = TxtModbusPowerDeviceAddress.Text.Trim();
            string modbusSwitchDeviceAddressText = TxtModbusSwitchDeviceAddress.Text.Trim();
            string modbusReadTimeoutText = TxtModbusReadTimeout.Text.Trim();
            string modbusVoltageRegisterText = TxtModbusVoltageRegister.Text.Trim();
            string modbusCurrentRegisterText = TxtModbusCurrentRegister.Text.Trim();
            string modbusPowerControlRegisterText = TxtModbusPowerControlRegister.Text.Trim();
            string modbusShortCircuitCoilText = TxtModbusShortCircuitCoil.Text.Trim();
            string modbusOpenCircuitCoilText = TxtModbusOpenCircuitCoil.Text.Trim();
            MesIntegrationMode selectedMode = GetSelectedMode();

            var source = _currentConfig ?? new AppConfig();
            var sourceModbus = source.Modbus ?? new ModbusRtuConfig();
            string plcIp = source.PLCIPAddress;
            int plcPort = source.PLCPort;

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

            string modbusPortNameValue = string.IsNullOrWhiteSpace(modbusPortNameInput)
                ? sourceModbus.PortName
                : modbusPortNameInput;

            if (string.IsNullOrWhiteSpace(modbusPortNameValue))
            {
                errorMessage = "串口号不能为空";
                config = null!;
                return false;
            }

            if (!int.TryParse(modbusBaudRateText, out int modbusBaudRateValue) || modbusBaudRateValue <= 0)
            {
                modbusBaudRateValue = sourceModbus.BaudRate > 0 ? sourceModbus.BaudRate : 9600;
                TxtModbusBaudRate.Text = modbusBaudRateValue.ToString();
            }

            if (!int.TryParse(modbusDataBitsText, out int modbusDataBitsValue) || modbusDataBitsValue <= 0)
            {
                modbusDataBitsValue = sourceModbus.DataBits > 0 ? sourceModbus.DataBits : 8;
                TxtModbusDataBits.Text = modbusDataBitsValue.ToString();
            }

            string modbusParityValue = string.IsNullOrWhiteSpace(modbusParityInput)
                ? sourceModbus.Parity
                : modbusParityInput;
            if (string.IsNullOrWhiteSpace(modbusParityValue))
            {
                modbusParityValue = "N";
            }

            if (!int.TryParse(modbusStopBitsText, out int modbusStopBitsValue) || modbusStopBitsValue <= 0)
            {
                modbusStopBitsValue = sourceModbus.StopBits > 0 ? sourceModbus.StopBits : 1;
                TxtModbusStopBits.Text = modbusStopBitsValue.ToString();
            }

            if (!int.TryParse(modbusPowerDeviceAddressText, out int modbusPowerDeviceAddressValue) || modbusPowerDeviceAddressValue <= 0)
            {
                modbusPowerDeviceAddressValue = sourceModbus.PowerDeviceAddress == 0
                    ? (sourceModbus.DeviceAddress == 0 ? 1 : sourceModbus.DeviceAddress)
                    : sourceModbus.PowerDeviceAddress;
                TxtModbusPowerDeviceAddress.Text = modbusPowerDeviceAddressValue.ToString();
            }

            if (!int.TryParse(modbusSwitchDeviceAddressText, out int modbusSwitchDeviceAddressValue) || modbusSwitchDeviceAddressValue <= 0)
            {
                modbusSwitchDeviceAddressValue = sourceModbus.SwitchDeviceAddress == 0
                    ? (sourceModbus.DeviceAddress == 0 ? 1 : sourceModbus.DeviceAddress)
                    : sourceModbus.SwitchDeviceAddress;
                TxtModbusSwitchDeviceAddress.Text = modbusSwitchDeviceAddressValue.ToString();
            }

            if (!int.TryParse(modbusReadTimeoutText, out int modbusReadTimeoutValue) || modbusReadTimeoutValue <= 0)
            {
                modbusReadTimeoutValue = sourceModbus.ReadTimeoutMs > 0 ? sourceModbus.ReadTimeoutMs : 2000;
                TxtModbusReadTimeout.Text = modbusReadTimeoutValue.ToString();
            }

            if (!int.TryParse(modbusVoltageRegisterText, out int modbusVoltageRegisterValue) || modbusVoltageRegisterValue <= 0)
            {
                modbusVoltageRegisterValue = sourceModbus.VoltageRegister > 0 ? sourceModbus.VoltageRegister : 40002;
                TxtModbusVoltageRegister.Text = modbusVoltageRegisterValue.ToString();
            }

            if (!int.TryParse(modbusCurrentRegisterText, out int modbusCurrentRegisterValue) || modbusCurrentRegisterValue <= 0)
            {
                modbusCurrentRegisterValue = sourceModbus.CurrentRegister > 0 ? sourceModbus.CurrentRegister : 40001;
                TxtModbusCurrentRegister.Text = modbusCurrentRegisterValue.ToString();
            }

            if (!int.TryParse(modbusPowerControlRegisterText, out int modbusPowerControlRegisterValue) || modbusPowerControlRegisterValue <= 0)
            {
                modbusPowerControlRegisterValue = sourceModbus.PowerControlRegister > 0 ? sourceModbus.PowerControlRegister : 40018;
                TxtModbusPowerControlRegister.Text = modbusPowerControlRegisterValue.ToString();
            }

            if (!int.TryParse(modbusShortCircuitCoilText, out int modbusShortCircuitCoilValue) || modbusShortCircuitCoilValue <= 0)
            {
                modbusShortCircuitCoilValue = sourceModbus.ShortCircuitCoil > 0 ? sourceModbus.ShortCircuitCoil : 1;
                TxtModbusShortCircuitCoil.Text = modbusShortCircuitCoilValue.ToString();
            }

            if (!int.TryParse(modbusOpenCircuitCoilText, out int modbusOpenCircuitCoilValue) || modbusOpenCircuitCoilValue <= 0)
            {
                modbusOpenCircuitCoilValue = sourceModbus.OpenCircuitCoil > 0 ? sourceModbus.OpenCircuitCoil : 2;
                TxtModbusOpenCircuitCoil.Text = modbusOpenCircuitCoilValue.ToString();
            }

            config = new AppConfig
            {
                PLCIPAddress = plcIp,
                PLCPort = plcPort,
                PLCStationId = source.PLCStationId,
                AutoSave = source.AutoSave,
                LastWorkOrder = source.LastWorkOrder,
                LastProductModel = source.LastProductModel,
                MesIntegrationMode = selectedMode,
                MesServerIp = mesIpValue,
                MesServerPort = mesPortValue,
                MesProtocol = normalizedProtocol,
                ModbusServerIp = modbusIpValue,
                ModbusServerPort = modbusPortValue,
                Modbus = new ModbusRtuConfig
                {
                    PortName = modbusPortNameValue,
                    BaudRate = modbusBaudRateValue,
                    DataBits = modbusDataBitsValue,
                    Parity = modbusParityValue,
                    StopBits = modbusStopBitsValue,
                    PowerDeviceAddress = (byte)modbusPowerDeviceAddressValue,
                    SwitchDeviceAddress = (byte)modbusSwitchDeviceAddressValue,
                    ReadTimeoutMs = modbusReadTimeoutValue,
                    VoltageRegister = modbusVoltageRegisterValue,
                    CurrentRegister = modbusCurrentRegisterValue,
                    PowerControlRegister = modbusPowerControlRegisterValue,
                    ShortCircuitCoil = modbusShortCircuitCoilValue,
                    OpenCircuitCoil = modbusOpenCircuitCoilValue
                }
            };

            return true;
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
    }
}
