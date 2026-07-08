using System;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public sealed class PressureModbusService : IDisposable
    {
        private const int MinRaw = 4000;
        private const int MaxRaw = 20000;
        private const int DefaultInputRegisterAddress = 40097;
        private const int DefaultOutputRegisterAddress = 40023;
        private readonly ModbusRtuClient _client;
        private readonly ILogService _logService;
        private byte _stationId = 1;
        private int _inputStartAddress = DefaultInputRegisterAddress;
        private int _outputStartAddress = DefaultOutputRegisterAddress;
        private double _inputFullScaleKPa = 100;
        private double _outputFullScaleKPa = 100;
        private readonly int[] _channelZeroRawOverrides = { -1, -1, -1, -1 };

        public PressureModbusService(SerialPortConfig config, ILogService? logService = null)
        {
            _logService = logService ?? LogService.Instance;
            _client = new ModbusRtuClient(config ?? SerialPortConfig.CreateDefaultDevice2(), _logService);
        }

        public void UpdateConfig(SystemConfig config)
        {
            if (config == null)
            {
                return;
            }

            _stationId = config.PressureModuleStationId is >= 1 and <= 247
                ? config.PressureModuleStationId
                : (byte)1;
            _inputStartAddress = config.PressureInputStartAddress > 0 ? config.PressureInputStartAddress : DefaultInputRegisterAddress;
            _outputStartAddress = config.PressureOutputStartAddress > 0 ? config.PressureOutputStartAddress : DefaultOutputRegisterAddress;
            _inputFullScaleKPa = NormalizeRange(config.PressureInputFullScaleKPa, 50, 200, 100);
            _outputFullScaleKPa = NormalizeRange(config.PressureOutputFullScaleKPa, 100, 200, 100);
            _channelZeroRawOverrides[0] = NormalizeZeroRaw(config.PressureZeroRaw1);
            _channelZeroRawOverrides[1] = NormalizeZeroRaw(config.PressureZeroRaw2);
            _channelZeroRawOverrides[2] = NormalizeZeroRaw(config.PressureZeroRaw3);
            _channelZeroRawOverrides[3] = NormalizeZeroRaw(config.PressureZeroRaw4);
            _client.UpdateConfig(config.SerialDevice2 ?? SerialPortConfig.CreateDefaultDevice2());
        }

        public void UpdateSerialConfig(SerialPortConfig config)
        {
            _client.UpdateConfig(config ?? SerialPortConfig.CreateDefaultDevice2());
        }

        public async Task<double> ReadPressureKPaAsync(int channel, PressureChannelConfig? channelConfig, CancellationToken cancellationToken)
        {
            ValidateChannel(channel);
            channelConfig ??= BuildDefaultChannelConfig(channel);
            int raw = await ReadRawValueAsync(channel, channelConfig, cancellationToken).ConfigureAwait(false);
            return ConvertRawToPressure(raw, channelConfig, _inputFullScaleKPa, _channelZeroRawOverrides[channel - 1]);
        }

        public async Task<int> ReadRawValueAsync(int channel, PressureChannelConfig? channelConfig, CancellationToken cancellationToken)
        {
            ValidateChannel(channel);
            channelConfig ??= BuildDefaultChannelConfig(channel);
            ushort address = ToZeroBasedRegisterAddress(ResolveInputRegisterAddress(channel, channelConfig));
            ushort[] registers = await _client.ReadHoldingRegistersAsync(_stationId, address, 1, cancellationToken).ConfigureAwait(false);
            return registers.Length > 0 ? registers[0] : 0;
        }

        public async Task WriteOutputPressureAsync(int channel, double pressureKPa, PressureChannelConfig? channelConfig, CancellationToken cancellationToken)
        {
            ValidateChannel(channel);
            channelConfig ??= BuildDefaultChannelConfig(channel);
            ushort address = ToZeroBasedRegisterAddress(ResolveOutputRegisterAddress(channel, channelConfig));

            ushort raw = ConvertPressureToOutputRaw(pressureKPa, channelConfig, _outputFullScaleKPa);
            await _client.WriteSingleRegisterAsync(_stationId, address, raw, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteOutputPressureAsync(double pressureKPa, CancellationToken cancellationToken)
        {
            ushort raw = ConvertPressureToOutputRaw(pressureKPa, BuildDefaultChannelConfig(1), _outputFullScaleKPa);
            ushort address = ToZeroBasedRegisterAddress(_outputStartAddress);
            await _client.WriteSingleRegisterAsync(_stationId, address, raw, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private static PressureChannelConfig BuildDefaultChannelConfig(int channel)
        {
            return new PressureChannelConfig
            {
                InputRegisterAddress = DefaultInputRegisterAddress + channel - 1,
                OutputRegisterAddress = DefaultOutputRegisterAddress + channel - 1,
                ZeroRaw = MinRaw,
                FullScaleRaw = MaxRaw
            };
        }

        private int ResolveInputRegisterAddress(int channel, PressureChannelConfig config)
        {
            return config.InputRegisterAddress > 0
                && config.InputRegisterAddress != DefaultInputRegisterAddress
                ? config.InputRegisterAddress
                : _inputStartAddress + channel - 1;
        }

        private int ResolveOutputRegisterAddress(int channel, PressureChannelConfig config)
        {
            return config.OutputRegisterAddress > 0
                && config.OutputRegisterAddress != DefaultOutputRegisterAddress
                ? config.OutputRegisterAddress
                : _outputStartAddress + channel - 1;
        }

        private static double ConvertRawToPressure(int raw, PressureChannelConfig config, double defaultFullScaleKPa, int zeroRawOverride = -1)
        {
            int zeroRaw = zeroRawOverride >= 0 ? zeroRawOverride : NormalizeConfigZeroRaw(config.ZeroRaw);
            int fullScaleRaw = config.FullScaleRaw > zeroRaw ? config.FullScaleRaw : MaxRaw;
            double zeroPressure = config.PressureZeroKPa;
            double fullScalePressure = config.PressureFullScaleKPa > zeroPressure
                ? config.PressureFullScaleKPa
                : defaultFullScaleKPa;

            raw = Math.Clamp(raw, MinRaw, MaxRaw);
            double ratio = (raw - zeroRaw) / (double)(fullScaleRaw - zeroRaw);
            ratio = Math.Clamp(ratio, 0, 1);
            return zeroPressure + ratio * (fullScalePressure - zeroPressure);
        }

        private static int NormalizeZeroRaw(int value)
        {
            return value >= 3800 && value <= 4100 ? value : -1;
        }

        private static int NormalizeConfigZeroRaw(int value)
        {
            return value >= 3800 && value <= 4100 ? value : MinRaw;
        }

        private static ushort ConvertPressureToOutputRaw(double pressureKPa, PressureChannelConfig config, double defaultFullScaleKPa)
        {
            double min = config.Output4mAPressureKPa;
            double max = config.Output20mAPressureKPa > min
                ? config.Output20mAPressureKPa
                : defaultFullScaleKPa;
            double ratio = (pressureKPa - min) / (max - min);
            ratio = Math.Clamp(ratio, 0, 1);
            return (ushort)Math.Round(MinRaw + ratio * (MaxRaw - MinRaw));
        }

        private static double NormalizeRange(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
            {
                return fallback;
            }

            return value;
        }

        private static ushort ToZeroBasedRegisterAddress(int modbusAddress)
        {
            if (modbusAddress >= 40001 && modbusAddress <= 49999)
            {
                return (ushort)(modbusAddress - 40001);
            }

            if (modbusAddress < 0 || modbusAddress > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(modbusAddress), "Modbus register address is out of range.");
            }

            return (ushort)modbusAddress;
        }

        private static void ValidateChannel(int channel)
        {
            if (channel < 1 || channel > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), "Only channels 1-4 are supported.");
            }
        }
    }
}
