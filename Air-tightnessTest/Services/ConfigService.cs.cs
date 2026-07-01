// Services/ConfigService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private readonly string _modelsPath;
        private readonly string _legacyConfigPath;
        private readonly string _legacyModelsPath;
        private readonly ILogService _logService;

        public ConfigService(ILogService? logService = null)
        {
            _logService = logService ?? LogService.Instance;
            _configPath = ResolveConfigRoot();
            _modelsPath = Path.Combine(_configPath, "Models");
            _legacyConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            _legacyModelsPath = Path.Combine(_legacyConfigPath, "Models");

            if (!Directory.Exists(_configPath))
                Directory.CreateDirectory(_configPath);
            if (!Directory.Exists(_modelsPath))
                Directory.CreateDirectory(_modelsPath);

            MigrateLegacyConfig();
        }

        private static string ResolveConfigRoot()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            }

            return Path.Combine(localAppData, "LumbarMassageTest", "Config");
        }

        private void MigrateLegacyConfig()
        {
            try
            {
                if (string.Equals(_legacyConfigPath, _configPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!Directory.Exists(_legacyConfigPath))
                {
                    return;
                }

                var legacyConfigFile = Path.Combine(_legacyConfigPath, "app.json");
                var newConfigFile = Path.Combine(_configPath, "app.json");
                if (File.Exists(legacyConfigFile) && !File.Exists(newConfigFile))
                {
                    File.Copy(legacyConfigFile, newConfigFile);
                }

                if (Directory.Exists(_legacyModelsPath))
                {
                    var existingModels = Directory.GetFiles(_modelsPath, "*.json").Length;
                    if (existingModels == 0)
                    {
                        var legacyModels = Directory.GetFiles(_legacyModelsPath, "*.json");
                        foreach (var legacyModel in legacyModels)
                        {
                            var targetPath = Path.Combine(_modelsPath, Path.GetFileName(legacyModel));
                            if (!File.Exists(targetPath))
                            {
                                File.Copy(legacyModel, targetPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("迁移旧配置失败", ex);
            }
        }

        public async Task<List<ProductModel>> LoadProductModelsAsync()
        {
            var models = new List<ProductModel>();

            try
            {
                var files = Directory.GetFiles(_modelsPath, "*.json");
                foreach (var file in files)
                {
                    var json = await File.ReadAllTextAsync(file);
                    var model = JsonSerializer.Deserialize<ProductModel>(json);
                    if (model != null)
                    {
                        EnsureChannelConfigDefaults(model.Channel1Config);
                        EnsureChannelConfigDefaults(model.Channel2Config);
                        EnsureChannelConfigDefaults(model.Channel3Config);
                        EnsureChannelConfigDefaults(model.Channel4Config);
                        EnsureCurrentSleepConfigDefaults(model);
                        ApplyCurrentSleepConfigToChannels(model);
                        models.Add(model);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("鍔犺浇浜у搧鍨嬪彿澶辫触", ex);
            }

            if (models.Count == 0)
            {
                models.Add(CreateDefaultModel());
                await SaveProductModelAsync(models[0]);
            }

            return models;
        }

        public async Task<bool> SaveProductModelAsync(ProductModel model)
        {
            try
            {
                var filePath = Path.Combine(_modelsPath, $"{model.ModelName}.json");
                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("淇濆瓨浜у搧鍨嬪彿澶辫触", ex);
                return false;
            }
        }

        public async Task<bool> DeleteProductModelAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }

            try
            {
                var filePath = Path.Combine(_modelsPath, $"{modelName}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                var legacyPath = Path.Combine(_legacyModelsPath, $"{modelName}.json");
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }

                await Task.CompletedTask;
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("鍒犻櫎浜у搧鍨嬪彿澶辫触", ex);
                return false;
            }
        }

        public async Task<AppConfig> LoadAppConfigAsync()
        {
            var configFile = Path.Combine(_configPath, "app.json");

            try
            {
                if (File.Exists(configFile))
                {
                    var json = await File.ReadAllTextAsync(configFile);
                    var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                    EnsureAppConfigDefaults(config);
                    return config;
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("鍔犺浇搴旂敤閰嶇疆澶辫触", ex);
            }

            var defaultConfig = new AppConfig
            {
                PLCIPAddress = "192.168.1.188",
                PLCPort = 502,
                PlcDiscreteInputCount = 256,
                PlcCoilCount = 128,
                AutoSave = true,
                PLCStationId = IPLCService.DefaultUnitId,
                MesServerIp = "127.0.0.1",
                MesServerPort = 8080,
                MesProtocol = "TCP",
                SerialDevice1 = SerialPortConfig.CreateDefaultDevice1(),
                SerialDevice2 = SerialPortConfig.CreateDefaultDevice2(),
                PressureModuleStationId = 1,
                PressureInputStartAddress = 40097,
                PressureOutputStartAddress = 40023,
            };

            EnsureAppConfigDefaults(defaultConfig);

            await SaveAppConfigAsync(defaultConfig);
            return defaultConfig;
        }

        public async Task<bool> SaveAppConfigAsync(AppConfig config)
        {
            try
            {
                if (config == null)
                {
                    throw new ArgumentNullException(nameof(config));
                }

                EnsureAppConfigDefaults(config);

                var configFile = Path.Combine(_configPath, "app.json");
                var json = JsonSerializer.Serialize(config, JsonOptions);
                await File.WriteAllTextAsync(configFile, json);
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("淇濆瓨搴旂敤閰嶇疆澶辫触", ex);
                return false;
            }
        }

        private ProductModel CreateDefaultModel()
        {
            var currentSleepConfig = BuildDefaultCurrentSleepConfig();
            var model = new ProductModel
            {
                ModelName = "榛樿鍨嬪彿",
                Description = "榛樿鑵版墭鎸夋懇浜у搧",
                ImagePath = string.Empty,
                CurrentSleepConfig = currentSleepConfig,
                ProcessConfig = new TestProcessConfig
                {
                    EnableBarcodeCheck = true,
                    MaxTestCount = 3,
                    EnableCurrentMonitoring = true,
                    RecordCurrentBeforeStart = false,
                    CheckSameModel = true,
                    PromptOnDuplicateBarcode = true,
                    EnableBarcodePrefixCheck = false,
                    BarcodePrefix = string.Empty,
                    EnableLumbarTest = false,
                    EnableMassageTest = false,
                    ModeSwitchPowerOffDuration = 5000
                }
            };

            model.Channel1Config = BuildDefaultChannelConfig("閫氶亾1", currentSleepConfig);
            model.Channel2Config = DeepCopyChannelConfig(model.Channel1Config, "閫氶亾2");
            model.Channel3Config = DeepCopyChannelConfig(model.Channel1Config, "閫氶亾3");
            model.Channel4Config = DeepCopyChannelConfig(model.Channel1Config, "閫氶亾4");

            return model;
        }

        private CurrentSleepConfig BuildDefaultCurrentSleepConfig()
        {
            return new CurrentSleepConfig
            {
                StaticCurrentMin = 20,
                StaticCurrentMax = 80,
                WorkCurrentMin = 200,
                WorkCurrentMax = 1500,
                CurrentOverLimit = 2000,
                SleepCurrentThreshold = 0.5,
                SleepTestTimeout = 5000,
                HeightCodeMin = 0,
                HeightCodeMax = 5000,
                HeightRangeMin = 0,
                HeightRangeMax = 50
            };
        }

        private ChannelConfig BuildDefaultChannelConfig(string name, CurrentSleepConfig currentSleepConfig)
        {
            return new ChannelConfig
            {
                ChannelName = name,
                StaticCurrentMin = currentSleepConfig.StaticCurrentMin,
                StaticCurrentMax = currentSleepConfig.StaticCurrentMax,
                WorkCurrentMin = currentSleepConfig.WorkCurrentMin,
                WorkCurrentMax = currentSleepConfig.WorkCurrentMax,
                CurrentOverLimit = currentSleepConfig.CurrentOverLimit,
                SleepCurrentThreshold = currentSleepConfig.SleepCurrentThreshold,
                SleepTestTimeout = currentSleepConfig.SleepTestTimeout,
                HeightCodeMin = currentSleepConfig.HeightCodeMin,
                HeightCodeMax = currentSleepConfig.HeightCodeMax,
                HeightRangeMin = currentSleepConfig.HeightRangeMin,
                HeightRangeMax = currentSleepConfig.HeightRangeMax,
                PowerOffDurationMin = 5000,
                PowerOffDurationMax = 15000,
                LumbarTestConfigs = new List<LumbarTestConfig>
                {
                    new LumbarTestConfig
                    {
                        Order = 1,
                        Action = LumbarActionType.UpInflateDownDeflate,
                        TargetHeight = 50,
                        TargetTime = 3000
                    },
                    new LumbarTestConfig
                    {
                        Order = 2,
                        Action = LumbarActionType.DownInflateUpDeflate,
                        TargetHeight = 30,
                        TargetTime = 2500
                    },
                    new LumbarTestConfig
                    {
                        Order = 3,
                        Action = LumbarActionType.SimultaneousInflate,
                        TargetHeight = 60,
                        TargetTime = 4000
                    },
                    new LumbarTestConfig
                    {
                        Order = 4,
                        Action = LumbarActionType.SimultaneousDeflate,
                        TargetHeight = 0,
                        TargetTime = 1000
                    }
                },
                MassageConfigs = new List<MassageConfig>
                {
                    new MassageConfig
                    {
                        Point = 1,
                        HeightSwitchAddress = string.Empty,
                    },
                    new MassageConfig
                    {
                        Point = 2,
                        HeightSwitchAddress = string.Empty,
                    }
                },
                MassageTestSettings = new MassageTestSettings
                {
                    TotalDuration = 30000,
                    HighLevelDurationMin = 500,
                    HighLevelDurationMax = 4000,
                    PeakCurrentMin = 150,
                    PeakCurrentMax = 1200,
                    AverageCurrentMin = 120,
                    AverageCurrentMax = 900
                },
                MessageKeyTestConfigs = new List<MessageKeyTestConfig>
                {
                    new MessageKeyTestConfig
                    {
                        Order = 1,
                        Enabled = true,
                        OutputRegisterAddress = string.Empty,
                        ReadByteIndex = 8,
                        ExpectedValue = 0x02,
                        TriggerMode = MessageKeyTriggerMode.Continuous
                    }
                },
                AirLeakTestSettings = new AirLeakTestSettings(),
                PressureConfig = new PressureChannelConfig(),
                ManualControl = new ManualControlAddressConfig()
            };
        }

        private ChannelConfig DeepCopyChannelConfig(ChannelConfig source, string channelName)
        {
            return new ChannelConfig
            {
                ChannelName = channelName,
                StaticCurrentMin = source.StaticCurrentMin,
                StaticCurrentMax = source.StaticCurrentMax,
                WorkCurrentMin = source.WorkCurrentMin,
                WorkCurrentMax = source.WorkCurrentMax,
                CurrentOverLimit = source.CurrentOverLimit,
                SleepCurrentThreshold = source.SleepCurrentThreshold,
                SleepTestTimeout = source.SleepTestTimeout,
                HeightCodeMin = source.HeightCodeMin,
                HeightCodeMax = source.HeightCodeMax,
                HeightRangeMin = source.HeightRangeMin,
                HeightRangeMax = source.HeightRangeMax,
                PowerOffDurationMin = source.PowerOffDurationMin,
                PowerOffDurationMax = source.PowerOffDurationMax,
                LumbarTestConfigs = source.LumbarTestConfigs.Select(l => new LumbarTestConfig
                {
                    Order = l.Order,
                    Action = l.Action,
                    TargetHeight = l.TargetHeight,
                    TargetTime = l.TargetTime,
                    Enabled = l.Enabled
                }).ToList(),
                MassageConfigs = source.MassageConfigs.Select(m =>
                {
#pragma warning disable CS0618
                    return new MassageConfig
                    {
                        Point = m.Point,
                        Order = m.Order,
                        HeightSwitchAddress = m.HeightSwitchAddress,
                        Enabled = m.Enabled
                    };
#pragma warning restore CS0618
                }).ToList(),
                MassageTestSettings = new MassageTestSettings
                {
                    TotalDuration = source.MassageTestSettings?.TotalDuration ?? 60000,
                    HighLevelDurationMin = source.MassageTestSettings?.HighLevelDurationMin ?? 500,
                    HighLevelDurationMax = source.MassageTestSettings?.HighLevelDurationMax ?? 5000,
                    PeakCurrentMin = source.MassageTestSettings?.PeakCurrentMin ?? 100,
                    PeakCurrentMax = source.MassageTestSettings?.PeakCurrentMax ?? 2000,
                    AverageCurrentMin = source.MassageTestSettings?.AverageCurrentMin ?? 100,
                    AverageCurrentMax = source.MassageTestSettings?.AverageCurrentMax ?? 1500
                },
                AirLeakTestSettings = CloneAirLeakTestSettings(source.AirLeakTestSettings),
                PressureConfig = ClonePressureChannelConfig(source.PressureConfig),
                MessageKeyTestConfigs = source.MessageKeyTestConfigs?
                    .Select(c => new MessageKeyTestConfig
                    {
                        Order = c.Order,
                        Enabled = c.Enabled,
                        OutputRegisterAddress = c.OutputRegisterAddress ?? string.Empty,
                        ReadByteIndex = c.ReadByteIndex,
                        ExpectedValue = c.ExpectedValue,
                        TriggerMode = c.TriggerMode
                    })
                    .ToList() ?? new List<MessageKeyTestConfig>(),
                StatusMessagePreset = (ushort[])source.StatusMessagePreset.Clone(),
                ManualControl = CloneManualControlConfig(source.ManualControl)
            };
        }

        private static void EnsureChannelConfigDefaults(ChannelConfig channel)
        {
            if (channel == null)
            {
                return;
            }

            channel.MassageTestSettings ??= new MassageTestSettings();
            channel.AirLeakTestSettings ??= new AirLeakTestSettings();
            channel.PressureConfig ??= new PressureChannelConfig();

            if (channel.MassageConfigs == null)
            {
                channel.MassageConfigs = new List<MassageConfig>();
            }

            channel.MessageKeyTestConfigs ??= new List<MessageKeyTestConfig>();
            if (channel.MessageKeyTestConfigs.Count > 8)
            {
                channel.MessageKeyTestConfigs = channel.MessageKeyTestConfigs
                    .OrderBy(c => c.Order)
                    .Take(8)
                    .ToList();
            }

            foreach (var item in channel.MessageKeyTestConfigs)
            {
                if (item.ReadByteIndex <= 0)
                {
                    item.ReadByteIndex = 1;
                }

                item.ExpectedValue = Math.Clamp(item.ExpectedValue, 0, byte.MaxValue);
                item.OutputRegisterAddress ??= string.Empty;
            }

            if (channel.HeightCodeMax <= channel.HeightCodeMin)
            {
                channel.HeightCodeMin = 0;
                channel.HeightCodeMax = 5000;
            }

            if (Math.Abs(channel.HeightRangeMax - channel.HeightRangeMin) < double.Epsilon)
            {
                channel.HeightRangeMin = 0;
                channel.HeightRangeMax = 50;
            }

            foreach (var massage in channel.MassageConfigs)
            {
                if (string.IsNullOrWhiteSpace(massage.HeightSwitchAddress))
                {
                    massage.HeightSwitchAddress = string.Empty;
                }
            }

            channel.ManualControl ??= new ManualControlAddressConfig();
            channel.ManualControl.MassagePointAddresses ??= new List<string>();
        }

        private static void EnsureCurrentSleepConfigDefaults(ProductModel model)
        {
            if (model.CurrentSleepConfig == null)
            {
                model.CurrentSleepConfig = model.Channel1Config != null
                    ? CurrentSleepConfig.FromChannel(model.Channel1Config)
                    : new CurrentSleepConfig();
                return;
            }

            var currentSleepConfig = model.CurrentSleepConfig;
            var defaultConfig = new CurrentSleepConfig();
            if (model.Channel1Config != null
                && currentSleepConfig.Matches(defaultConfig)
                && !currentSleepConfig.MatchesChannel(model.Channel1Config))
            {
                model.CurrentSleepConfig = CurrentSleepConfig.FromChannel(model.Channel1Config);
            }
        }

        private static void ApplyCurrentSleepConfigToChannels(ProductModel model)
        {
            if (model.CurrentSleepConfig == null)
            {
                return;
            }

            if (model.Channel1Config != null)
            {
                model.CurrentSleepConfig.ApplyToChannel(model.Channel1Config);
            }

            if (model.Channel2Config != null)
            {
                model.CurrentSleepConfig.ApplyToChannel(model.Channel2Config);
            }

            if (model.Channel3Config != null)
            {
                model.CurrentSleepConfig.ApplyToChannel(model.Channel3Config);
            }

            if (model.Channel4Config != null)
            {
                model.CurrentSleepConfig.ApplyToChannel(model.Channel4Config);
            }
        }


        private static AirLeakTestSettings CloneAirLeakTestSettings(AirLeakTestSettings? source)
        {
            source ??= new AirLeakTestSettings();
            return new AirLeakTestSettings
            {
                HighInflateDurationMs = source.HighInflateDurationMs,
                HighStabilizeDurationMs = source.HighStabilizeDurationMs,
                HighDetectDurationMs = source.HighDetectDurationMs,
                HighExhaustDurationMs = source.HighExhaustDurationMs,
                HighMaxDropKPa = source.HighMaxDropKPa,
                LowInflateDurationMs = source.LowInflateDurationMs,
                LowStabilizeDurationMs = source.LowStabilizeDurationMs,
                LowDetectDurationMs = source.LowDetectDurationMs,
                LowExhaustDurationMs = source.LowExhaustDurationMs,
                LowMaxDropPa = source.LowMaxDropPa,
                PressureSampleIntervalMs = source.PressureSampleIntervalMs
            };
        }

        private static PressureChannelConfig ClonePressureChannelConfig(PressureChannelConfig? source)
        {
            source ??= new PressureChannelConfig();
            return new PressureChannelConfig
            {
                Enabled = source.Enabled,
                InputRegisterAddress = source.InputRegisterAddress,
                OutputRegisterAddress = source.OutputRegisterAddress,
                ZeroRaw = source.ZeroRaw,
                FullScaleRaw = source.FullScaleRaw,
                PressureZeroKPa = source.PressureZeroKPa,
                PressureFullScaleKPa = source.PressureFullScaleKPa,
                Output4mAPressureKPa = source.Output4mAPressureKPa,
                Output20mAPressureKPa = source.Output20mAPressureKPa
            };
        }
        private static ManualControlAddressConfig CloneManualControlConfig(ManualControlAddressConfig? source)
        {
            if (source == null)
            {
                return new ManualControlAddressConfig();
            }

            return new ManualControlAddressConfig
            {
                StopButtonAddress = source.StopButtonAddress ?? string.Empty,
                FullTestStartAddress = source.FullTestStartAddress ?? string.Empty,
                MassageStartAddress = source.MassageStartAddress ?? string.Empty,
                SideWingStartAddress = source.SideWingStartAddress ?? string.Empty,
                MassagePointAddresses = source.MassagePointAddresses?.ToList() ?? new List<string>(),
                PowerOffAddress = source.PowerOffAddress ?? string.Empty,
                ClampCylinderAddress = source.ClampCylinderAddress ?? string.Empty,
                SpareCylinderAddress = source.SpareCylinderAddress ?? string.Empty,
                DriverSwitchAddress = source.DriverSwitchAddress ?? string.Empty,
                UpInflateDownDeflateAddress = source.UpInflateDownDeflateAddress ?? string.Empty,
                DownInflateUpDeflateAddress = source.DownInflateUpDeflateAddress ?? string.Empty,
                BothInflateAddress = source.BothInflateAddress ?? string.Empty,
                BothDeflateAddress = source.BothDeflateAddress ?? string.Empty,
                MassageKeyAddress = source.MassageKeyAddress ?? string.Empty,
                FullTestLightAddress = source.FullTestLightAddress ?? string.Empty,
                MassageLightAddress = source.MassageLightAddress ?? string.Empty,
                SideWingLightAddress = source.SideWingLightAddress ?? string.Empty,
                TestOkLightAddress = source.TestOkLightAddress ?? string.Empty,
                TestNgLightAddress = source.TestNgLightAddress ?? string.Empty,
                AirLeakStartButtonAddress = source.AirLeakStartButtonAddress ?? string.Empty,
                HighPressureInletValveAddress = source.HighPressureInletValveAddress ?? string.Empty,
                HighPressureExhaustValveAddress = source.HighPressureExhaustValveAddress ?? string.Empty,
                LowPressureInletValveAddress = source.LowPressureInletValveAddress ?? string.Empty,
                LowPressureExhaustValveAddress = source.LowPressureExhaustValveAddress ?? string.Empty
            };
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private static void EnsureAppConfigDefaults(AppConfig config)
        {
            config ??= new AppConfig();

            if (string.IsNullOrWhiteSpace(config.PLCIPAddress))
            {
                config.PLCIPAddress = "192.168.1.188";
            }

            if (config.PLCPort <= 0)
            {
                config.PLCPort = 502;
            }

            if (config.PLCStationId < 1 || config.PLCStationId > 247)
            {
                config.PLCStationId = IPLCService.DefaultUnitId;
            }

            if (config.PlcDiscreteInputCount < 1 || config.PlcDiscreteInputCount > 2000)
            {
                config.PlcDiscreteInputCount = 256;
            }

            if (config.PlcCoilCount < 1 || config.PlcCoilCount > 2000)
            {
                config.PlcCoilCount = 128;
            }

            if (string.IsNullOrWhiteSpace(config.MesServerIp))
            {
                config.MesServerIp = "127.0.0.1";
            }

            if (config.MesServerPort <= 0)
            {
                config.MesServerPort = 8080;
            }

            if (!Enum.IsDefined(typeof(MesIntegrationMode), config.MesIntegrationMode))
            {
                config.MesIntegrationMode = MesIntegrationMode.HttpPush;
            }

            if (!IsValidMesProtocol(config.MesProtocol))
            {
                config.MesProtocol = "TCP";
            }
            else
            {
                config.MesProtocol = config.MesProtocol!.Trim().ToUpperInvariant();
            }

            if (string.IsNullOrWhiteSpace(config.ModbusServerIp))
            {
                config.ModbusServerIp = "0.0.0.0";
            }

            if (config.ModbusServerPort <= 0 || config.ModbusServerPort > 65535)
            {
                config.ModbusServerPort = 502;
            }
            
            if (config.ChannelCount != 2 && config.ChannelCount != 3 && config.ChannelCount != 4)
            {
                config.ChannelCount = 4;
            }

            if (config.PressureModuleStationId < 1 || config.PressureModuleStationId > 247)
            {
                config.PressureModuleStationId = 1;
            }

            if (config.PressureInputStartAddress <= 0)
            {
                config.PressureInputStartAddress = 40097;
            }

            if (config.PressureOutputStartAddress <= 0)
            {
                config.PressureOutputStartAddress = 40023;
            }

            config.SerialDevice1 ??= SerialPortConfig.CreateDefaultDevice1();
            config.SerialDevice2 ??= SerialPortConfig.CreateDefaultDevice2();

            if (string.IsNullOrWhiteSpace(config.SerialDevice1.PortName))
            {
                config.SerialDevice1.PortName = "COM1";
            }

            if (config.SerialDevice1.BaudRate <= 0)
            {
                config.SerialDevice1.BaudRate = 19200;
            }

            if (config.SerialDevice1.DataBits <= 0)
            {
                config.SerialDevice1.DataBits = 8;
            }

            if (string.IsNullOrWhiteSpace(config.SerialDevice1.Parity))
            {
                config.SerialDevice1.Parity = "None";
            }

            if (string.IsNullOrWhiteSpace(config.SerialDevice1.StopBits))
            {
                config.SerialDevice1.StopBits = "One";
            }

            if (string.IsNullOrWhiteSpace(config.SerialDevice2.PortName))
            {
                config.SerialDevice2.PortName = "COM2";
            }

            if (config.SerialDevice2.BaudRate <= 0)
            {
                config.SerialDevice2.BaudRate = 19200;
            }

            if (config.SerialDevice2.DataBits <= 0)
            {
                config.SerialDevice2.DataBits = 8;
            }

            if (string.IsNullOrWhiteSpace(config.SerialDevice2.Parity))
            {
                config.SerialDevice2.Parity = "None";
            }

            if (string.IsNullOrWhiteSpace(config.SerialDevice2.StopBits))
            {
                config.SerialDevice2.StopBits = "One";
            }

            if (config.LastWorkOrder == null)
            {
                config.LastWorkOrder = string.Empty;
            }

            if (config.LastProductModel == null)
            {
                config.LastProductModel = string.Empty;
            }

            if (config.TargetProduction < 0)
            {
                config.TargetProduction = 0;
            }

            if (config.DailyProductionDate == null)
            {
                config.DailyProductionDate = string.Empty;
            }

            if (config.DailyTestCount < 0)
            {
                config.DailyTestCount = 0;
            }

            if (config.DailyPassCount < 0)
            {
                config.DailyPassCount = 0;
            }

            if (config.DailyFailCount < 0)
            {
                config.DailyFailCount = 0;
            }

            config.DailyChannelProductions ??= new List<ChannelDailyProduction>();
        }

        private static bool IsValidMesProtocol(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return string.Equals(value.Trim(), "TCP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Trim(), "UDP", StringComparison.OrdinalIgnoreCase);
        }

    }
}

