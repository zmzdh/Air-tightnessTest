// UserControls/ModelConfigControl.xaml.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;
using LumbarMassageTest.Views;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace LumbarMassageTest.UserControls
{
    public partial class ModelConfigControl : UserControl
    {
        private readonly ConfigService _configService;
        private readonly DatabaseService _databaseService;
        private List<ProductModel> _productModels;
        private ProductModel _currentModel;

        private ObservableCollection<LumbarActionEntry> _lumbarActions;
        private ObservableCollection<MassageActionEntry> _massageActions;
        private ObservableCollection<MessageKeyTestEntry> _messageKeyTests;
        private ObservableCollection<ManualControlEntry> _manualControlEntries;
        private MassageTestSettings _massageSettings = new MassageTestSettings();

        private MessageConfig _messageConfig = new MessageConfig();
        private readonly List<string> _lumbarAddressWarnings = new List<string>();
        private const int MaxMassagePointCount = 32;

        public List<LumbarActionOption> LumbarActionList { get; set; }
        public List<MessageKeyTriggerModeOption> MessageKeyTriggerModeOptions { get; } = new()
        {
            new MessageKeyTriggerModeOption(MessageKeyTriggerMode.Continuous, "杩炵画"),
            new MessageKeyTriggerModeOption(MessageKeyTriggerMode.Interval, "闂撮殧")
        };

        public ModelConfigControl(ConfigService configService)
        {
            InitializeComponent();
            _configService = configService;
            _databaseService = new DatabaseService();

            InitializeLumbarActionList();
            LoadProductModels();

            DataContext = this;
            InitializeMessageKeyTriggerModeColumn();
            ApplyChannelColumnVisibility();
        }

        private void ApplyChannelColumnVisibility()
        {
            int channelCount = (Application.Current.MainWindow as MainWindow)?.GetConfiguredChannelCountForUi() ?? 4;
            bool showCh3 = channelCount >= 3;
            bool showCh4 = channelCount >= 4;

            void UpdateColumns(DataGrid grid)
            {
                foreach (var column in grid.Columns)
                {
                    string header = column.Header?.ToString() ?? string.Empty;
                    if (header.Contains("閫氶亾3", StringComparison.Ordinal))
                    {
                        column.Visibility = showCh3 ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else if (header.Contains("閫氶亾4", StringComparison.Ordinal))
                    {
                        column.Visibility = showCh4 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }

            UpdateColumns(MessageKeyTestGrid);
            UpdateColumns(LumbarConfigGrid);
            UpdateColumns(ManualControlGrid);
            MassageView?.ApplyChannelColumnVisibility(channelCount);
        }

        private void InitializeMessageKeyTriggerModeColumn()
        {
            var triggerModeColumn = MessageKeyTestGrid.Columns
                .OfType<DataGridComboBoxColumn>()
                .FirstOrDefault(column => string.Equals(column.Header?.ToString(), "瑙﹀彂鏂瑰紡", StringComparison.Ordinal));

            if (triggerModeColumn != null)
            {
                triggerModeColumn.ItemsSource = MessageKeyTriggerModeOptions;
            }
        }

        private void InitializeLumbarActionList()
        {
            LumbarActionList = new List<LumbarActionOption>
            {
                new LumbarActionOption(LumbarActionType.UpInflateDownDeflate, "涓婂厖涓嬫斁"),
                new LumbarActionOption(LumbarActionType.DownInflateUpDeflate, "涓嬪厖涓婃斁"),
                new LumbarActionOption(LumbarActionType.SimultaneousInflate, "鍚屾椂鍏呮皵"),
                new LumbarActionOption(LumbarActionType.SimultaneousDeflate, "鍚屾椂鏀炬皵"),
                new LumbarActionOption(LumbarActionType.FrameHeaderSwitch, "甯уご鍒囨崲")
            };
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

        private static MassageTestSettings CloneMassageSettings(MassageTestSettings source)
        {
            if (source == null)
            {
                return new MassageTestSettings();
            }

            return new MassageTestSettings
            {
                TotalDuration = source.TotalDuration,
                HighLevelDurationMin = source.HighLevelDurationMin,
                HighLevelDurationMax = source.HighLevelDurationMax,
                MaxConcurrentPoints = source.MaxConcurrentPoints,
                PeakCurrentMin = source.PeakCurrentMin,
                PeakCurrentMax = source.PeakCurrentMax,
                AverageCurrentMin = source.AverageCurrentMin,
                AverageCurrentMax = source.AverageCurrentMax
            };
        }

        private static ushort[] NormalizeMessageArray(ushort[] source)
        {
            var result = new ushort[20];

            if (source != null)
            {
                for (int i = 0; i < Math.Min(source.Length, result.Length); i++)
                {
                    result[i] = source[i];
                }
            }

            return result;
        }

        private static string NormalizeLumbarAddresses(
            string? input,
            string channelLabel,
            int order,
            Action<string>? warningSink)
        {
            var parsed = ModbusAddressHelper.ParseAddressList(input).ToList();
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalid = new List<string>();

            foreach (var token in parsed)
            {
                var normalizedToken = ModbusAddressHelper.NormalizeBitAddressToken(token);
                if (normalizedToken != null && normalizedToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(normalizedToken))
                    {
                        normalized.Add(normalizedToken);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(token))
                {
                    invalid.Add(token);
                }
            }

            if (invalid.Count > 0)
            {
                string message = $"閫氶亾{channelLabel} 鑵版墭鍔ㄤ綔{order}鍖呭惈鏃犳晥瀵勫瓨鍣? {string.Join("銆?, invalid)}";
                if (normalized.Count > 0)
                {
                    message += $"锛屽凡淇濈暀鏈夋晥瀵勫瓨鍣? {string.Join("銆?, normalized)}";
                }

                warningSink?.Invoke(message + "銆?);
            }

            return string.Join(", ", normalized);
        }

        private static void LogLumbarAddressCorrection(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine($"[ModelConfig] {message}");
            }
        }

        private static string FormatMessagePreview(ushort[] message)
        {
            if (message == null)
            {
                return "鏈厤缃?;
            }

            var normalized = NormalizeMessageArray(message);
            return string.Join(" ", normalized.Select(v => v.ToString("X2")));
        }

        private void UpdateMessagePreview(string elementName, ushort[] message)
        {
            if (FindName(elementName) is TextBlock textBlock)
            {
                textBlock.Text = FormatMessagePreview(message);
            }
        }

        private void UpdateAllMessagePreviews()
        {
            UpdateMessagePreview("PowerOnMessagePreview", _messageConfig?.PowerOnMessage);
            UpdateMessagePreview("SleepMessagePreview", _messageConfig?.SleepMessage);
            UpdateMessagePreview("StopMessagePreview", _messageConfig?.StopMessage);
            UpdateMessagePreview("MassageMessagePreview", _messageConfig?.MassageMessage);
            UpdateMessagePreview("Massage2MessagePreview", _messageConfig?.MassageMessage2);
            UpdateMessagePreview("ReadMessagePreview", _messageConfig?.ReadMessage);
        }

        private void SyncMessageConfigsToModel()
        {
            if (_currentModel?.Channel1Config != null)
            {
                _currentModel.Channel1Config.MessageConfig = CloneMessageConfig(_messageConfig);
            }

            if (_currentModel?.Channel2Config != null)
            {
                _currentModel.Channel2Config.MessageConfig = CloneMessageConfig(_messageConfig);
            }

            if (_currentModel?.Channel3Config != null)
            {
                _currentModel.Channel3Config.MessageConfig = CloneMessageConfig(_messageConfig);
            }

            if (_currentModel?.Channel4Config != null)
            {
                _currentModel.Channel4Config.MessageConfig = CloneMessageConfig(_messageConfig);
            }
        }

        private void ConfigureMessage(MessageConfig targetConfig, Func<MessageConfig, ushort[]> getter,
            Action<MessageConfig, ushort[]> setter, string previewName)
        {
            if (targetConfig == null)
            {
                return;
            }

            var currentData = NormalizeMessageArray(getter(targetConfig));
            var dialog = new MessageConfigDialog(currentData);
            if (dialog.ShowDialog() == true)
            {
                var newData = NormalizeMessageArray(dialog.MessageData);
                setter(targetConfig, newData);
                UpdateMessagePreview(previewName, newData);
                SyncMessageConfigsToModel();
            }
        }

        private async Task SaveMessageConfigsToDatabaseAsync(ProductModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ModelName))
            {
                return;
            }

            try
            {
                int channelCount = GetConfiguredChannelCount();
                for (int channel = 1; channel <= channelCount; channel++)
                {
                    await _databaseService.SaveMessageConfigAsync(model.ModelName, channel, CloneMessageConfig(_messageConfig));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"淇濆瓨鎶ユ枃閰嶇疆鍒版暟鎹簱澶辫触: {ex.Message}", "閿欒", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private int GetConfiguredChannelCount()
        {
            return (Application.Current.MainWindow as MainWindow)?.GetConfiguredChannelCountForUi() ?? 4;
        }

        public record LumbarActionOption(LumbarActionType Value, string DisplayName);
        public record MessageKeyTriggerModeOption(MessageKeyTriggerMode Value, string Label);

        private class LumbarActionEntry
        {
            public int Order { get; set; }
            public LumbarActionType Action { get; set; }
            public double TargetHeight { get; set; }
            public int TargetTime { get; set; }
            public bool Enabled { get; set; } = true;
            public string Ch1RegisterAddress { get; set; } = string.Empty;
            public string Ch2RegisterAddress { get; set; } = string.Empty;
            public string Ch3RegisterAddress { get; set; } = string.Empty;
            public string Ch4RegisterAddress { get; set; } = string.Empty;
            public ushort[] SendMessage { get; set; } = new ushort[20];
        }

        private class MassageActionEntry
        {
            public int Point { get; set; }
            public string Ch1HeightSwitchAddress { get; set; } = string.Empty;
            public string Ch2HeightSwitchAddress { get; set; } = string.Empty;
            public string Ch3HeightSwitchAddress { get; set; } = string.Empty;
            public string Ch4HeightSwitchAddress { get; set; } = string.Empty;
            public bool Enabled { get; set; } = true;
        }

        private class MessageKeyTestEntry
        {
            public int Order { get; set; }
            public bool Enabled { get; set; } = true;
            public string Ch1OutputRegisterAddress { get; set; } = string.Empty;
            public string Ch2OutputRegisterAddress { get; set; } = string.Empty;
            public string Ch3OutputRegisterAddress { get; set; } = string.Empty;
            public string Ch4OutputRegisterAddress { get; set; } = string.Empty;
            public int ReadByteIndex { get; set; } = 21;
            public int ExpectedValue { get; set; }
            public MessageKeyTriggerMode TriggerMode { get; set; } = MessageKeyTriggerMode.Continuous;
        }

        private enum ManualControlKey
        {
            StopButton,
            FullTestStart,
            MassageStart,
            SideWingStart,
            PowerOff,
            ClampCylinder,
            SpareCylinder,
            DriverSwitch,
            MassageKey,
            FullTestLight,
            MassageLight,
            SideWingLight,
            TestOkLight,
            TestNgLight,
            AirLeakStartButton,
            HighPressureInletValve,
            HighPressureExhaustValve,
            LowPressureInletValve,
            LowPressureExhaustValve
        }

        private sealed record ManualControlDefinition(
            ManualControlKey Key,
            string Label,
            string TypeLabel,
            ModbusBitType ExpectedType,
            bool AllowAlternateType = false);

        private sealed class ManualControlEntry : INotifyPropertyChanged
        {
            private string _ch1Address = string.Empty;
            private string _ch2Address = string.Empty;
            private string _ch3Address = string.Empty;
            private string _ch4Address = string.Empty;

            public ManualControlEntry(ManualControlDefinition definition)
            {
                Key = definition.Key;
                Label = definition.Label;
                TypeLabel = definition.TypeLabel;
                ExpectedType = definition.ExpectedType;
                AllowAlternateType = definition.AllowAlternateType;
            }

            public ManualControlKey Key { get; }
            public string Label { get; }
            public string TypeLabel { get; }
            public ModbusBitType ExpectedType { get; }
            public bool AllowAlternateType { get; }

            public string Ch1Address
            {
                get => _ch1Address;
                set => SetField(ref _ch1Address, value);
            }

            public string Ch2Address
            {
                get => _ch2Address;
                set => SetField(ref _ch2Address, value);
            }

            public string Ch3Address
            {
                get => _ch3Address;
                set => SetField(ref _ch3Address, value);
            }

            public string Ch4Address
            {
                get => _ch4Address;
                set => SetField(ref _ch4Address, value);
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
            {
                if (field == value)
                {
                    return;
                }

                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private static readonly ManualControlDefinition[] ManualControlDefinitions =
        {
            new(ManualControlKey.StopButton, "鍋滄鎸夐挳", "1x", ModbusBitType.DiscreteInput),
            new(ManualControlKey.FullTestStart, "鍏ㄦ祴鍚姩", "1x", ModbusBitType.DiscreteInput),
            new(ManualControlKey.MassageStart, "鎸夋懇鍚姩", "1x", ModbusBitType.DiscreteInput),
            new(ManualControlKey.SideWingStart, "渚х考鍚姩", "1x", ModbusBitType.DiscreteInput),
            new(ManualControlKey.PowerOff, "鐢垫簮鍏抽棴", "0x", ModbusBitType.Coil),
            new(ManualControlKey.ClampCylinder, "澶圭揣姘旂几", "0x", ModbusBitType.Coil),
            new(ManualControlKey.SpareCylinder, "澶囩敤姘旂几", "0x", ModbusBitType.Coil),
            new(ManualControlKey.DriverSwitch, "涓诲壇椹惧垏鎹?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.MassageKey, "鎸夋懇寮€鍏?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.FullTestLight, "鍏ㄦ祴鎸夐挳鐏?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.MassageLight, "鎸夋懇鎸夐挳鐏?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.SideWingLight, "渚х考鎸夐挳鐏?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.TestOkLight, "娴嬭瘯OK鐏?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.TestNgLight, "娴嬭瘯NG鐏?, "0x", ModbusBitType.Coil),
            new(ManualControlKey.AirLeakStartButton, "气密启动按钮", "1x", ModbusBitType.DiscreteInput),
            new(ManualControlKey.HighPressureInletValve, "高压进气阀", "0x", ModbusBitType.Coil),
            new(ManualControlKey.HighPressureExhaustValve, "高压排气阀", "0x", ModbusBitType.Coil),
            new(ManualControlKey.LowPressureInletValve, "低压进气阀", "0x", ModbusBitType.Coil),
            new(ManualControlKey.LowPressureExhaustValve, "低压排气阀", "0x", ModbusBitType.Coil)
        };

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
                MessageBox.Show($"鍔犺浇浜у搧鍨嬪彿澶辫触: {ex.Message}", "閿欒",
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

        private async Task LoadModelConfigAsync()
        {
            if (_currentModel == null) return;

            // 鍩烘湰淇℃伅
            TxtModelName.Text = _currentModel.ModelName;
            TxtModelDesc.Text = _currentModel.Description ?? string.Empty;
            TxtImagePath.Text = _currentModel.ImagePath ?? string.Empty;

            if (!string.IsNullOrEmpty(_currentModel.ImagePath) && System.IO.File.Exists(_currentModel.ImagePath))
            {
                ModelImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_currentModel.ImagePath));
            }
            else
            {
                ModelImage.Source = null;
            }

            // 娴佺▼閰嶇疆
            var process = _currentModel.ProcessConfig ?? new TestProcessConfig();
            ChkEnableBarcode.IsChecked = process.EnableBarcodeCheck;
            ChkEnableCurrentMonitor.IsChecked = process.EnableCurrentMonitoring;
            ChkRecordCurrentBeforeStart.IsChecked = process.RecordCurrentBeforeStart;
            ChkEnableLumbarTest.IsChecked = process.EnableLumbarTest;
            ChkEnableMassageTest.IsChecked = process.EnableMassageTest;
            ChkCheckSameModel.IsChecked = process.CheckSameModel;
            ChkPromptOnDuplicate.IsChecked = process.PromptOnDuplicateBarcode;
            ChkEnableBarcodePrefixCheck.IsChecked = process.EnableBarcodePrefixCheck;
            TxtBarcodePrefix.Text = process.BarcodePrefix ?? string.Empty;
            ChkMeasureSleepCurrent.IsChecked = process.MeasureSleepCurrent;
            ChkMeasureStaticCurrent.IsChecked = process.MeasureStaticCurrent;
            TxtMaxTestCount.Text = process.MaxTestCount.ToString();
            TxtPowerOffDuration.Text = process.ModeSwitchPowerOffDuration.ToString();
            UpdateLumbarConfigVisibility(process.EnableLumbarTest);

            _currentModel.Channel1Config ??= new ChannelConfig { ChannelName = "閫氶亾1" };
            _currentModel.Channel2Config ??= new ChannelConfig { ChannelName = "閫氶亾2" };
            _currentModel.Channel3Config ??= new ChannelConfig { ChannelName = "閫氶亾3" };
            _currentModel.Channel4Config ??= new ChannelConfig { ChannelName = "閫氶亾4" };

            var ch1 = _currentModel.Channel1Config;
            var ch2 = _currentModel.Channel2Config;
            var ch3 = _currentModel.Channel3Config;
            var ch4 = _currentModel.Channel4Config;

            _currentModel.CurrentSleepConfig ??= CurrentSleepConfig.FromChannel(ch1);
            var shared = _currentModel.CurrentSleepConfig;
            if (shared != null)
            {
                TxtSharedStaticMin.Text = shared.StaticCurrentMin.ToString();
                TxtSharedStaticMax.Text = shared.StaticCurrentMax.ToString();
                TxtSharedWorkMin.Text = shared.WorkCurrentMin.ToString();
                TxtSharedWorkMax.Text = shared.WorkCurrentMax.ToString();
                TxtSharedCurrentOver.Text = shared.CurrentOverLimit.ToString();
                TxtSharedSleepThreshold.Text = shared.SleepCurrentThreshold.ToString();
                TxtSharedSleepTimeout.Text = shared.SleepTestTimeout.ToString();
                TxtSharedHeightCodeMin.Text = shared.HeightCodeMin.ToString();
                TxtSharedHeightCodeMax.Text = shared.HeightCodeMax.ToString();
                TxtSharedHeightRangeMin.Text = shared.HeightRangeMin.ToString();
                TxtSharedHeightRangeMax.Text = shared.HeightRangeMax.ToString();
            }

            _messageKeyTests = new ObservableCollection<MessageKeyTestEntry>(
                MergeMessageKeyTests(ch1.MessageKeyTestConfigs, ch2.MessageKeyTestConfigs, ch3.MessageKeyTestConfigs, ch4.MessageKeyTestConfigs));
            MessageKeyTestGrid.ItemsSource = _messageKeyTests;

            _messageConfig = CloneMessageConfig(ch1.MessageConfig);
            if ((_messageConfig?.PowerOnMessage?.Any(v => v != 0) != true || _messageConfig == null) && ch2.MessageConfig != null)
            {
                _messageConfig = CloneMessageConfig(ch2.MessageConfig);
            }

            if (!string.IsNullOrWhiteSpace(_currentModel.ModelName))
            {
                var dbLeft = await _databaseService.LoadChannelMessageConfigAsync(_currentModel.ModelName, 1);
                if (dbLeft != null)
                {
                    _messageConfig = CloneMessageConfig(dbLeft);
                }
                else
                {
                    var dbRight = await _databaseService.LoadChannelMessageConfigAsync(_currentModel.ModelName, 2);
                    if (dbRight != null)
                    {
                        _messageConfig = CloneMessageConfig(dbRight);
                    }
                }
            }

            _currentModel.Channel1Config.MessageConfig = CloneMessageConfig(_messageConfig);
            _currentModel.Channel2Config.MessageConfig = CloneMessageConfig(_messageConfig);
            _currentModel.Channel3Config.MessageConfig = CloneMessageConfig(_messageConfig);
            _currentModel.Channel4Config.MessageConfig = CloneMessageConfig(_messageConfig);

            _lumbarActions = new ObservableCollection<LumbarActionEntry>(
                MergeLumbarActions(ch1.LumbarTestConfigs, ch2.LumbarTestConfigs, ch3.LumbarTestConfigs, ch4.LumbarTestConfigs));
            LumbarConfigGrid.ItemsSource = _lumbarActions;

            _massageActions = new ObservableCollection<MassageActionEntry>(
                MergeMassageActions(ch1.MassageConfigs, ch2.MassageConfigs, ch3.MassageConfigs, ch4.MassageConfigs));
            MassageView.ItemsSource = _massageActions;

            _massageSettings = CloneMassageSettings(ch1.MassageTestSettings);
            if (ch1.MassageTestSettings == null && ch2.MassageTestSettings != null)
            {
                _massageSettings = CloneMassageSettings(ch2.MassageTestSettings);
            }
            MassageView.Settings = _massageSettings;

            EnsureManualControlEntries();
            LoadManualControlConfig(ch1, 1);
            LoadManualControlConfig(ch2, 2);
            if (GetConfiguredChannelCount() >= 4)
            {
                LoadManualControlConfig(ch3, 3);
                LoadManualControlConfig(ch4, 4);
            }

            UpdateAllMessagePreviews();
        }

        private void LoadManualControlConfig(ChannelConfig config, int channel)
        {
            EnsureManualControlEntries();
            var manual = config.ManualControl ?? new ManualControlAddressConfig();

            foreach (var entry in _manualControlEntries)
            {
                string raw = entry.Key switch
                {
                    ManualControlKey.StopButton => manual.StopButtonAddress,
                    ManualControlKey.FullTestStart => manual.FullTestStartAddress,
                    ManualControlKey.MassageStart => manual.MassageStartAddress,
                    ManualControlKey.SideWingStart => manual.SideWingStartAddress,
                    ManualControlKey.PowerOff => manual.PowerOffAddress,
                    ManualControlKey.ClampCylinder => manual.ClampCylinderAddress,
                    ManualControlKey.SpareCylinder => manual.SpareCylinderAddress,
                    ManualControlKey.DriverSwitch => manual.DriverSwitchAddress,
                    ManualControlKey.MassageKey => manual.MassageKeyAddress,
                    ManualControlKey.FullTestLight => manual.FullTestLightAddress,
                    ManualControlKey.MassageLight => manual.MassageLightAddress,
                    ManualControlKey.SideWingLight => manual.SideWingLightAddress,
                    ManualControlKey.TestOkLight => manual.TestOkLightAddress,
                    ManualControlKey.TestNgLight => manual.TestNgLightAddress,
                    ManualControlKey.AirLeakStartButton => manual.AirLeakStartButtonAddress,
                    ManualControlKey.HighPressureInletValve => manual.HighPressureInletValveAddress,
                    ManualControlKey.HighPressureExhaustValve => manual.HighPressureExhaustValveAddress,
                    ManualControlKey.LowPressureInletValve => manual.LowPressureInletValveAddress,
                    ManualControlKey.LowPressureExhaustValve => manual.LowPressureExhaustValveAddress,
                    _ => string.Empty
                };

                string normalized = NormalizeManualAddress(raw, entry.ExpectedType, entry.AllowAlternateType);
                SetEntryAddress(entry, channel, normalized);
            }
        }

        private void SaveManualControlConfig(ChannelConfig config, int channel)
        {
            EnsureManualControlEntries();
            var manual = config.ManualControl ?? new ManualControlAddressConfig();

            foreach (var entry in _manualControlEntries)
            {
                string raw = GetEntryAddress(entry, channel);
                string normalized = NormalizeManualAddress(raw, entry.ExpectedType, entry.AllowAlternateType);

                switch (entry.Key)
                {
                    case ManualControlKey.StopButton:
                        manual.StopButtonAddress = normalized;
                        break;
                    case ManualControlKey.FullTestStart:
                        manual.FullTestStartAddress = normalized;
                        break;
                    case ManualControlKey.MassageStart:
                        manual.MassageStartAddress = normalized;
                        break;
                    case ManualControlKey.SideWingStart:
                        manual.SideWingStartAddress = normalized;
                        break;
                    case ManualControlKey.PowerOff:
                        manual.PowerOffAddress = normalized;
                        break;
                    case ManualControlKey.ClampCylinder:
                        manual.ClampCylinderAddress = normalized;
                        break;
                    case ManualControlKey.SpareCylinder:
                        manual.SpareCylinderAddress = normalized;
                        break;
                    case ManualControlKey.DriverSwitch:
                        manual.DriverSwitchAddress = normalized;
                        break;
                    case ManualControlKey.MassageKey:
                        manual.MassageKeyAddress = normalized;
                        break;
                    case ManualControlKey.FullTestLight:
                        manual.FullTestLightAddress = normalized;
                        break;
                    case ManualControlKey.MassageLight:
                        manual.MassageLightAddress = normalized;
                        break;
                    case ManualControlKey.SideWingLight:
                        manual.SideWingLightAddress = normalized;
                        break;
                    case ManualControlKey.TestOkLight:
                        manual.TestOkLightAddress = normalized;
                        break;
                    case ManualControlKey.TestNgLight:
                        manual.TestNgLightAddress = normalized;
                        break;
                    case ManualControlKey.AirLeakStartButton:
                        manual.AirLeakStartButtonAddress = normalized;
                        break;
                    case ManualControlKey.HighPressureInletValve:
                        manual.HighPressureInletValveAddress = normalized;
                        break;
                    case ManualControlKey.HighPressureExhaustValve:
                        manual.HighPressureExhaustValveAddress = normalized;
                        break;
                    case ManualControlKey.LowPressureInletValve:
                        manual.LowPressureInletValveAddress = normalized;
                        break;
                    case ManualControlKey.LowPressureExhaustValve:
                        manual.LowPressureExhaustValveAddress = normalized;
                        break;
                }
            }

            manual.MassagePointAddresses = new List<string>();
            manual.UpInflateDownDeflateAddress = string.Empty;
            manual.DownInflateUpDeflateAddress = string.Empty;
            manual.BothInflateAddress = string.Empty;
            manual.BothDeflateAddress = string.Empty;

            config.ManualControl = manual;
        }

        private void ClearManualControlInputs()
        {
            EnsureManualControlEntries();
            foreach (var entry in _manualControlEntries)
            {
                entry.Ch1Address = string.Empty;
                entry.Ch2Address = string.Empty;
                entry.Ch3Address = string.Empty;
                entry.Ch4Address = string.Empty;
            }
        }

        private static string NormalizeManualAddress(string? address, ModbusBitType expectedType, bool allowAlternateType)
        {
            if (!ModbusAddressHelper.TryParseBitAddress(address, out var parsed))
            {
                return string.Empty;
            }

            if (!allowAlternateType && parsed.Type != expectedType)
            {
                return string.Empty;
            }

            return address.Trim();
        }

        private void EnsureManualControlEntries()
        {
            if (_manualControlEntries != null)
            {
                return;
            }

            _manualControlEntries = new ObservableCollection<ManualControlEntry>(
                ManualControlDefinitions.Select(definition => new ManualControlEntry(definition)));
            ManualControlGrid.ItemsSource = _manualControlEntries;
        }

        private static string GetEntryAddress(ManualControlEntry entry, int channel)
        {
            return channel switch
            {
                1 => entry.Ch1Address,
                2 => entry.Ch2Address,
                3 => entry.Ch3Address,
                4 => entry.Ch4Address,
                _ => string.Empty
            };
        }

        private static void SetEntryAddress(ManualControlEntry entry, int channel, string value)
        {
            switch (channel)
            {
                case 1:
                    entry.Ch1Address = value;
                    break;
                case 2:
                    entry.Ch2Address = value;
                    break;
                case 3:
                    entry.Ch3Address = value;
                    break;
                case 4:
                    entry.Ch4Address = value;
                    break;
            }
        }

        private void SaveCurrentModel()
        {
            if (_currentModel == null) return;

            try
            {
                _lumbarAddressWarnings.Clear();
                CommitAllDataGridEdits();

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
                process.EnableBarcodePrefixCheck = ChkEnableBarcodePrefixCheck.IsChecked == true;
                process.BarcodePrefix = TxtBarcodePrefix.Text?.Trim() ?? string.Empty;
                process.MeasureSleepCurrent = ChkMeasureSleepCurrent.IsChecked == true;
                process.MeasureStaticCurrent = ChkMeasureStaticCurrent.IsChecked == true;
                process.MaxTestCount = int.Parse(TxtMaxTestCount.Text);
                process.ModeSwitchPowerOffDuration = int.Parse(TxtPowerOffDuration.Text);
                _currentModel.ProcessConfig = process;

                _currentModel.Channel1Config ??= new ChannelConfig { ChannelName = "閫氶亾1" };
                _currentModel.Channel2Config ??= new ChannelConfig { ChannelName = "閫氶亾2" };
                _currentModel.Channel3Config ??= new ChannelConfig { ChannelName = "閫氶亾3" };
                _currentModel.Channel4Config ??= new ChannelConfig { ChannelName = "閫氶亾4" };

                _currentModel.CurrentSleepConfig ??= new CurrentSleepConfig();
                _currentModel.CurrentSleepConfig.StaticCurrentMin = double.Parse(TxtSharedStaticMin.Text);
                _currentModel.CurrentSleepConfig.StaticCurrentMax = double.Parse(TxtSharedStaticMax.Text);
                _currentModel.CurrentSleepConfig.WorkCurrentMin = double.Parse(TxtSharedWorkMin.Text);
                _currentModel.CurrentSleepConfig.WorkCurrentMax = double.Parse(TxtSharedWorkMax.Text);
                _currentModel.CurrentSleepConfig.CurrentOverLimit = double.Parse(TxtSharedCurrentOver.Text);
                _currentModel.CurrentSleepConfig.SleepCurrentThreshold = double.Parse(TxtSharedSleepThreshold.Text);
                _currentModel.CurrentSleepConfig.SleepTestTimeout = int.Parse(TxtSharedSleepTimeout.Text);
                _currentModel.CurrentSleepConfig.HeightCodeMin = int.Parse(TxtSharedHeightCodeMin.Text);
                _currentModel.CurrentSleepConfig.HeightCodeMax = int.Parse(TxtSharedHeightCodeMax.Text);
                _currentModel.CurrentSleepConfig.HeightRangeMin = double.Parse(TxtSharedHeightRangeMin.Text);
                _currentModel.CurrentSleepConfig.HeightRangeMax = double.Parse(TxtSharedHeightRangeMax.Text);

                var ch1 = _currentModel.Channel1Config;
                ch1.MessageKeyTestConfigs = _messageKeyTests?.Select(entry => CreateMessageKeyTestConfig(entry, 1)).ToList()
                    ?? new List<MessageKeyTestConfig>();
                ch1.LumbarTestConfigs = _lumbarActions?.Select(entry => CreateLumbarConfig(entry, 1)).ToList()
                    ?? new List<LumbarTestConfig>();
                ch1.MassageConfigs = _massageActions?.Select(entry => CreateMassageConfig(entry, 1)).ToList()
                    ?? new List<MassageConfig>();
                ch1.MassageTestSettings = CloneMassageSettings(_massageSettings);
                ch1.MessageConfig = CloneMessageConfig(_messageConfig);
                SaveManualControlConfig(ch1, 1);

                var ch2 = _currentModel.Channel2Config;
                ch2.MessageKeyTestConfigs = _messageKeyTests?.Select(entry => CreateMessageKeyTestConfig(entry, 2)).ToList()
                    ?? new List<MessageKeyTestConfig>();
                ch2.LumbarTestConfigs = _lumbarActions?.Select(entry => CreateLumbarConfig(entry, 2)).ToList()
                    ?? new List<LumbarTestConfig>();
                ch2.MassageConfigs = _massageActions?.Select(entry => CreateMassageConfig(entry, 2)).ToList()
                    ?? new List<MassageConfig>();
                ch2.MassageTestSettings = CloneMassageSettings(_massageSettings);
                ch2.MessageConfig = CloneMessageConfig(_messageConfig);
                SaveManualControlConfig(ch2, 2);

                int channelCount = GetConfiguredChannelCount();
                var ch3 = _currentModel.Channel3Config;
                var ch4 = _currentModel.Channel4Config;
                if (channelCount >= 3)
                {
                    ch3.MessageKeyTestConfigs = _messageKeyTests?.Select(entry => CreateMessageKeyTestConfig(entry, 3)).ToList()
                        ?? new List<MessageKeyTestConfig>();
                    ch3.LumbarTestConfigs = _lumbarActions?.Select(entry => CreateLumbarConfig(entry, 3)).ToList()
                        ?? new List<LumbarTestConfig>();
                    ch3.MassageConfigs = _massageActions?.Select(entry => CreateMassageConfig(entry, 3)).ToList()
                        ?? new List<MassageConfig>();
                    ch3.MassageTestSettings = CloneMassageSettings(_massageSettings);
                    ch3.MessageConfig = CloneMessageConfig(_messageConfig);
                    SaveManualControlConfig(ch3, 3);
                }
                if (channelCount >= 4)
                {
                    ch4.MessageKeyTestConfigs = _messageKeyTests?.Select(entry => CreateMessageKeyTestConfig(entry, 4)).ToList()
                        ?? new List<MessageKeyTestConfig>();
                    ch4.LumbarTestConfigs = _lumbarActions?.Select(entry => CreateLumbarConfig(entry, 4)).ToList()
                        ?? new List<LumbarTestConfig>();
                    ch4.MassageConfigs = _massageActions?.Select(entry => CreateMassageConfig(entry, 4)).ToList()
                        ?? new List<MassageConfig>();
                    ch4.MassageTestSettings = CloneMassageSettings(_massageSettings);
                    ch4.MessageConfig = CloneMessageConfig(_messageConfig);
                    SaveManualControlConfig(ch4, 4);
                }

                _currentModel.CurrentSleepConfig.ApplyToChannel(ch1);
                _currentModel.CurrentSleepConfig.ApplyToChannel(ch2);
                if (channelCount >= 3)
                    _currentModel.CurrentSleepConfig.ApplyToChannel(ch3);
                if (channelCount >= 4)
                    _currentModel.CurrentSleepConfig.ApplyToChannel(ch4);

                if (_lumbarAddressWarnings.Count > 0)
                {
                    string warningMessage = string.Join(Environment.NewLine, _lumbarAddressWarnings.Distinct());
                    MessageBox.Show(warningMessage, "鑵版墭瀵勫瓨鍣ㄥ凡鏍℃", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"淇濆瓨閰嶇疆鏁版嵁澶辫触: {ex.Message}");
            }
        }

        private void CommitAllDataGridEdits()
        {
            CommitDataGridEdit(LumbarConfigGrid);
            CommitDataGridEdit(MassageView?.ConfigGrid);
            CommitDataGridEdit(MessageKeyTestGrid);
            CommitDataGridEdit(ManualControlGrid);
        }

        private static void CommitDataGridEdit(DataGrid dataGrid)
        {
            if (dataGrid == null)
            {
                return;
            }

            dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            dataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private static List<LumbarActionEntry> MergeLumbarActions(
            IEnumerable<LumbarTestConfig> ch1Configs,
            IEnumerable<LumbarTestConfig> ch2Configs,
            IEnumerable<LumbarTestConfig> ch3Configs,
            IEnumerable<LumbarTestConfig> ch4Configs)
        {
            var map = new Dictionary<int, LumbarActionEntry>();

            foreach (var config in ch1Configs ?? Enumerable.Empty<LumbarTestConfig>())
            {
                string normalized = NormalizeLumbarAddresses(
                    config.MRegisterAddress,
                    "1",
                    config.Order,
                    LogLumbarAddressCorrection);

                var entry = new LumbarActionEntry
                {
                    Order = config.Order,
                    Action = config.Action,
                    TargetHeight = config.TargetHeight,
                    TargetTime = config.TargetTime,
                    Enabled = config.Enabled,
                    Ch1RegisterAddress = normalized,
                    Ch2RegisterAddress = string.Empty,
                    Ch3RegisterAddress = string.Empty,
                    Ch4RegisterAddress = string.Empty,
                    SendMessage = NormalizeMessageArray(config.SendMessage)
                };

                map[entry.Order] = entry;
            }

            foreach (var config in ch2Configs ?? Enumerable.Empty<LumbarTestConfig>())
            {
                string normalized = NormalizeLumbarAddresses(
                    config.MRegisterAddress,
                    "2",
                    config.Order,
                    LogLumbarAddressCorrection);

                if (!map.TryGetValue(config.Order, out var entry))
                {
                    entry = new LumbarActionEntry
                    {
                        Order = config.Order,
                        Action = config.Action,
                        TargetHeight = config.TargetHeight,
                        TargetTime = config.TargetTime,
                        Enabled = config.Enabled,
                        Ch1RegisterAddress = string.Empty,
                        Ch2RegisterAddress = normalized,
                        Ch3RegisterAddress = string.Empty,
                        Ch4RegisterAddress = string.Empty,
                        SendMessage = NormalizeMessageArray(config.SendMessage)
                    };
                    map[entry.Order] = entry;
                }
                else
                {
                    entry.Ch2RegisterAddress = normalized;

                    if (entry.SendMessage == null || entry.SendMessage.All(v => v == 0))
                    {
                        entry.SendMessage = NormalizeMessageArray(config.SendMessage);
                    }
                }
            }

            foreach (var config in ch3Configs ?? Enumerable.Empty<LumbarTestConfig>())
            {
                string normalized = NormalizeLumbarAddresses(
                    config.MRegisterAddress,
                    "3",
                    config.Order,
                    LogLumbarAddressCorrection);

                if (!map.TryGetValue(config.Order, out var entry))
                {
                    entry = new LumbarActionEntry
                    {
                        Order = config.Order,
                        Action = config.Action,
                        TargetHeight = config.TargetHeight,
                        TargetTime = config.TargetTime,
                        Enabled = config.Enabled,
                        Ch1RegisterAddress = string.Empty,
                        Ch2RegisterAddress = string.Empty,
                        Ch3RegisterAddress = normalized,
                        Ch4RegisterAddress = string.Empty,
                        SendMessage = NormalizeMessageArray(config.SendMessage)
                    };
                    map[entry.Order] = entry;
                }
                else
                {
                    entry.Ch3RegisterAddress = normalized;

                    if (entry.SendMessage == null || entry.SendMessage.All(v => v == 0))
                    {
                        entry.SendMessage = NormalizeMessageArray(config.SendMessage);
                    }
                }
            }

            foreach (var config in ch4Configs ?? Enumerable.Empty<LumbarTestConfig>())
            {
                string normalized = NormalizeLumbarAddresses(
                    config.MRegisterAddress,
                    "4",
                    config.Order,
                    LogLumbarAddressCorrection);

                if (!map.TryGetValue(config.Order, out var entry))
                {
                    entry = new LumbarActionEntry
                    {
                        Order = config.Order,
                        Action = config.Action,
                        TargetHeight = config.TargetHeight,
                        TargetTime = config.TargetTime,
                        Enabled = config.Enabled,
                        Ch1RegisterAddress = string.Empty,
                        Ch2RegisterAddress = string.Empty,
                        Ch3RegisterAddress = string.Empty,
                        Ch4RegisterAddress = normalized,
                        SendMessage = NormalizeMessageArray(config.SendMessage)
                    };
                    map[entry.Order] = entry;
                }
                else
                {
                    entry.Ch4RegisterAddress = normalized;

                    if (entry.SendMessage == null || entry.SendMessage.All(v => v == 0))
                    {
                        entry.SendMessage = NormalizeMessageArray(config.SendMessage);
                    }
                }
            }

            return map.Values
                .OrderBy(e => e.Order)
                .ToList();
        }

        private static List<MassageActionEntry> MergeMassageActions(
            IEnumerable<MassageConfig> ch1Configs,
            IEnumerable<MassageConfig> ch2Configs,
            IEnumerable<MassageConfig> ch3Configs,
            IEnumerable<MassageConfig> ch4Configs)
        {
            var map = new Dictionary<int, MassageActionEntry>();

            foreach (var config in ch1Configs ?? Enumerable.Empty<MassageConfig>())
            {
                if (config.Point <= 0 || config.Point > MaxMassagePointCount)
                {
                    continue;
                }

                var entry = new MassageActionEntry
                {
                    Point = config.Point,
                    Ch1HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress),
                    Ch2HeightSwitchAddress = string.Empty,
                    Ch3HeightSwitchAddress = string.Empty,
                    Ch4HeightSwitchAddress = string.Empty,
                    Enabled = config.Enabled
                };

                map[entry.Point] = entry;
            }

            foreach (var config in ch2Configs ?? Enumerable.Empty<MassageConfig>())
            {
                if (config.Point <= 0 || config.Point > MaxMassagePointCount)
                {
                    continue;
                }

                if (!map.TryGetValue(config.Point, out var entry))
                {
                    entry = new MassageActionEntry
                    {
                        Point = config.Point,
                        Ch1HeightSwitchAddress = string.Empty,
                        Ch2HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress),
                        Ch3HeightSwitchAddress = string.Empty,
                        Ch4HeightSwitchAddress = string.Empty,
                        Enabled = config.Enabled
                    };
                    map[entry.Point] = entry;
                }
                else
                {
                    entry.Ch2HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress);
                }
            }

            foreach (var config in ch3Configs ?? Enumerable.Empty<MassageConfig>())
            {
                if (config.Point <= 0 || config.Point > MaxMassagePointCount)
                {
                    continue;
                }

                if (!map.TryGetValue(config.Point, out var entry))
                {
                    entry = new MassageActionEntry
                    {
                        Point = config.Point,
                        Ch1HeightSwitchAddress = string.Empty,
                        Ch2HeightSwitchAddress = string.Empty,
                        Ch3HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress),
                        Ch4HeightSwitchAddress = string.Empty,
                        Enabled = config.Enabled
                    };
                    map[entry.Point] = entry;
                }
                else
                {
                    entry.Ch3HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress);
                }
            }

            foreach (var config in ch4Configs ?? Enumerable.Empty<MassageConfig>())
            {
                if (config.Point <= 0 || config.Point > MaxMassagePointCount)
                {
                    continue;
                }

                if (!map.TryGetValue(config.Point, out var entry))
                {
                    entry = new MassageActionEntry
                    {
                        Point = config.Point,
                        Ch1HeightSwitchAddress = string.Empty,
                        Ch2HeightSwitchAddress = string.Empty,
                        Ch3HeightSwitchAddress = string.Empty,
                        Ch4HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress),
                        Enabled = config.Enabled
                    };
                    map[entry.Point] = entry;
                }
                else
                {
                    entry.Ch4HeightSwitchAddress = NormalizeMassageAddress(config.HeightSwitchAddress);
                }
            }

            return map.Values
                .OrderBy(e => e.Point)
                .ToList();
        }

        private static List<MessageKeyTestEntry> MergeMessageKeyTests(
            IEnumerable<MessageKeyTestConfig> ch1Configs,
            IEnumerable<MessageKeyTestConfig> ch2Configs,
            IEnumerable<MessageKeyTestConfig> ch3Configs,
            IEnumerable<MessageKeyTestConfig> ch4Configs)
        {
            var map = new Dictionary<int, MessageKeyTestEntry>();

            static void MergeFromChannel(IDictionary<int, MessageKeyTestEntry> target, IEnumerable<MessageKeyTestConfig>? source, int channel)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var config in source)
                {
                    int order = config.Order <= 0 ? target.Count + 1 : config.Order;
                    if (!target.TryGetValue(order, out var entry))
                    {
                        entry = new MessageKeyTestEntry
                        {
                            Order = order,
                            Enabled = config.Enabled,
                            ReadByteIndex = Math.Max(1, config.ReadByteIndex),
                            ExpectedValue = Math.Clamp(config.ExpectedValue, 0, byte.MaxValue),
                            TriggerMode = config.TriggerMode
                        };
                        target[order] = entry;
                    }

                    string normalized = NormalizeMessageKeyOutputAddress(config.OutputRegisterAddress);
                    switch (channel)
                    {
                        case 1:
                            entry.Ch1OutputRegisterAddress = normalized;
                            break;
                        case 2:
                            entry.Ch2OutputRegisterAddress = normalized;
                            break;
                        case 3:
                            entry.Ch3OutputRegisterAddress = normalized;
                            break;
                        case 4:
                            entry.Ch4OutputRegisterAddress = normalized;
                            break;
                    }
                }
            }

            MergeFromChannel(map, ch1Configs, 1);
            MergeFromChannel(map, ch2Configs, 2);
            MergeFromChannel(map, ch3Configs, 3);
            MergeFromChannel(map, ch4Configs, 4);

            return map.Values
                .OrderBy(e => e.Order)
                .Take(8)
                .ToList();
        }

        private LumbarTestConfig CreateLumbarConfig(LumbarActionEntry entry, int channel)
        {
            string channelLabel = channel.ToString();
            string rawInput = channel switch
            {
                1 => entry.Ch1RegisterAddress,
                2 => entry.Ch2RegisterAddress,
                3 => entry.Ch3RegisterAddress,
                4 => entry.Ch4RegisterAddress,
                _ => string.Empty
            };

            string normalized = NormalizeLumbarAddresses(
                rawInput ?? string.Empty,
                channelLabel,
                entry.Order,
                warning => _lumbarAddressWarnings.Add(warning));

            switch (channel)
            {
                case 1:
                    entry.Ch1RegisterAddress = normalized;
                    break;
                case 2:
                    entry.Ch2RegisterAddress = normalized;
                    break;
                case 3:
                    entry.Ch3RegisterAddress = normalized;
                    break;
                case 4:
                    entry.Ch4RegisterAddress = normalized;
                    break;
            }

            return new LumbarTestConfig
            {
                Order = entry.Order,
                Action = entry.Action,
                TargetHeight = entry.TargetHeight,
                TargetTime = entry.TargetTime,
                Enabled = entry.Enabled,
                MRegisterAddress = normalized,
                SendMessage = NormalizeMessageArray(entry.SendMessage)
            };
        }

        private static MassageConfig CreateMassageConfig(MassageActionEntry entry, int channel)
        {
            string address = channel switch
            {
                1 => entry.Ch1HeightSwitchAddress,
                2 => entry.Ch2HeightSwitchAddress,
                3 => entry.Ch3HeightSwitchAddress,
                4 => entry.Ch4HeightSwitchAddress,
                _ => string.Empty
            };

            string normalized = NormalizeMassageAddress(address);

            switch (channel)
            {
                case 1:
                    entry.Ch1HeightSwitchAddress = normalized;
                    break;
                case 2:
                    entry.Ch2HeightSwitchAddress = normalized;
                    break;
                case 3:
                    entry.Ch3HeightSwitchAddress = normalized;
                    break;
                case 4:
                    entry.Ch4HeightSwitchAddress = normalized;
                    break;
            }

            return new MassageConfig
            {
                Point = entry.Point,
                HeightSwitchAddress = normalized,
                Enabled = entry.Enabled
            };
        }

        private static MessageKeyTestConfig CreateMessageKeyTestConfig(MessageKeyTestEntry entry, int channel)
        {
            string address = channel switch
            {
                1 => entry.Ch1OutputRegisterAddress,
                2 => entry.Ch2OutputRegisterAddress,
                3 => entry.Ch3OutputRegisterAddress,
                4 => entry.Ch4OutputRegisterAddress,
                _ => string.Empty
            };

            return new MessageKeyTestConfig
            {
                Order = Math.Max(1, entry.Order),
                Enabled = entry.Enabled,
                OutputRegisterAddress = NormalizeMessageKeyOutputAddress(address),
                ReadByteIndex = Math.Max(1, entry.ReadByteIndex),
                ExpectedValue = Math.Clamp(entry.ExpectedValue, 0, byte.MaxValue),
                TriggerMode = entry.TriggerMode
            };
        }

        private static string NormalizeMassageAddress(string? address)
        {
            if (!ModbusAddressHelper.TryParseBitAddress(address, out var parsed))
            {
                return string.Empty;
            }

            if (parsed.Type != ModbusBitType.DiscreteInput)
            {
                return string.Empty;
            }

            return address.Trim();
        }

        private static string NormalizeMessageKeyOutputAddress(string? address)
        {
            if (!ModbusAddressHelper.TryParseBitAddress(address, out var parsed))
            {
                return string.Empty;
            }

            if (parsed.Type != ModbusBitType.Coil)
            {
                return string.Empty;
            }

            return address.Trim();
        }

        private void BtnSelectImage_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "閫夋嫨浜у搧鍥剧墖",
                Filter = "鍥剧墖鏂囦欢|*.jpg;*.jpeg;*.png;*.bmp;*.gif",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtImagePath.Text = openFileDialog.FileName;
                ModelImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(openFileDialog.FileName));
            }
        }

        private void BtnAddModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddModelDialog();
            if (dialog.ShowDialog() == true)
            {
                var newModel = new ProductModel
                {
                    ModelName = dialog.ModelName,
                    Description = dialog.ModelDescription,
                    Channel1Config = new ChannelConfig { ChannelName = "閫氶亾1" },
                    Channel2Config = new ChannelConfig { ChannelName = "閫氶亾2" },
                    Channel3Config = new ChannelConfig { ChannelName = "閫氶亾3" },
                    Channel4Config = new ChannelConfig { ChannelName = "閫氶亾4" },
                    ProcessConfig = new TestProcessConfig()
                };

                _productModels.Add(newModel);
                ModelListBox.ItemsSource = _productModels.Select(m => m.ModelName).ToList();
                ModelListBox.SelectedIndex = _productModels.Count - 1;
            }
        }

        private async void BtnDeleteModel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("璇峰厛閫夋嫨瑕佸垹闄ょ殑鍨嬪彿", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"纭畾瑕佸垹闄ゅ瀷鍙?'{_currentModel.ModelName}' 鍚楋紵", "纭鍒犻櫎",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await _databaseService.DeleteMessageConfigsAsync(_currentModel.ModelName);
                    await _configService.DeleteProductModelAsync(_currentModel.ModelName);

                    _productModels.Remove(_currentModel);
                    ModelListBox.ItemsSource = _productModels.Select(m => m.ModelName).ToList();

                    if (_productModels.Count > 0)
                    {
                        ModelListBox.SelectedIndex = 0;
                    }
                    else
                    {
                        _currentModel = null;
                        ClearModelConfig();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"鍒犻櫎鍨嬪彿澶辫触: {ex.Message}", "閿欒", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ClearModelConfig()
        {
            TxtModelName.Text = string.Empty;
            TxtModelDesc.Text = string.Empty;
            TxtImagePath.Text = string.Empty;
            ModelImage.Source = null;

            ChkEnableBarcode.IsChecked = false;
            ChkEnableCurrentMonitor.IsChecked = false;
            ChkRecordCurrentBeforeStart.IsChecked = false;
            ChkEnableLumbarTest.IsChecked = false;
            ChkEnableMassageTest.IsChecked = false;
            ChkCheckSameModel.IsChecked = false;
            ChkPromptOnDuplicate.IsChecked = false;
            ChkEnableBarcodePrefixCheck.IsChecked = false;
            TxtBarcodePrefix.Text = string.Empty;
            TxtMaxTestCount.Text = "0";
            TxtPowerOffDuration.Text = "0";
            UpdateLumbarConfigVisibility(false);

            TxtSharedStaticMin.Text = TxtSharedStaticMax.Text = "0";
            TxtSharedWorkMin.Text = TxtSharedWorkMax.Text = "0";
            TxtSharedCurrentOver.Text = "0";
            TxtSharedSleepThreshold.Text = "0";
            TxtSharedSleepTimeout.Text = "0";
            TxtSharedHeightCodeMin.Text = "0";
            TxtSharedHeightCodeMax.Text = "5000";
            TxtSharedHeightRangeMin.Text = "0";
            TxtSharedHeightRangeMax.Text = "50";

            ClearManualControlInputs();

            _lumbarActions = new ObservableCollection<LumbarActionEntry>();
            _massageActions = new ObservableCollection<MassageActionEntry>();
            _messageKeyTests = new ObservableCollection<MessageKeyTestEntry>();
            LumbarConfigGrid.ItemsSource = _lumbarActions;
            MassageView.ItemsSource = _massageActions;
            MessageKeyTestGrid.ItemsSource = _messageKeyTests;
            _massageSettings = new MassageTestSettings();
            MassageView.Settings = _massageSettings;

            _messageConfig = new MessageConfig();
            UpdateAllMessagePreviews();
            SyncMessageConfigsToModel();
        }

        private void ChkEnableLumbarTest_Checked(object sender, RoutedEventArgs e)
        {
            UpdateLumbarConfigVisibility(ChkEnableLumbarTest.IsChecked == true);
        }

        private void UpdateLumbarConfigVisibility(bool isEnabled)
        {
            if (LumbarConfigGroup == null)
            {
                return;
            }

            LumbarConfigGroup.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnSaveModel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("璇峰厛閫夋嫨鍨嬪彿", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SaveCurrentModel();
                await _configService.SaveProductModelAsync(_currentModel);
                await SaveMessageConfigsToDatabaseAsync(_currentModel);
                MessageBox.Show("淇濆瓨鎴愬姛", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Information);
                ModelListBox.ItemsSource = _productModels.Select(m => m.ModelName).ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"淇濆瓨澶辫触: {ex.Message}", "閿欒", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnReloadModel_Click(object sender, RoutedEventArgs e)
        {
            await LoadModelConfigAsync();
        }

        private void BtnAddLumbar_Click(object sender, RoutedEventArgs e)
        {
            _lumbarActions ??= new ObservableCollection<LumbarActionEntry>();
            int nextOrder = _lumbarActions.Any() ? _lumbarActions.Max(l => l.Order) + 1 : 1;
            _lumbarActions.Add(new LumbarActionEntry
            {
                Order = nextOrder,
                Action = LumbarActionType.UpInflateDownDeflate,
                TargetHeight = 50,
                TargetTime = 3000,
                Enabled = true,
                Ch1RegisterAddress = string.Empty,
                Ch2RegisterAddress = string.Empty,
                Ch3RegisterAddress = string.Empty,
                Ch4RegisterAddress = string.Empty,
                SendMessage = new ushort[20]
            });
        }

        private void BtnRemoveLumbar_Click(object sender, RoutedEventArgs e)
        {
            if (LumbarConfigGrid.SelectedItem is LumbarActionEntry selected && _lumbarActions != null)
            {
                _lumbarActions.Remove(selected);
            }
        }

        private void BtnAddMassage_Click(object sender, RoutedEventArgs e)
        {
            _massageActions ??= new ObservableCollection<MassageActionEntry>();
            int nextPoint = _massageActions.Any() ? _massageActions.Max(m => m.Point) + 1 : 1;
            if (nextPoint > MaxMassagePointCount)
            {
                MessageBox.Show($"鎸夋懇鍔ㄤ綔鐐逛綅涓婇檺涓簕MaxMassagePointCount}锛屾棤娉曠户缁坊鍔犮€?, "鎻愮ず",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _massageActions.Add(new MassageActionEntry
            {
                Point = nextPoint,
                Ch1HeightSwitchAddress = string.Empty,
                Ch2HeightSwitchAddress = string.Empty,
                Ch3HeightSwitchAddress = string.Empty,
                Ch4HeightSwitchAddress = string.Empty,
                Enabled = true
            });
        }

        private void BtnRemoveMassage_Click(object sender, RoutedEventArgs e)
        {
            if (MassageView?.SelectedMassageConfig is MassageActionEntry selected && _massageActions != null)
            {
                _massageActions.Remove(selected);
            }
        }

        private void BtnAddMessageKeyTest_Click(object sender, RoutedEventArgs e)
        {
            _messageKeyTests ??= new ObservableCollection<MessageKeyTestEntry>();
            if (_messageKeyTests.Count >= 8)
            {
                MessageBox.Show("鎸夐敭鎶ユ枃娴嬭瘯閰嶇疆鏈€澶?椤广€?, "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int nextOrder = _messageKeyTests.Any() ? _messageKeyTests.Max(m => m.Order) + 1 : 1;
            _messageKeyTests.Add(new MessageKeyTestEntry
            {
                Order = nextOrder,
                Enabled = true,
                ReadByteIndex = 21,
                ExpectedValue = 0,
                TriggerMode = MessageKeyTriggerMode.Continuous,
                Ch1OutputRegisterAddress = string.Empty,
                Ch2OutputRegisterAddress = string.Empty,
                Ch3OutputRegisterAddress = string.Empty,
                Ch4OutputRegisterAddress = string.Empty
            });
        }

        private void BtnRemoveMessageKeyTest_Click(object sender, RoutedEventArgs e)
        {
            if (MessageKeyTestGrid.SelectedItem is MessageKeyTestEntry selected && _messageKeyTests != null)
            {
                _messageKeyTests.Remove(selected);
            }
        }

        private void BtnConfigPowerOnMessage_Click(object sender, RoutedEventArgs e)
        {
            ConfigureMessage(_messageConfig, c => c.PowerOnMessage, (c, data) => c.PowerOnMessage = data,
                "PowerOnMessagePreview");
        }

        private void BtnConfigSleepMessage_Click(object sender, RoutedEventArgs e)
        {
            ConfigureMessage(_messageConfig, c => c.SleepMessage, (c, data) => c.SleepMessage = data,
                "SleepMessagePreview");
        }

        private void BtnConfigStopMessage_Click(object sender, RoutedEventArgs e)
        {
            ConfigureMessage(_messageConfig, c => c.StopMessage, (c, data) => c.StopMessage = data,
                "StopMessagePreview");
        }

        private void BtnConfigMassageMessage_Click(object sender, RoutedEventArgs e)
        {
            ConfigureMessage(_messageConfig, c => c.MassageMessage, (c, data) => c.MassageMessage = data,
                "MassageMessagePreview");
        }

        private void BtnConfigMassage2Message_Click(object sender, RoutedEventArgs e)
        {
            ConfigureMessage(_messageConfig, c => c.MassageMessage2, (c, data) => c.MassageMessage2 = data,
                "Massage2MessagePreview");
        }

        private void BtnConfigReadMessage_Click(object sender, RoutedEventArgs e)
        {
            ConfigureMessage(_messageConfig, c => c.ReadMessage, (c, data) => c.ReadMessage = data,
                "ReadMessagePreview");
        }

        private void BtnConfigLumbarMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is LumbarActionEntry entry)
            {
                var dialog = new MessageConfigDialog(entry.SendMessage);
                if (dialog.ShowDialog() == true)
                {
                    entry.SendMessage = NormalizeMessageArray(dialog.MessageData);
                }
            }
        }

        // 瀵煎嚭閰嶇疆鏂囦欢
        private void BtnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("璇峰厛閫夋嫨鍨嬪彿", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SaveCurrentModel();

                var saveFileDialog = new SaveFileDialog
                {
                    Title = "瀵煎嚭閰嶇疆鏂囦欢",
                    Filter = "JSON鏂囦欢|*.json",
                    FileName = $"{_currentModel.ModelName}_閰嶇疆.json"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    var json = JsonConvert.SerializeObject(_currentModel, Formatting.Indented);
                    File.WriteAllText(saveFileDialog.FileName, json);
                    MessageBox.Show("閰嶇疆鏂囦欢瀵煎嚭鎴愬姛", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"瀵煎嚭閰嶇疆鏂囦欢澶辫触: {ex.Message}", "閿欒", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 瀵煎叆閰嶇疆鏂囦欢
        private async void BtnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
            {
                MessageBox.Show("璇峰厛閫夋嫨鍨嬪彿", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "瀵煎叆閰嶇疆鏂囦欢",
                    Filter = "JSON鏂囦欢|*.json"
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
                        _currentModel.CurrentSleepConfig = importedModel.CurrentSleepConfig;
                        _currentModel.Channel1Config = importedModel.Channel1Config;
                        _currentModel.Channel2Config = importedModel.Channel2Config;
                        _currentModel.Channel3Config = importedModel.Channel3Config;
                        _currentModel.Channel4Config = importedModel.Channel4Config;

                        ModelListBox.ItemsSource = _productModels.Select(m => m.ModelName).ToList();
                        await LoadModelConfigAsync();
                        MessageBox.Show("閰嶇疆鏂囦欢瀵煎叆鎴愬姛", "鎻愮ず", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("瀵煎叆鐨勯厤缃枃浠舵牸寮忎笉姝ｇ‘", "閿欒", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"瀵煎叆閰嶇疆鏂囦欢澶辫触: {ex.Message}", "閿欒", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}

