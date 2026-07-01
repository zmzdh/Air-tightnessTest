using System;
using System.IO.Ports;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public sealed class ModbusRtuClient : IDisposable
    {
        private readonly ILogService _logService;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly object _syncRoot = new();
        private SerialPortConfig _config;
        private SerialPort? _serialPort;
        private bool _disposed;

        public ModbusRtuClient(SerialPortConfig config, ILogService? logService = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logService = logService ?? LogService.Instance;
        }

        public void UpdateConfig(SerialPortConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_syncRoot)
            {
                _config = config;
                ReopenPort();
            }
        }

        public async Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return Array.Empty<ushort>();
            }

            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                SerialPort port = EnsurePort();
                port.DiscardInBuffer();

                byte[] request = BuildReadRequest(unitId, startAddress, count);
                await port.BaseStream.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);
                await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                byte[] header = await ReadExactAsync(port, 3, cancellationToken).ConfigureAwait(false);
                byte functionCode = header[1];
                if ((functionCode & 0x80) != 0)
                {
                    byte[] tail = await ReadExactAsync(port, 2, cancellationToken).ConfigureAwait(false);
                    byte[] response = CombineArrays(header, tail);
                    ValidateResponse(unitId, response, 0x03, allowException: true);
                    throw new InvalidOperationException("Modbus RTU 异常响应");
                }

                int byteCount = header[2];
                byte[] payloadAndCrc = await ReadExactAsync(port, byteCount + 2, cancellationToken).ConfigureAwait(false);
                byte[] fullResponse = CombineArrays(header, payloadAndCrc);

                ValidateResponse(unitId, fullResponse, 0x03, allowException: false);

                if (byteCount != count * 2)
                {
                    throw new InvalidOperationException($"Modbus RTU 鍝嶅簲瀛楄妭鏁板紓甯? {byteCount}");
                }

                var registers = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    int index = 3 + i * 2;
                    registers[i] = (ushort)((fullResponse[index] << 8) | fullResponse[index + 1]);
                }

                if (stopwatch.ElapsedMilliseconds > 300)
                {
                    _logService.LogWarning(
                        $"Modbus RTU 读取耗时 {stopwatch.ElapsedMilliseconds}ms (站号{unitId}, 地址{startAddress}, 数量{count})");
                }
                return registers;
            }
            catch (Exception ex)
            {
                _logService.LogError(
                    $"Modbus RTU 读取保持寄存器失败(站号{unitId}, 地址{startAddress}, 数量{count}, 耗时{stopwatch.ElapsedMilliseconds}ms)",
                    ex);
                throw;
            }
            finally
            {
                _ioLock.Release();
            }
        }


        public async Task WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                SerialPort port = EnsurePort();
                port.DiscardInBuffer();

                byte[] request = BuildWriteSingleRegisterRequest(unitId, address, value);
                await port.BaseStream.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);
                await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                byte[] response = await ReadExactAsync(port, 8, cancellationToken).ConfigureAwait(false);
                ValidateResponse(unitId, response, 0x06, allowException: false);

                if (response[2] != request[2] || response[3] != request[3] ||
                    response[4] != request[4] || response[5] != request[5])
                {
                    throw new InvalidOperationException("Modbus RTU write echo mismatch.");
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(
                    $"Modbus RTU 写入保持寄存器失败(站号{unitId}, 地址{address}, 值{value}, 耗时{stopwatch.ElapsedMilliseconds}ms)",
                    ex);
                throw;
            }
            finally
            {
                _ioLock.Release();
            }
        }
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _serialPort?.Dispose();
                _serialPort = null;
            }
        }

        private SerialPort EnsurePort()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(ModbusRtuClient));
                }

                if (_serialPort == null)
                {
                    _serialPort = CreatePort(_config);
                    _serialPort.Open();
                }
                else if (!_serialPort.IsOpen)
                {
                    _serialPort.Open();
                }

                return _serialPort;
            }
        }

        private void ReopenPort()
        {
            if (_serialPort != null)
            {
                try
                {
                    _serialPort.Dispose();
                }
                catch (Exception ex)
                {
                    _logService.LogWarning("关闭Modbus RTU串口失败", ex);
                }
                finally
                {
                    _serialPort = null;
                }
            }

            try
            {
                _serialPort = CreatePort(_config);
                _serialPort.Open();
            }
            catch (Exception ex)
            {
                _logService.LogError("打开Modbus RTU串口失败", ex);
            }
        }

        private static SerialPort CreatePort(SerialPortConfig config)
        {
            return new SerialPort
            {
                PortName = config.PortName,
                BaudRate = config.BaudRate,
                DataBits = config.DataBits,
                Parity = ParseParity(config.Parity),
                StopBits = ParseStopBits(config.StopBits),
                ReadTimeout = 500,
                WriteTimeout = 500
            };
        }

        private static Parity ParseParity(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "even" => Parity.Even,
                "odd" => Parity.Odd,
                "mark" => Parity.Mark,
                "space" => Parity.Space,
                _ => Parity.None
            };
        }

        private static StopBits ParseStopBits(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "onepointfive" => StopBits.OnePointFive,
                "two" => StopBits.Two,
                _ => StopBits.One
            };
        }

        private static byte[] BuildReadRequest(byte unitId, ushort startAddress, ushort count)
        {
            var frame = new byte[8];
            frame[0] = unitId;
            frame[1] = 0x03;
            frame[2] = (byte)(startAddress >> 8);
            frame[3] = (byte)(startAddress & 0xFF);
            frame[4] = (byte)(count >> 8);
            frame[5] = (byte)(count & 0xFF);

            ushort crc = ComputeCrc(frame, 0, 6);
            frame[6] = (byte)(crc & 0xFF);
            frame[7] = (byte)((crc >> 8) & 0xFF);
            return frame;
        }


        private static byte[] BuildWriteSingleRegisterRequest(byte unitId, ushort address, ushort value)
        {
            var frame = new byte[8];
            frame[0] = unitId;
            frame[1] = 0x06;
            frame[2] = (byte)(address >> 8);
            frame[3] = (byte)(address & 0xFF);
            frame[4] = (byte)(value >> 8);
            frame[5] = (byte)(value & 0xFF);

            ushort crc = ComputeCrc(frame, 0, 6);
            frame[6] = (byte)(crc & 0xFF);
            frame[7] = (byte)((crc >> 8) & 0xFF);
            return frame;
        }
        private static async Task<byte[]> ReadExactAsync(SerialPort port, int length, CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read;
                try
                {
                    read = await Task.Run(() => port.Read(buffer, offset, length - offset), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException("Modbus RTU 响应读取超时", ex);
                }

                if (read <= 0)
                {
                    throw new InvalidOperationException("Modbus RTU 响应读取失败");
                }

                offset += read;
            }

            return buffer;
        }

        private static void ValidateResponse(byte unitId, byte[] response, byte expectedFunctionCode, bool allowException)
        {
            if (response.Length < 5)
            {
                throw new InvalidOperationException("Modbus RTU 响应长度异常");
            }

            ushort crc = ComputeCrc(response, 0, response.Length - 2);
            ushort receivedCrc = (ushort)(response[^2] | (response[^1] << 8));
            if (crc != receivedCrc)
            {
                throw new InvalidOperationException("Modbus RTU CRC 校验失败");
            }

            if (response[0] != unitId)
            {
                throw new InvalidOperationException("Modbus RTU 站号不匹配");
            }

            if (response[1] == (expectedFunctionCode | 0x80))
            {
                if (allowException)
                {
                    byte exceptionCode = response[2];
                    throw new InvalidOperationException($"Modbus RTU 异常响应(功能码0x{response[1]:X2}, 异常码0x{exceptionCode:X2})");
                }

                throw new InvalidOperationException("Modbus RTU 异常响应");
            }

            if (response[1] != expectedFunctionCode)
            {
                throw new InvalidOperationException("Modbus RTU 功能码不匹配");
            }
        }

        private static byte[] CombineArrays(byte[] first, byte[] second)
        {
            var combined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

        private static ushort ComputeCrc(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[offset + i];
                for (int bit = 0; bit < 8; bit++)
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
