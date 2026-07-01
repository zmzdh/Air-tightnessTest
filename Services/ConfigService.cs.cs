// Services/ConfigService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private readonly string _modelsPath;
        private readonly ILogService _logService;

        public ConfigService(ILogService? logService = null)
        {
            _logService = logService ?? LogService.Instance;
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            _modelsPath = Path.Combine(_configPath, "Models");

            if (!Directory.Exists(_configPath))
                Directory.CreateDirectory(_configPath);
            if (!Directory.Exists(_modelsPath))
                Directory.CreateDirectory(_modelsPath);
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
                        models.Add(model);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("加载产品型号失败", ex);
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
                _logService.LogError("保存产品型号失败", ex);
                return false;
            }
        }

        public async Task<bool> SaveProductModelsAsync(IEnumerable<ProductModel> models)
        {
            if (models == null)
            {
                throw new ArgumentNullException(nameof(models));
            }

            var modelList = models.ToList();

            try
            {
                // Save or update current models
                foreach (var model in modelList)
                {
                    EnsureChannelConfigDefaults(model.Channel1Config);
                    EnsureChannelConfigDefaults(model.Channel2Config);
                    await SaveProductModelAsync(model);
                }

                // Remove deleted models from disk
                var existingFiles = Directory.GetFiles(_modelsPath, "*.json");
                var currentFileNames = new HashSet<string>(modelList.Select(m => Path.Combine(_modelsPath, $"{m.ModelName}.json")));

                foreach (var file in existingFiles)
                {
                    if (!currentFileNames.Contains(file))
                    {
                        File.Delete(file);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("保存产品型号列表失败", ex);
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
                _logService.LogError("加载应用配置失败", ex);
            }

            var defaultConfig = new AppConfig
            {
                PLCIPAddress = "192.168.1.188",
                PLCPort = 502,
                AutoSave = true,
                PLCStationId = IPLCService.DefaultUnitId,
                MesServerIp = "127.0.0.1",
                MesServerPort = 8080,
                MesProtocol = "TCP",
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
                _logService.LogError("保存应用配置失败", ex);
                return false;
            }
        }

        private ProductModel CreateDefaultModel()
        {
            var model = new ProductModel
            {
                ModelName = "默认型号",
                Description = "默认腰托按摩产品",
                ImagePath = string.Empty,
                ProcessConfig = new TestProcessConfig
                {
                    EnableBarcodeCheck = true,
                    MaxTestCount = 3,
                    EnableCurrentMonitoring = true,
                    RecordCurrentBeforeStart = false,
                    CheckSameModel = true,
                    PromptOnDuplicateBarcode = true,
                    EnableLumbarTest = true,
                    EnableMassageTest = true,
                    ModeSwitchPowerOffDuration = 5000
                }
            };

            model.Channel1Config = BuildDefaultChannelConfig("左通道");
            model.Channel2Config = DeepCopyChannelConfig(model.Channel1Config, "右通道");

            return model;
        }

        private ChannelConfig BuildDefaultChannelConfig(string name)
        {
            return new ChannelConfig
            {
                ChannelName = name,
                StaticCurrentMin = 20,
                StaticCurrentMax = 80,
                WorkCurrentMin = 200,
                WorkCurrentMax = 1500,
                CurrentOverLimit = 2000,
                SleepCurrentThreshold = 0.5,
                SleepTestTimeout = 5000,
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
                        PressureSwitchAddress = "M10",
                    },
                    new MassageConfig
                    {
                        Point = 2,
                        PressureSwitchAddress = "M11",
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
                }
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
                        PressureSwitchAddress = m.PressureSwitchAddress,
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
                StatusMessagePreset = (ushort[])source.StatusMessagePreset.Clone()
            };
        }

        private static void EnsureChannelConfigDefaults(ChannelConfig channel)
        {
            if (channel == null)
            {
                return;
            }

            channel.MassageTestSettings ??= new MassageTestSettings();

            if (channel.MassageConfigs == null)
            {
                channel.MassageConfigs = new List<MassageConfig>();
            }

            foreach (var massage in channel.MassageConfigs)
            {
                if (string.IsNullOrWhiteSpace(massage.PressureSwitchAddress))
                {
                    massage.PressureSwitchAddress = string.Empty;
                }
            }
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

            if (config.LastWorkOrder == null)
            {
                config.LastWorkOrder = string.Empty;
            }

            if (config.LastProductModel == null)
            {
                config.LastProductModel = string.Empty;
            }

            config.Swd ??= new SwdConfig();
            if (config.Swd.EraseTimeoutMs <= 0)
            {
                config.Swd.EraseTimeoutMs = 5000;
            }

            if (config.Swd.ProgramTimeoutMs <= 0)
            {
                config.Swd.ProgramTimeoutMs = 20000;
            }

            if (string.IsNullOrWhiteSpace(config.Swd.FirmwareDirectory))
            {
                config.Swd.FirmwareDirectory = "Firmware";
            }

            config.Can ??= new CanTestConfig();
            if (config.Can.BaudRate <= 0)
            {
                config.Can.BaudRate = 500000;
            }

            if (config.Can.ListenTimeoutMs <= 0)
            {
                config.Can.ListenTimeoutMs = 5000;
            }

            if (config.Can.ListenRetryCount < 0)
            {
                config.Can.ListenRetryCount = 1;
            }

            if (config.Can.DefaultFrequencyHz <= 0)
            {
                config.Can.DefaultFrequencyHz = 80;
            }

            if (config.Can.DefaultStrengthLow <= 0)
            {
                config.Can.DefaultStrengthLow = 100;
            }

            if (config.Can.DefaultStrengthMid <= 0)
            {
                config.Can.DefaultStrengthMid = 150;
            }

            if (config.Can.DefaultStrengthHigh <= 0)
            {
                config.Can.DefaultStrengthHigh = 250;
            }

            if (config.Can.CurrentToleranceMa <= 0)
            {
                config.Can.CurrentToleranceMa = 50;
            }

            if (config.Can.VoltageMin <= 0 || config.Can.VoltageMax <= 0 || config.Can.VoltageMax < config.Can.VoltageMin)
            {
                config.Can.VoltageMin = 13.0;
                config.Can.VoltageMax = 14.0;
            }

            config.Modbus ??= new ModbusRtuConfig();
            if (config.Modbus.BaudRate <= 0)
            {
                config.Modbus.BaudRate = 9600;
            }

            if (config.Modbus.DataBits <= 0)
            {
                config.Modbus.DataBits = 8;
            }

            if (string.IsNullOrWhiteSpace(config.Modbus.Parity))
            {
                config.Modbus.Parity = "N";
            }

            if (config.Modbus.StopBits <= 0)
            {
                config.Modbus.StopBits = 1;
            }

            byte legacyAddress = config.Modbus.DeviceAddress == 0 ? (byte)1 : config.Modbus.DeviceAddress;
            if (config.Modbus.PowerDeviceAddress == 0)
            {
                config.Modbus.PowerDeviceAddress = legacyAddress;
            }

            if (config.Modbus.SwitchDeviceAddress == 0)
            {
                config.Modbus.SwitchDeviceAddress = legacyAddress;
            }

            if (config.Modbus.ReadTimeoutMs <= 0)
            {
                config.Modbus.ReadTimeoutMs = 2000;
            }

            if (string.IsNullOrWhiteSpace(config.Modbus.PortName))
            {
                config.Modbus.PortName = "COM1";
            }

            if (config.Modbus.VoltageRegister <= 0)
            {
                config.Modbus.VoltageRegister = 40002;
            }

            if (config.Modbus.CurrentRegister <= 0)
            {
                config.Modbus.CurrentRegister = 40001;
            }

            if (config.Modbus.PowerControlRegister <= 0)
            {
                config.Modbus.PowerControlRegister = 40018;
            }

            if (config.Modbus.ShortCircuitCoil <= 0)
            {
                config.Modbus.ShortCircuitCoil = 1;
            }

            if (config.Modbus.OpenCircuitCoil <= 0)
            {
                config.Modbus.OpenCircuitCoil = 2;
            }
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
