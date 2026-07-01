using System;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Services
{
    public class ModbusRtuService : IDisposable
    {
        private readonly ModbusRtuConfig _config;
        private readonly ILogService _logService;
        private SerialPort? _port;
        private readonly object _syncRoot = new();

        public ModbusRtuService(ModbusRtuConfig config, ILogService? logService = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logService = logService ?? LogService.Instance;
        }

        public Task OpenAsync(CancellationToken token)
        {
            lock (_syncRoot)
            {
                if (_port != null && _port.IsOpen)
                {
                    return Task.CompletedTask;
                }

                string portName = (_config.PortName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(portName))
                {
                    throw new InvalidOperationException("Modbus 串口号未配置，请在系统设置中选择有效串口。");
                }

                string[] availablePorts = SerialPort.GetPortNames()
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                string? matchedPort = availablePorts.FirstOrDefault(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
                if (matchedPort == null)
                {
                    string availableList = availablePorts.Length == 0 ? "(无)" : string.Join(", ", availablePorts);
                    throw new InvalidOperationException($"未找到配置的 Modbus 串口 {portName}，当前可用串口: {availableList}");
                }

                _port = new SerialPort(matchedPort, _config.BaudRate, ParseParity(_config.Parity), _config.DataBits, ParseStopBits(_config.StopBits))
                {
                    ReadTimeout = _config.ReadTimeoutMs,
                    WriteTimeout = _config.ReadTimeoutMs
                };

                _port.Open();
            }

            return Task.CompletedTask;
        }

        public void Close()
        {
            lock (_syncRoot)
            {
                if (_port == null)
                {
                    return;
                }

                try
                {
                    _port.Close();
                }
                finally
                {
                    _port.Dispose();
                    _port = null;
                }
            }
        }

        public void Dispose()
        {
            Close();
        }

        public async Task<double> ReadVoltageAsync(CancellationToken token)
        {
            ushort[] registers = await ReadHoldingRegistersAsync(_config.VoltageRegister, 1, token);
            return registers.FirstOrDefault() / 100.0;
        }

        public async Task<double> ReadCurrentAsync(CancellationToken token)
        {
            ushort[] registers = await ReadHoldingRegistersAsync(_config.CurrentRegister, 1, token);
            return registers.FirstOrDefault() / 1000.0;
        }

        public Task SetPowerAsync(bool on, CancellationToken token)
        {
            return WriteSingleRegisterAsync(_config.PowerControlRegister, (ushort)(on ? 1 : 0), token);
        }

        public Task ActivateShortCircuitAsync(CancellationToken token)
        {
            return WriteSingleCoilAsync(_config.ShortCircuitCoil, true, token);
        }

        public Task ActivateOpenCircuitAsync(CancellationToken token)
        {
            return WriteSingleCoilAsync(_config.OpenCircuitCoil, true, token);
        }

        private async Task<ushort[]> ReadHoldingRegistersAsync(int address, int count, CancellationToken token)
        {
            byte[] request = BuildReadRequest(_config.PowerDeviceAddress, 0x03, NormalizeRegister(address), (ushort)count);
            int expectedLength = 5 + count * 2;
            byte[] response = await SendAndReceiveAsync(request, expectedLength, token);

            if (response.Length < expectedLength)
            {
                throw new InvalidOperationException("Modbus 响应长度异常");
            }

            byte byteCount = response[2];
            if (byteCount != count * 2)
            {
                throw new InvalidOperationException("Modbus 响应字节数与请求不符");
            }

            ushort[] data = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                data[i] = (ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]);
            }

            return data;
        }

        private Task WriteSingleRegisterAsync(int address, ushort value, CancellationToken token)
        {
            byte[] request = BuildWriteSingleRegister(_config.PowerDeviceAddress, NormalizeRegister(address), value);
            return SendAndReceiveAsync(request, 8, token);
        }

        private Task WriteSingleCoilAsync(int address, bool state, CancellationToken token)
        {
            byte[] request = BuildWriteSingleCoil(_config.SwitchDeviceAddress, NormalizeCoil(address), state);
            return SendAndReceiveAsync(request, 8, token);
        }

        private async Task<byte[]> SendAndReceiveAsync(byte[] request, int expectedLength, CancellationToken token)
        {
            SerialPort port = EnsurePort();
            byte[] response = new byte[expectedLength];
            token.ThrowIfCancellationRequested();

            await Task.Run(() =>
            {
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
                port.Write(request, 0, request.Length);

                int offset = 0;
                while (offset < expectedLength)
                {
                    token.ThrowIfCancellationRequested();
                    int read = port.Read(response, offset, expectedLength - offset);
                    if (read <= 0)
                    {
                        throw new TimeoutException("读取 Modbus 响应超时");
                    }

                    offset += read;
                }
            }, token);

            if (!ValidateCrc(response))
            {
                throw new InvalidOperationException("Modbus CRC 校验失败");
            }

            return response;
        }

        private SerialPort EnsurePort()
        {
            lock (_syncRoot)
            {
                if (_port == null || !_port.IsOpen)
                {
                    throw new InvalidOperationException("串口未打开");
                }

                return _port;
            }
        }

        private static Parity ParseParity(string parity)
        {
            return string.Equals(parity, "E", StringComparison.OrdinalIgnoreCase) ? Parity.Even
                : string.Equals(parity, "O", StringComparison.OrdinalIgnoreCase) ? Parity.Odd
                : Parity.None;
        }

        private static StopBits ParseStopBits(int stopBits)
        {
            return stopBits switch
            {
                1 => StopBits.One,
                2 => StopBits.Two,
                _ => StopBits.One
            };
        }

        private static ushort NormalizeRegister(int address)
        {
            return (ushort)(address > 40000 ? address - 40001 : Math.Max(0, address));
        }

        private static ushort NormalizeCoil(int address)
        {
            return (ushort)(address > 0 ? address - 1 : 0);
        }

        private static byte[] BuildReadRequest(byte device, byte function, ushort startAddress, ushort count)
        {
            byte[] frame =
            {
                device,
                function,
                (byte)(startAddress >> 8),
                (byte)(startAddress & 0xFF),
                (byte)(count >> 8),
                (byte)(count & 0xFF),
                0,
                0
            };

            AppendCrc(frame);
            return frame;
        }

        private static byte[] BuildWriteSingleRegister(byte device, ushort address, ushort value)
        {
            byte[] frame =
            {
                device,
                0x06,
                (byte)(address >> 8),
                (byte)(address & 0xFF),
                (byte)(value >> 8),
                (byte)(value & 0xFF),
                0,
                0
            };

            AppendCrc(frame);
            return frame;
        }

        private static byte[] BuildWriteSingleCoil(byte device, ushort address, bool state)
        {
            byte[] frame =
            {
                device,
                0x05,
                (byte)(address >> 8),
                (byte)(address & 0xFF),
                state ? (byte)0xFF : (byte)0x00,
                0x00,
                0,
                0
            };

            AppendCrc(frame);
            return frame;
        }

        private static void AppendCrc(Span<byte> frame)
        {
            ushort crc = CalculateCrc(frame[..^2]);
            frame[^2] = (byte)(crc & 0xFF);
            frame[^1] = (byte)(crc >> 8);
        }

        private static bool ValidateCrc(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 3)
            {
                return false;
            }

            ushort expected = (ushort)(frame[^1] << 8 | frame[^2]);
            ushort actual = CalculateCrc(frame[..^2]);
            return expected == actual;
        }

        private static ushort CalculateCrc(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb)
                    {
                        crc ^= 0xA001;
                    }
                }
            }

            return crc;
        }
    }
}
