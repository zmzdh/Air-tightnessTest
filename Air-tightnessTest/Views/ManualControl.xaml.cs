// UserControls/ManualControl.xaml.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.UserControls
{
    public partial class ManualControl : UserControl
    {
        private readonly CommService _commService;
        private MainWindow _mainWindow;
        private readonly ILogService _logService;
        private bool _isUpdatingFromPlc = false;
        private readonly Dictionary<int, byte[]> _receiveData = new();
        private readonly System.Windows.Threading.DispatcherTimer _continuousSendTimer;
        private bool _isContinuousSending;
        private byte[] _pendingContinuousPayload = Array.Empty<byte>();
        private ProductModel? _currentModel;
        private readonly Dictionary<int, TextBlock> _manualPressureTexts = new();

        private readonly Dictionary<string, string> _controlAddressMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 存储按摩点指示器的字典
        private Dictionary<string, Ellipse> _massageIndicators =
            new Dictionary<string, Ellipse>();

        // 存储控制按钮状态的字典
        private Dictionary<string, bool> _controlStates =
            new Dictionary<string, bool>();

        public ManualControl(CommService commService)
            : this(commService, LogService.Instance)
        {
        }

        public ManualControl(CommService commService, ILogService? logService)
        {
            InitializeComponent();
            AddValveControlGroups();
            SimplifyManualDebugLayout();
            _commService = commService ?? throw new ArgumentNullException(nameof(commService));
            _logService = logService ?? LogService.Instance;
            _mainWindow = (MainWindow)Application.Current.MainWindow;

            EnsureMessageReceivedSubscription();
            _continuousSendTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _continuousSendTimer.Tick += ContinuousSendTimer_Tick;

            InitializeMassageStatus();
            InitializeControlStates();
            ApplyManualConfig(_mainWindow?.GetCurrentModelForManual());

            // 初始更新一次
            UpdateWithPLCData(_mainWindow.GetCurrentPLCData());

            Loaded += ManualControl_Loaded;
            Unloaded += ManualControl_Unloaded;
        }

        private void ManualControl_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureMessageReceivedSubscription();

            if (_mainWindow == null)
            {
                _mainWindow = Application.Current.MainWindow as MainWindow;
            }

            InitializeReceiveData();
            ApplyChannelVisibility();
            ApplyManualConfig(_mainWindow?.GetCurrentModelForManual());
            UpdateWithPLCData(_mainWindow?.GetCurrentPLCData());
        }

        private void ManualControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _continuousSendTimer.Stop();
            RemoveMessageReceivedSubscription();
        }

        private void EnsureMessageReceivedSubscription()
        {
            _commService.MessageReceived -= CommService_MessageReceived;
            _commService.MessageReceived += CommService_MessageReceived;
        }

        private void RemoveMessageReceivedSubscription()
        {
            _commService.MessageReceived -= CommService_MessageReceived;
        }

        private void AddValveControlGroups()
        {
            AddValveControlGroup(Ch1ControlPanel, "Ch1");
            AddValveControlGroup(Ch2ControlPanel, "Ch2");
            AddValveControlGroup(Ch3ControlPanel, "Ch3");
            AddValveControlGroup(Ch4ControlPanel, "Ch4");
        }

        private void AddValveControlGroup(Border channelPanel, string prefix)
        {
            if (channelPanel.Child is not StackPanel stackPanel)
            {
                return;
            }

            var group = new GroupBox
            {
                Header = "电磁阀开关测试",
                Margin = new Thickness(0, 0, 0, 15)
            };

            var grid = new UniformGrid
            {
                Columns = 2,
                Margin = new Thickness(0, 5, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            AddValveButton(grid, prefix, "HighPressureInletValve", "高压进气阀");
            AddValveButton(grid, prefix, "HighPressureExhaustValve", "高压排气阀");
            AddValveButton(grid, prefix, "LowPressureInletValve", "低压进气阀");
            AddValveButton(grid, prefix, "LowPressureExhaustValve", "低压排气阀");

            group.Content = grid;
            stackPanel.Children.Insert(Math.Min(4, stackPanel.Children.Count), group);
        }

        private void AddValveButton(Panel parent, string prefix, string suffix, string label)
        {
            string controlKey = $"{prefix}{suffix}";
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var button = new Button
            {
                Name = controlKey,
                Content = label,
                Style = TryFindResource("ToggleButtonStyle") as Style
            };
            button.Click += async (_, _) => await ToggleControlAsync(controlKey);

            var indicator = new Ellipse
            {
                Name = $"{controlKey}Indicator",
                Style = TryFindResource("StatusIndicatorStyle") as Style,
                Fill = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };

            RegisterControlName(indicator.Name, indicator);
            row.Children.Add(button);
            row.Children.Add(indicator);
            parent.Children.Add(row);
        }

        private void RegisterControlName(string name, object scopedElement)
        {
            try
            {
                RegisterName(name, scopedElement);
            }
            catch (ArgumentException)
            {
                UnregisterName(name);
                RegisterName(name, scopedElement);
            }
        }

        private void SimplifyManualDebugLayout()
        {
            SimplifyChannelPanel(Ch1ControlPanel, 1);
            SimplifyChannelPanel(Ch2ControlPanel, 2);
            SimplifyChannelPanel(Ch3ControlPanel, 3);
            SimplifyChannelPanel(Ch4ControlPanel, 4);
            RemoveCommunicationTestPanel();
        }

        private void SimplifyChannelPanel(Border channelPanel, int channel)
        {
            if (channelPanel.Child is not StackPanel stackPanel)
            {
                return;
            }

            foreach (var group in stackPanel.Children.OfType<GroupBox>().ToList())
            {
                string header = group.Header?.ToString() ?? string.Empty;
                if (header == "输出控制" || header == "按键模拟" || header == "指示灯控制" || header == "按摩状态监控")
                {
                    stackPanel.Children.Remove(group);
                    continue;
                }

                if (header == "测量值")
                {
                    SimplifyMeasurementGroup(group, channel);
                }
            }
        }

        private void SimplifyMeasurementGroup(GroupBox group, int channel)
        {
            var pressureText = new TextBlock
            {
                Text = "实时压力: 0.00 KPa",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center
            };

            group.Header = "实时压力";
            group.Content = pressureText;
            _manualPressureTexts[channel] = pressureText;
        }

        private void RemoveCommunicationTestPanel()
        {
            if (Content is not ScrollViewer scrollViewer || scrollViewer.Content is not Grid rootGrid)
            {
                return;
            }

            foreach (UIElement child in rootGrid.Children.OfType<UIElement>().ToList())
            {
                if (Grid.GetColumn(child) == 4)
                {
                    rootGrid.Children.Remove(child);
                }
            }

            if (Col5 != null)
            {
                Col5.Width = new GridLength(0);
            }
        }
        private void InitializeReceiveData()
        {
            for (int channel = 1; channel <= 4; channel++)
            {
                _receiveData[channel] = new byte[20];
            }
        }

        private TextBox GetSendTextBox() => TxtSendData;

        private void InitializeControlStates()
        {
            // 初始化所有控制状态为关闭
            _controlStates["Ch1PowerOff"] = false;
            _controlStates["Ch1CylinderOpen"] = false;
            _controlStates["Ch1CylinderClose"] = false;
            _controlStates["Ch1DriverSwitch"] = false;
            _controlStates["Ch1MassageKey"] = false;
            _controlStates["Ch1UpInflateDownDeflate"] = false;
            _controlStates["Ch1DownInflateUpDeflate"] = false;
            _controlStates["Ch1BothInflate"] = false;
            _controlStates["Ch1BothDeflate"] = false;
            _controlStates["Ch1HighPressureInletValve"] = false;
            _controlStates["Ch1HighPressureExhaustValve"] = false;
            _controlStates["Ch1LowPressureInletValve"] = false;
            _controlStates["Ch1LowPressureExhaustValve"] = false;
            _controlStates["Ch1FullTestLight"] = false;
            _controlStates["Ch1MassageLight"] = false;
            _controlStates["Ch1SideWingLight"] = false;
            _controlStates["Ch1OKLight"] = false;
            _controlStates["Ch1NGLight"] = false;

            _controlStates["Ch2PowerOff"] = false;
            _controlStates["Ch2CylinderOpen"] = false;
            _controlStates["Ch2CylinderClose"] = false;
            _controlStates["Ch2DriverSwitch"] = false;
            _controlStates["Ch2MassageKey"] = false;
            _controlStates["Ch2UpInflateDownDeflate"] = false;
            _controlStates["Ch2DownInflateUpDeflate"] = false;
            _controlStates["Ch2BothInflate"] = false;
            _controlStates["Ch2BothDeflate"] = false;
            _controlStates["Ch2HighPressureInletValve"] = false;
            _controlStates["Ch2HighPressureExhaustValve"] = false;
            _controlStates["Ch2LowPressureInletValve"] = false;
            _controlStates["Ch2LowPressureExhaustValve"] = false;
            _controlStates["Ch2FullTestLight"] = false;
            _controlStates["Ch2MassageLight"] = false;
            _controlStates["Ch2SideWingLight"] = false;
            _controlStates["Ch2OKLight"] = false;
            _controlStates["Ch2NGLight"] = false;

            _controlStates["Ch3PowerOff"] = false;
            _controlStates["Ch3CylinderOpen"] = false;
            _controlStates["Ch3CylinderClose"] = false;
            _controlStates["Ch3DriverSwitch"] = false;
            _controlStates["Ch3MassageKey"] = false;
            _controlStates["Ch3UpInflateDownDeflate"] = false;
            _controlStates["Ch3DownInflateUpDeflate"] = false;
            _controlStates["Ch3BothInflate"] = false;
            _controlStates["Ch3BothDeflate"] = false;
            _controlStates["Ch3HighPressureInletValve"] = false;
            _controlStates["Ch3HighPressureExhaustValve"] = false;
            _controlStates["Ch3LowPressureInletValve"] = false;
            _controlStates["Ch3LowPressureExhaustValve"] = false;
            _controlStates["Ch3FullTestLight"] = false;
            _controlStates["Ch3MassageLight"] = false;
            _controlStates["Ch3SideWingLight"] = false;
            _controlStates["Ch3OKLight"] = false;
            _controlStates["Ch3NGLight"] = false;

            _controlStates["Ch4PowerOff"] = false;
            _controlStates["Ch4CylinderOpen"] = false;
            _controlStates["Ch4CylinderClose"] = false;
            _controlStates["Ch4DriverSwitch"] = false;
            _controlStates["Ch4MassageKey"] = false;
            _controlStates["Ch4UpInflateDownDeflate"] = false;
            _controlStates["Ch4DownInflateUpDeflate"] = false;
            _controlStates["Ch4BothInflate"] = false;
            _controlStates["Ch4BothDeflate"] = false;
            _controlStates["Ch4HighPressureInletValve"] = false;
            _controlStates["Ch4HighPressureExhaustValve"] = false;
            _controlStates["Ch4LowPressureInletValve"] = false;
            _controlStates["Ch4LowPressureExhaustValve"] = false;
            _controlStates["Ch4FullTestLight"] = false;
            _controlStates["Ch4MassageLight"] = false;
            _controlStates["Ch4SideWingLight"] = false;
            _controlStates["Ch4OKLight"] = false;
            _controlStates["Ch4NGLight"] = false;
        }

        public void ApplyManualConfig(ProductModel? model)
        {
            _currentModel = model;
            _controlAddressMap.Clear();

            if (model == null)
            {
                return;
            }

            ApplyChannelControlAddresses(model.Channel1Config, "Ch1");
            ApplyChannelControlAddresses(model.Channel2Config, "Ch2");
            ApplyChannelControlAddresses(model.Channel3Config, "Ch3");
            ApplyChannelControlAddresses(model.Channel4Config, "Ch4");
        }

        private void ApplyChannelControlAddresses(ChannelConfig? config, string prefix)
        {
            if (config?.ManualControl == null)
            {
                return;
            }

            var manual = config.ManualControl;

            AddControlAddress($"{prefix}PowerOff", manual.PowerOffAddress);
            AddControlAddress($"{prefix}CylinderOpen", manual.ClampCylinderAddress);
            AddControlAddress($"{prefix}CylinderClose", manual.SpareCylinderAddress);
            AddControlAddress($"{prefix}DriverSwitch", manual.DriverSwitchAddress);
            AddControlAddress($"{prefix}MassageKey", manual.MassageKeyAddress);
            AddControlAddress($"{prefix}UpInflateDownDeflate", GetLumbarActionAddress(config, LumbarActionType.UpInflateDownDeflate));
            AddControlAddress($"{prefix}DownInflateUpDeflate", GetLumbarActionAddress(config, LumbarActionType.DownInflateUpDeflate));
            AddControlAddress($"{prefix}BothInflate", GetLumbarActionAddress(config, LumbarActionType.SimultaneousInflate));
            AddControlAddress($"{prefix}BothDeflate", GetLumbarActionAddress(config, LumbarActionType.SimultaneousDeflate));
            AddControlAddress($"{prefix}HighPressureInletValve", FirstNonEmpty(manual.HighPressureInletValveAddress, manual.UpInflateDownDeflateAddress));
            AddControlAddress($"{prefix}HighPressureExhaustValve", FirstNonEmpty(manual.HighPressureExhaustValveAddress, manual.DownInflateUpDeflateAddress));
            AddControlAddress($"{prefix}LowPressureInletValve", FirstNonEmpty(manual.LowPressureInletValveAddress, manual.BothInflateAddress));
            AddControlAddress($"{prefix}LowPressureExhaustValve", FirstNonEmpty(manual.LowPressureExhaustValveAddress, manual.BothDeflateAddress));
            AddControlAddress($"{prefix}FullTestLight", manual.FullTestLightAddress);
            AddControlAddress($"{prefix}MassageLight", manual.MassageLightAddress);
            AddControlAddress($"{prefix}SideWingLight", manual.SideWingLightAddress);
            AddControlAddress($"{prefix}OKLight", manual.TestOkLightAddress);
            AddControlAddress($"{prefix}NGLight", manual.TestNgLightAddress);
        }

        private static string? GetLumbarActionAddress(ChannelConfig? config, LumbarActionType action)
        {
            if (config?.LumbarTestConfigs == null)
            {
                return null;
            }

            var match = config.LumbarTestConfigs
                .Where(c => c != null && c.Action == action && !string.IsNullOrWhiteSpace(c.MRegisterAddress))
                .OrderBy(c => c.Order)
                .FirstOrDefault();

            if (match == null)
            {
                return null;
            }

            return ModbusAddressHelper.ParseAddressList(match.MRegisterAddress)
                .Select(ModbusAddressHelper.NormalizeBitAddressToken)
                .FirstOrDefault(token => !string.IsNullOrWhiteSpace(token) &&
                                         token.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private void AddControlAddress(string key, string? address)
        {
            var normalized = ModbusAddressHelper.NormalizeBitAddressToken(address);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                _controlAddressMap[key] = normalized;
            }
        }

        public void UpdateWithPLCData(PLCData data)
        {
            if (data == null) return;
            int channelCount = _mainWindow?.GetConfiguredChannelCountForUi() ?? 4;

            try
            {
                // 更新通道状态
                UpdateChannelStatus(1, data.Channel1);
                UpdateChannelStatus(2, data.Channel2);
                if (channelCount >= 3)
                    UpdateChannelStatus(3, data.Channel3);
                if (channelCount >= 4)
                    UpdateChannelStatus(4, data.Channel4);

                // 更新按摩状态
                UpdateMassageStatus(
                    data.Channel1,
                    data.Channel2,
                    channelCount >= 3 ? data.Channel3 : null,
                    channelCount >= 4 ? data.Channel4 : null);

                // 更新控制状态
                UpdateControlStatus(
                    data.Channel1,
                    data.Channel2,
                    channelCount >= 3 ? data.Channel3 : null,
                    channelCount >= 4 ? data.Channel4 : null);

                // 更新485通讯数据
                UpdateCommData();

                // 更新系统数据
                UpdateSystemData(data.System);
            }
            catch (Exception ex)
            {
                _logService.LogError("手动控制界面更新错误", ex);
            }
        }

        private void ApplyChannelVisibility()
        {
            int channelCount = _mainWindow?.GetConfiguredChannelCountForUi() ?? 4;
            bool ch3Visible = channelCount >= 3;
            bool ch4Visible = channelCount >= 4;

            Ch3ControlPanel.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch4ControlPanel.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch3ReceiveGroup.Visibility = ch3Visible ? Visibility.Visible : Visibility.Collapsed;
            Ch4ReceiveGroup.Visibility = ch4Visible ? Visibility.Visible : Visibility.Collapsed;

            // 恢复默认布局: 每个通道占一列
            Ch1ControlPanel.SetValue(Grid.ColumnSpanProperty, 1);
            Ch2ControlPanel.SetValue(Grid.ColumnSpanProperty, 1);
            Ch2ControlPanel.SetValue(Grid.ColumnProperty, 1);

            if (channelCount == 2)
            {
                Col1.Width = new GridLength(1, GridUnitType.Star);
                Col2.Width = new GridLength(1, GridUnitType.Star);
                Col3.Width = new GridLength(0);
                Col4.Width = new GridLength(0);
            }
            else if (channelCount == 3)
            {
                Col1.Width = new GridLength(1, GridUnitType.Star);
                Col2.Width = new GridLength(1, GridUnitType.Star);
                Col3.Width = new GridLength(1, GridUnitType.Star);
                Col4.Width = new GridLength(0);
            }
            else // 4 channels
            {
                Col1.Width = new GridLength(1, GridUnitType.Star);
                Col2.Width = new GridLength(1, GridUnitType.Star);
                Col3.Width = new GridLength(1, GridUnitType.Star);
                Col4.Width = new GridLength(1, GridUnitType.Star);
            }

            Col5.Width = new GridLength(0);
        }

        private void UpdateChannelStatus(int channel, ChannelData channelData)
        {
            if (channelData == null) return;

            Ellipse stopButtonIndicator = channel switch
            {
                1 => Ch1StopButtonIndicator,
                2 => Ch2StopButtonIndicator,
                3 => Ch3StopButtonIndicator,
                4 => Ch4StopButtonIndicator,
                _ => null
            };
            Ellipse fullTestButtonIndicator = channel switch
            {
                1 => Ch1FullTestButtonIndicator,
                2 => Ch2FullTestButtonIndicator,
                3 => Ch3FullTestButtonIndicator,
                4 => Ch4FullTestButtonIndicator,
                _ => null
            };
            Ellipse massageButtonIndicator = channel switch
            {
                1 => Ch1MassageButtonIndicator,
                2 => Ch2MassageButtonIndicator,
                3 => Ch3MassageButtonIndicator,
                4 => Ch4MassageButtonIndicator,
                _ => null
            };
            Ellipse sideWingButtonIndicator = channel switch
            {
                1 => Ch1SideWingButtonIndicator,
                2 => Ch2SideWingButtonIndicator,
                3 => Ch3SideWingButtonIndicator,
                4 => Ch4SideWingButtonIndicator,
                _ => null
            };
            TextBlock heightText = channel switch
            {
                1 => TxtCh1Height,
                2 => TxtCh2Height,
                3 => TxtCh3Height,
                4 => TxtCh4Height,
                _ => null
            };
            TextBlock encoderText = channel switch
            {
                1 => TxtCh1Encoder,
                2 => TxtCh2Encoder,
                3 => TxtCh3Encoder,
                4 => TxtCh4Encoder,
                _ => null
            };
            TextBlock currentText = channel switch
            {
                1 => TxtCh1Current,
                2 => TxtCh2Current,
                3 => TxtCh3Current,
                4 => TxtCh4Current,
                _ => null
            };

            if (stopButtonIndicator == null || fullTestButtonIndicator == null || massageButtonIndicator == null || sideWingButtonIndicator == null)
            {
                return;
            }

            stopButtonIndicator.Fill = channelData.StopButton ? Brushes.Green : Brushes.Gray;
            fullTestButtonIndicator.Fill = channelData.AirLeakStartButton ? Brushes.Green : Brushes.Gray;
            massageButtonIndicator.Fill = channelData.MassageStart ? Brushes.Green : Brushes.Gray;
            sideWingButtonIndicator.Fill = channelData.SideWingStart ? Brushes.Green : Brushes.Gray;

            if (encoderText != null)
            {
                encoderText.Text = channelData.HeightRawValue.ToString();
            }

            if (heightText != null)
            {
                double actualHeight = ConvertChannelHeightToMillimeters(channel, channelData.HeightRawValue);
                heightText.Text = $"{actualHeight:F2} mm";
            }

            if (currentText != null)
            {
                currentText.Text = $"{channelData.CurrentValue:F2} KPa";
                if (_manualPressureTexts.TryGetValue(channel, out var pressureText))
                {
                    pressureText.Text = $"实时压力: {channelData.CurrentValue:F2} KPa";
                }
            }
        }

        private double ConvertChannelHeightToMillimeters(int channel, int rawHeight)
        {
            ChannelConfig? config = channel switch
            {
                1 => _currentModel?.Channel1Config,
                2 => _currentModel?.Channel2Config,
                3 => _currentModel?.Channel3Config,
                4 => _currentModel?.Channel4Config,
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

        private void UpdateMassageStatus(ChannelData ch1, ChannelData ch2, ChannelData ch3, ChannelData ch4)
        {
            try
            {
                // 通道1按摩点更新
                if (ch1?.MassagePoints != null)
                {
                    int maxPoints = Math.Min(ch1.MassagePoints.Length, MaxMassagePointCount);
                    for (int i = 0; i < maxPoints; i++)
                    {
                        string ch1Key = $"Ch1MassagePoint{i + 1}Indicator";
                        if (_massageIndicators.TryGetValue(ch1Key, out var indicator))
                        {
                            indicator.Fill = ch1.MassagePoints[i] ? Brushes.Green : Brushes.Gray;
                        }
                        else
                        {
                            _logService.LogWarning($"未找到指示器: {ch1Key}");
                        }
                    }
                }

                // 通道2按摩点更新
                if (ch2?.MassagePoints != null)
                {
                    int maxPoints = Math.Min(ch2.MassagePoints.Length, MaxMassagePointCount);
                    for (int i = 0; i < maxPoints; i++)
                    {
                        string ch2Key = $"Ch2MassagePoint{i + 1}Indicator";
                        if (_massageIndicators.TryGetValue(ch2Key, out var indicator))
                        {
                            indicator.Fill = ch2.MassagePoints[i] ? Brushes.Green : Brushes.Gray;
                        }
                        else
                        {
                            _logService.LogWarning($"未找到指示器: {ch2Key}");
                        }
                    }
                }

                if (ch3?.MassagePoints != null)
                {
                    int maxPoints = Math.Min(ch3.MassagePoints.Length, MaxMassagePointCount);
                    for (int i = 0; i < maxPoints; i++)
                    {
                        string ch3Key = $"Ch3MassagePoint{i + 1}Indicator";
                        if (_massageIndicators.TryGetValue(ch3Key, out var indicator))
                        {
                            indicator.Fill = ch3.MassagePoints[i] ? Brushes.Green : Brushes.Gray;
                        }
                        else
                        {
                            _logService.LogWarning($"未找到指示器: {ch3Key}");
                        }
                    }
                }

                if (ch4?.MassagePoints != null)
                {
                    int maxPoints = Math.Min(ch4.MassagePoints.Length, MaxMassagePointCount);
                    for (int i = 0; i < maxPoints; i++)
                    {
                        string ch4Key = $"Ch4MassagePoint{i + 1}Indicator";
                        if (_massageIndicators.TryGetValue(ch4Key, out var indicator))
                        {
                            indicator.Fill = ch4.MassagePoints[i] ? Brushes.Green : Brushes.Gray;
                        }
                        else
                        {
                            _logService.LogWarning($"未找到指示器: {ch4Key}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("按摩状态更新错误", ex);
            }
        }

        private void UpdateControlStatus(ChannelData ch1, ChannelData ch2, ChannelData ch3, ChannelData ch4)
        {
            if (ch1 != null)
            {
                // 更新通道1控制状态
                UpdateControlIndicator("Ch1PowerOffIndicator", ch1.PowerOff);
                _controlStates["Ch1PowerOff"] = ch1.PowerOff;
                UpdateControlIndicator("Ch1CylinderOpenIndicator", ch1.CylinderOpen);
                _controlStates["Ch1CylinderOpen"] = ch1.CylinderOpen;
                UpdateControlIndicator("Ch1CylinderCloseIndicator", ch1.CylinderClose);
                _controlStates["Ch1CylinderClose"] = ch1.CylinderClose;
                UpdateControlIndicator("Ch1DriverSwitchIndicator", ch1.DriverSwitch);
                _controlStates["Ch1DriverSwitch"] = ch1.DriverSwitch;
                UpdateControlIndicator("Ch1MassageKeyIndicator", ch1.MassageKey);
                _controlStates["Ch1MassageKey"] = ch1.MassageKey;
                UpdateControlIndicator("Ch1FullTestLightIndicator", ch1.FullTestLight);
                _controlStates["Ch1FullTestLight"] = ch1.FullTestLight;
                UpdateControlIndicator("Ch1MassageLightIndicator", ch1.MassageLight);
                _controlStates["Ch1MassageLight"] = ch1.MassageLight;
                UpdateControlIndicator("Ch1SideWingLightIndicator", ch1.SideWingLight);
                _controlStates["Ch1SideWingLight"] = ch1.SideWingLight;
                UpdateControlIndicator("Ch1OKLightIndicator", ch1.TestOKLight);
                _controlStates["Ch1OKLight"] = ch1.TestOKLight;
                UpdateControlIndicator("Ch1NGLightIndicator", ch1.TestNGLight);
                _controlStates["Ch1NGLight"] = ch1.TestNGLight;
                UpdateControlIndicator("Ch1UpInflateDownDeflateIndicator", ch1.UpInflateDownDeflate);
                _controlStates["Ch1UpInflateDownDeflate"] = ch1.UpInflateDownDeflate;
                UpdateControlIndicator("Ch1DownInflateUpDeflateIndicator", ch1.DownInflateUpDeflate);
                _controlStates["Ch1DownInflateUpDeflate"] = ch1.DownInflateUpDeflate;
                UpdateControlIndicator("Ch1BothInflateIndicator", ch1.BothInflate);
                _controlStates["Ch1BothInflate"] = ch1.BothInflate;
                UpdateControlIndicator("Ch1BothDeflateIndicator", ch1.BothDeflate);
                _controlStates["Ch1BothDeflate"] = ch1.BothDeflate;
                UpdateControlIndicator("Ch1HighPressureInletValveIndicator", ch1.HighPressureInletValve);
                _controlStates["Ch1HighPressureInletValve"] = ch1.HighPressureInletValve;
                UpdateControlIndicator("Ch1HighPressureExhaustValveIndicator", ch1.HighPressureExhaustValve);
                _controlStates["Ch1HighPressureExhaustValve"] = ch1.HighPressureExhaustValve;
                UpdateControlIndicator("Ch1LowPressureInletValveIndicator", ch1.LowPressureInletValve);
                _controlStates["Ch1LowPressureInletValve"] = ch1.LowPressureInletValve;
                UpdateControlIndicator("Ch1LowPressureExhaustValveIndicator", ch1.LowPressureExhaustValve);
                _controlStates["Ch1LowPressureExhaustValve"] = ch1.LowPressureExhaustValve;
            }

            if (ch2 != null)
            {
                // 更新通道2控制状态
                UpdateControlIndicator("Ch2PowerOffIndicator", ch2.PowerOff);
                _controlStates["Ch2PowerOff"] = ch2.PowerOff;
                UpdateControlIndicator("Ch2CylinderOpenIndicator", ch2.CylinderOpen);
                _controlStates["Ch2CylinderOpen"] = ch2.CylinderOpen;
                UpdateControlIndicator("Ch2CylinderCloseIndicator", ch2.CylinderClose);
                _controlStates["Ch2CylinderClose"] = ch2.CylinderClose;
                UpdateControlIndicator("Ch2DriverSwitchIndicator", ch2.DriverSwitch);
                _controlStates["Ch2DriverSwitch"] = ch2.DriverSwitch;
                UpdateControlIndicator("Ch2MassageKeyIndicator", ch2.MassageKey);
                _controlStates["Ch2MassageKey"] = ch2.MassageKey;
                UpdateControlIndicator("Ch2FullTestLightIndicator", ch2.FullTestLight);
                _controlStates["Ch2FullTestLight"] = ch2.FullTestLight;
                UpdateControlIndicator("Ch2MassageLightIndicator", ch2.MassageLight);
                _controlStates["Ch2MassageLight"] = ch2.MassageLight;
                UpdateControlIndicator("Ch2SideWingLightIndicator", ch2.SideWingLight);
                _controlStates["Ch2SideWingLight"] = ch2.SideWingLight;
                UpdateControlIndicator("Ch2OKLightIndicator", ch2.TestOKLight);
                _controlStates["Ch2OKLight"] = ch2.TestOKLight;
                UpdateControlIndicator("Ch2NGLightIndicator", ch2.TestNGLight);
                _controlStates["Ch2NGLight"] = ch2.TestNGLight;
                UpdateControlIndicator("Ch2UpInflateDownDeflateIndicator", ch2.UpInflateDownDeflate);
                _controlStates["Ch2UpInflateDownDeflate"] = ch2.UpInflateDownDeflate;
                UpdateControlIndicator("Ch2DownInflateUpDeflateIndicator", ch2.DownInflateUpDeflate);
                _controlStates["Ch2DownInflateUpDeflate"] = ch2.DownInflateUpDeflate;
                UpdateControlIndicator("Ch2BothInflateIndicator", ch2.BothInflate);
                _controlStates["Ch2BothInflate"] = ch2.BothInflate;
                UpdateControlIndicator("Ch2BothDeflateIndicator", ch2.BothDeflate);
                _controlStates["Ch2BothDeflate"] = ch2.BothDeflate;
                UpdateControlIndicator("Ch2HighPressureInletValveIndicator", ch2.HighPressureInletValve);
                _controlStates["Ch2HighPressureInletValve"] = ch2.HighPressureInletValve;
                UpdateControlIndicator("Ch2HighPressureExhaustValveIndicator", ch2.HighPressureExhaustValve);
                _controlStates["Ch2HighPressureExhaustValve"] = ch2.HighPressureExhaustValve;
                UpdateControlIndicator("Ch2LowPressureInletValveIndicator", ch2.LowPressureInletValve);
                _controlStates["Ch2LowPressureInletValve"] = ch2.LowPressureInletValve;
                UpdateControlIndicator("Ch2LowPressureExhaustValveIndicator", ch2.LowPressureExhaustValve);
                _controlStates["Ch2LowPressureExhaustValve"] = ch2.LowPressureExhaustValve;
            }

            if (ch3 != null)
            {
                UpdateControlIndicator("Ch3PowerOffIndicator", ch3.PowerOff);
                _controlStates["Ch3PowerOff"] = ch3.PowerOff;
                UpdateControlIndicator("Ch3CylinderOpenIndicator", ch3.CylinderOpen);
                _controlStates["Ch3CylinderOpen"] = ch3.CylinderOpen;
                UpdateControlIndicator("Ch3CylinderCloseIndicator", ch3.CylinderClose);
                _controlStates["Ch3CylinderClose"] = ch3.CylinderClose;
                UpdateControlIndicator("Ch3DriverSwitchIndicator", ch3.DriverSwitch);
                _controlStates["Ch3DriverSwitch"] = ch3.DriverSwitch;
                UpdateControlIndicator("Ch3MassageKeyIndicator", ch3.MassageKey);
                _controlStates["Ch3MassageKey"] = ch3.MassageKey;
                UpdateControlIndicator("Ch3FullTestLightIndicator", ch3.FullTestLight);
                _controlStates["Ch3FullTestLight"] = ch3.FullTestLight;
                UpdateControlIndicator("Ch3MassageLightIndicator", ch3.MassageLight);
                _controlStates["Ch3MassageLight"] = ch3.MassageLight;
                UpdateControlIndicator("Ch3SideWingLightIndicator", ch3.SideWingLight);
                _controlStates["Ch3SideWingLight"] = ch3.SideWingLight;
                UpdateControlIndicator("Ch3OKLightIndicator", ch3.TestOKLight);
                _controlStates["Ch3OKLight"] = ch3.TestOKLight;
                UpdateControlIndicator("Ch3NGLightIndicator", ch3.TestNGLight);
                _controlStates["Ch3NGLight"] = ch3.TestNGLight;
                UpdateControlIndicator("Ch3UpInflateDownDeflateIndicator", ch3.UpInflateDownDeflate);
                _controlStates["Ch3UpInflateDownDeflate"] = ch3.UpInflateDownDeflate;
                UpdateControlIndicator("Ch3DownInflateUpDeflateIndicator", ch3.DownInflateUpDeflate);
                _controlStates["Ch3DownInflateUpDeflate"] = ch3.DownInflateUpDeflate;
                UpdateControlIndicator("Ch3BothInflateIndicator", ch3.BothInflate);
                _controlStates["Ch3BothInflate"] = ch3.BothInflate;
                UpdateControlIndicator("Ch3BothDeflateIndicator", ch3.BothDeflate);
                _controlStates["Ch3BothDeflate"] = ch3.BothDeflate;
                UpdateControlIndicator("Ch3HighPressureInletValveIndicator", ch3.HighPressureInletValve);
                _controlStates["Ch3HighPressureInletValve"] = ch3.HighPressureInletValve;
                UpdateControlIndicator("Ch3HighPressureExhaustValveIndicator", ch3.HighPressureExhaustValve);
                _controlStates["Ch3HighPressureExhaustValve"] = ch3.HighPressureExhaustValve;
                UpdateControlIndicator("Ch3LowPressureInletValveIndicator", ch3.LowPressureInletValve);
                _controlStates["Ch3LowPressureInletValve"] = ch3.LowPressureInletValve;
                UpdateControlIndicator("Ch3LowPressureExhaustValveIndicator", ch3.LowPressureExhaustValve);
                _controlStates["Ch3LowPressureExhaustValve"] = ch3.LowPressureExhaustValve;
            }

            if (ch4 != null)
            {
                UpdateControlIndicator("Ch4PowerOffIndicator", ch4.PowerOff);
                _controlStates["Ch4PowerOff"] = ch4.PowerOff;
                UpdateControlIndicator("Ch4CylinderOpenIndicator", ch4.CylinderOpen);
                _controlStates["Ch4CylinderOpen"] = ch4.CylinderOpen;
                UpdateControlIndicator("Ch4CylinderCloseIndicator", ch4.CylinderClose);
                _controlStates["Ch4CylinderClose"] = ch4.CylinderClose;
                UpdateControlIndicator("Ch4DriverSwitchIndicator", ch4.DriverSwitch);
                _controlStates["Ch4DriverSwitch"] = ch4.DriverSwitch;
                UpdateControlIndicator("Ch4MassageKeyIndicator", ch4.MassageKey);
                _controlStates["Ch4MassageKey"] = ch4.MassageKey;
                UpdateControlIndicator("Ch4FullTestLightIndicator", ch4.FullTestLight);
                _controlStates["Ch4FullTestLight"] = ch4.FullTestLight;
                UpdateControlIndicator("Ch4MassageLightIndicator", ch4.MassageLight);
                _controlStates["Ch4MassageLight"] = ch4.MassageLight;
                UpdateControlIndicator("Ch4SideWingLightIndicator", ch4.SideWingLight);
                _controlStates["Ch4SideWingLight"] = ch4.SideWingLight;
                UpdateControlIndicator("Ch4OKLightIndicator", ch4.TestOKLight);
                _controlStates["Ch4OKLight"] = ch4.TestOKLight;
                UpdateControlIndicator("Ch4NGLightIndicator", ch4.TestNGLight);
                _controlStates["Ch4NGLight"] = ch4.TestNGLight;
                UpdateControlIndicator("Ch4UpInflateDownDeflateIndicator", ch4.UpInflateDownDeflate);
                _controlStates["Ch4UpInflateDownDeflate"] = ch4.UpInflateDownDeflate;
                UpdateControlIndicator("Ch4DownInflateUpDeflateIndicator", ch4.DownInflateUpDeflate);
                _controlStates["Ch4DownInflateUpDeflate"] = ch4.DownInflateUpDeflate;
                UpdateControlIndicator("Ch4BothInflateIndicator", ch4.BothInflate);
                _controlStates["Ch4BothInflate"] = ch4.BothInflate;
                UpdateControlIndicator("Ch4BothDeflateIndicator", ch4.BothDeflate);
                _controlStates["Ch4BothDeflate"] = ch4.BothDeflate;
                UpdateControlIndicator("Ch4HighPressureInletValveIndicator", ch4.HighPressureInletValve);
                _controlStates["Ch4HighPressureInletValve"] = ch4.HighPressureInletValve;
                UpdateControlIndicator("Ch4HighPressureExhaustValveIndicator", ch4.HighPressureExhaustValve);
                _controlStates["Ch4HighPressureExhaustValve"] = ch4.HighPressureExhaustValve;
                UpdateControlIndicator("Ch4LowPressureInletValveIndicator", ch4.LowPressureInletValve);
                _controlStates["Ch4LowPressureInletValve"] = ch4.LowPressureInletValve;
                UpdateControlIndicator("Ch4LowPressureExhaustValveIndicator", ch4.LowPressureExhaustValve);
                _controlStates["Ch4LowPressureExhaustValve"] = ch4.LowPressureExhaustValve;
            }
        }

        private void UpdateCommData()
        {
            _isUpdatingFromPlc = true;
            try
            {
                TxtCh1ReceiveData.Text = FormatCommValues(GetReceiveData(1));
                TxtCh2ReceiveData.Text = FormatCommValues(GetReceiveData(2));
                TxtCh3ReceiveData.Text = FormatCommValues(GetReceiveData(3));
                TxtCh4ReceiveData.Text = FormatCommValues(GetReceiveData(4));
            }
            finally
            {
                _isUpdatingFromPlc = false;
            }
        }

        private byte[] GetReceiveData(int channel)
        {
            return _receiveData.TryGetValue(channel, out var data) ? data : new byte[20];
        }

        private void UpdateSystemData(SystemData systemData)
        {
            if (systemData != null)
            {
                // 可以在这里更新系统级别的显示，如测试步骤和结果
                // 例如：TxtTestStep.Text = $"步骤: {(TestStep)systemData.TestStep}";
                // TxtTestResult.Text = $"结果: {(TestResult)systemData.TestResult}";
            }
        }

        private void UpdateControlIndicator(string indicatorName, bool state)
        {
            var indicator = FindName(indicatorName) as Ellipse;
            if (indicator != null)
            {
                indicator.Fill = state ? Brushes.Green : Brushes.Gray;
            }
        }

        private const int MaxMassagePointCount = 32;

        private void InitializeMassageStatus()
        {
            try
            {
                Ch1MassageStatusPanel.Children.Clear();
                Ch2MassageStatusPanel.Children.Clear();
                Ch3MassageStatusPanel.Children.Clear();
                Ch4MassageStatusPanel.Children.Clear();
                _massageIndicators.Clear();

                // 创建通道1按摩点1-32的状态显示
                for (int i = 1; i <= MaxMassagePointCount; i++)
                {
                    AddMassagePointToPanel(Ch1MassageStatusPanel, i, 1);
                    AddMassagePointToPanel(Ch2MassageStatusPanel, i, 2);
                    AddMassagePointToPanel(Ch3MassageStatusPanel, i, 3);
                    AddMassagePointToPanel(Ch4MassageStatusPanel, i, 4);
                }

                // 初始设置为灰色
                foreach (var indicator in _massageIndicators.Values)
                {
                    indicator.Fill = Brushes.Gray;
                }

                _logService.LogInfo($"初始化完成，共创建 {_massageIndicators.Count} 个按摩点指示器");
            }
            catch (Exception ex)
            {
                _logService.LogError("初始化按摩状态错误", ex);
            }
        }

        private void AddMassagePointToPanel(StackPanel panel, int pointNumber, int channel)
        {
            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };

            stackPanel.Children.Add(new TextBlock
            {
                Text = $"按摩点{pointNumber}:",
                Width = 70,
                VerticalAlignment = VerticalAlignment.Center
            });

            var indicator = new Ellipse
            {
                Width = 15,
                Height = 15,
                Fill = Brushes.Gray,
                Margin = new Thickness(5, 0, 5, 0)
            };

            // 为指示器创建唯一名称并存储
            string indicatorName = $"Ch{channel}MassagePoint{pointNumber}Indicator";
            indicator.Name = indicatorName;
            _massageIndicators[indicatorName] = indicator;

            stackPanel.Children.Add(indicator);

            var durationText = new TextBlock
            {
                Text = "0ms",
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = $"duration_{channel}_{pointNumber}"
            };
            stackPanel.Children.Add(durationText);

            panel.Children.Add(stackPanel);

            // 注册名称以便后续查找
            this.RegisterName(indicatorName, indicator);
        }

        private string FormatCommValues(IReadOnlyList<byte> values)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", values.Select(v => v.ToString("X2")));
        }

        private byte[] ParseSendData(string rawData)
        {
            var values = new byte[20];

            if (string.IsNullOrWhiteSpace(rawData))
            {
                return values;
            }

            var tokens = rawData
                .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(values.Length)
                .ToArray();

            for (int i = 0; i < tokens.Length; i++)
            {
                if (!TryParseToken(tokens[i], out byte parsedValue))
                {
                    throw new FormatException($"无效的数据值: '{tokens[i]}'");
                }

                values[i] = parsedValue;
            }

            return values;
        }

        private bool TryParseToken(string token, out byte value)
        {
            token = token.Trim();

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(2);
            }

            if (ushort.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort hexValue) &&
                hexValue <= byte.MaxValue)
            {
                value = (byte)hexValue;
                return true;
            }

            if (ushort.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort decValue) &&
                decValue <= byte.MaxValue)
            {
                value = (byte)decValue;
                return true;
            }

            value = 0;
            return false;
        }

        // 通道1控制方法 - 可开可关
        private async void Ch1PowerOff_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1PowerOff");

        private async void Ch1CylinderOpen_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1CylinderOpen");

        private async void Ch1CylinderClose_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1CylinderClose");

        private async void Ch1DriverSwitch_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1DriverSwitch");

        private async void Ch1MassageKey_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1MassageKey");

        // 通道2控制方法 - 可开可关（更新地址映射）
        private async void Ch2PowerOff_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2PowerOff");

        private async void Ch2CylinderOpen_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2CylinderOpen");

        private async void Ch2CylinderClose_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2CylinderClose");

        private async void Ch2DriverSwitch_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2DriverSwitch");

        private async void Ch2MassageKey_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2MassageKey");

        // 通道3控制方法
        private async void Ch3PowerOff_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3PowerOff");

        private async void Ch3CylinderOpen_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3CylinderOpen");

        private async void Ch3CylinderClose_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3CylinderClose");

        private async void Ch3DriverSwitch_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3DriverSwitch");

        private async void Ch3MassageKey_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3MassageKey");

        // 通道4控制方法
        private async void Ch4PowerOff_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4PowerOff");

        private async void Ch4CylinderOpen_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4CylinderOpen");

        private async void Ch4CylinderClose_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4CylinderClose");

        private async void Ch4DriverSwitch_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4DriverSwitch");

        private async void Ch4MassageKey_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4MassageKey");

        // 充放气控制 (两个通道通用) - 可开可关
        private async void Ch1UpInflateDownDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1UpInflateDownDeflate");

        private async void Ch1DownInflateUpDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1DownInflateUpDeflate");

        private async void Ch1BothInflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1BothInflate");

        private async void Ch1BothDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1BothDeflate");

        private async void Ch2UpInflateDownDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2UpInflateDownDeflate");

        private async void Ch2DownInflateUpDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2DownInflateUpDeflate");

        private async void Ch2BothInflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2BothInflate");

        private async void Ch2BothDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2BothDeflate");

        private async void Ch3UpInflateDownDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3UpInflateDownDeflate");

        private async void Ch3DownInflateUpDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3DownInflateUpDeflate");

        private async void Ch3BothInflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3BothInflate");

        private async void Ch3BothDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3BothDeflate");

        private async void Ch4UpInflateDownDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4UpInflateDownDeflate");

        private async void Ch4DownInflateUpDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4DownInflateUpDeflate");

        private async void Ch4BothInflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4BothInflate");

        private async void Ch4BothDeflate_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4BothDeflate");

        // 指示灯控制 (两个通道通用) - 可开可关
        private async void Ch1FullTestLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1FullTestLight");

        private async void Ch1MassageLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1MassageLight");

        private async void Ch1SideWingLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1SideWingLight");

        private async void Ch1OKLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1OKLight");

        private async void Ch1NGLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch1NGLight");

        private async void Ch2FullTestLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2FullTestLight");

        private async void Ch2MassageLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2MassageLight");

        private async void Ch2SideWingLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2SideWingLight");

        private async void Ch2OKLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2OKLight");

        private async void Ch2NGLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch2NGLight");

        private async void Ch3FullTestLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3FullTestLight");

        private async void Ch3MassageLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3MassageLight");

        private async void Ch3SideWingLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3SideWingLight");

        private async void Ch3OKLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3OKLight");

        private async void Ch3NGLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch3NGLight");

        private async void Ch4FullTestLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4FullTestLight");

        private async void Ch4MassageLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4MassageLight");

        private async void Ch4SideWingLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4SideWingLight");

        private async void Ch4OKLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4OKLight");

        private async void Ch4NGLight_Click(object sender, RoutedEventArgs e) => await ToggleControlAsync("Ch4NGLight");

        private async Task ToggleControlAsync(string controlKey)
        {
            try
            {
                if (_mainWindow == null)
                {
                    return;
                }

                if (!_controlAddressMap.TryGetValue(controlKey, out string address))
                {
                    return;
                }

                bool currentState = _controlStates.TryGetValue(controlKey, out bool state) ? state : false;
                bool newState = !currentState;

                bool success = await _mainWindow.WritePLCBit(address, newState);
                if (success)
                {
                    _controlStates[controlKey] = newState;
                    UpdateControlIndicator($"{controlKey}Indicator", newState);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError($"切换控制{controlKey}时发生错误", ex);
            }
        }

        // 485通讯事件处理方法
        private async void BtnSingleSend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var textBox = GetSendTextBox();
                string sendData = textBox?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sendData))
                {
                    AddCommLog($"[{DateTime.Now:HH:mm:ss}] 发送数据为空");
                    return;
                }

                var values = ParseSendData(sendData);
                string formattedValues = FormatCommValues(values);

                bool sent = await _commService.SendRawMessageAsync(values);
                if (sent)
                {
                    _isUpdatingFromPlc = true;
                    try
                    {
                        if (textBox != null)
                        {
                            textBox.Text = formattedValues;
                        }
                    }
                    finally
                    {
                        _isUpdatingFromPlc = false;
                    }

                    AddCommLog($"[{DateTime.Now:HH:mm:ss}] 单次发送: {formattedValues}");
                }
            }
            catch (FormatException ex)
            {
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 发送数据格式错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 单次发送错误: {ex.Message}");
            }
        }

        private async void BtnContinuousSend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var textBox = GetSendTextBox();
                string sendData = textBox?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sendData))
                {
                    AddCommLog($"[{DateTime.Now:HH:mm:ss}] 发送数据为空");
                    return;
                }

                var values = ParseSendData(sendData);
                string formattedValues = FormatCommValues(values);

                _isUpdatingFromPlc = true;
                try
                {
                    if (textBox != null)
                    {
                        textBox.Text = formattedValues;
                    }
                }
                finally
                {
                    _isUpdatingFromPlc = false;
                }

                _pendingContinuousPayload = values;
                _isContinuousSending = true;
                _continuousSendTimer.Start();
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 连续发送开始: {formattedValues}");
            }
            catch (FormatException ex)
            {
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 连续发送数据格式错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 连续发送错误: {ex.Message}");
            }
        }

        private async void BtnStopContinuous_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isContinuousSending = false;
                _continuousSendTimer.Stop();
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 连续发送已停止");
            }
            catch (Exception ex)
            {
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 停止连续发送错误: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private void BtnClearSend_Click(object sender, RoutedEventArgs e)
        {
            var textBox = GetSendTextBox();
            textBox?.Clear();
            AddCommLog($"[{DateTime.Now:HH:mm:ss}] 发送数据已清除");
        }

        private void TxtSendData_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFromPlc)
            {
                return;
            }
        }

        private void TxtSendData_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtCommLog.Clear();
        }

        private async void ContinuousSendTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isContinuousSending || _pendingContinuousPayload.Length == 0)
            {
                return;
            }

            bool sent = await _commService.SendRawMessageAsync(_pendingContinuousPayload);
            if (!sent)
            {
                _isContinuousSending = false;
                _continuousSendTimer.Stop();
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 连续发送失败，已停止");
            }
        }

        private void CommService_MessageReceived(object? sender, CommMessageReceivedEventArgs e)
        {
            if (e == null || e.Payload.Length == 0)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                _receiveData[e.Channel] = e.Payload;
                UpdateCommData();
                AddCommLog($"[{DateTime.Now:HH:mm:ss}] 收到通道{e.Channel}报文: {FormatCommValues(e.Payload)}");
            });
        }

        private void AddCommLog(string logMessage)
        {
            if (TxtCommLog != null)
            {
                TxtCommLog.AppendText(logMessage + Environment.NewLine);
                TxtCommLog.ScrollToEnd();
            }
        }

        public void Dispose()
        {
            // 清理资源
        }
    }
}
