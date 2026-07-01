using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LumbarMassageTest.Services
{
    public interface IPLCService
    {
        const byte DefaultUnitId = 0x01;

        Task<bool> ConnectAsync(string ipAddress, int port, byte unitId = DefaultUnitId);
        Task DisconnectAsync();
        bool IsConnected { get; }

        Task<bool[]> ReadBitsAsync(string startAddress, ushort count);
        Task<short[]> ReadWordsAsync(string startAddress, ushort count);
        Task WriteBitAsync(string address, bool value);
        Task WriteWordAsync(string address, ushort value);
        Task WriteWordsAsync(string startAddress, byte[] data);

        event EventHandler<string>? OnError;
        event EventHandler<bool>? OnConnectionChanged;
    }

    public class PLCService : IPLCService, IDisposable
    {
        private static PLCService? _instance;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _isConnected;
        private int _reconnectCount;
        private const int MAX_RECONNECT_COUNT = 5;
        private string _ipAddress = string.Empty;
        private int _port;
        private byte _unitId = IPLCService.DefaultUnitId;
        private CancellationTokenSource _reconnectTokenSource;
        private Task? _reconnectTask;
        private readonly object _stateLock = new();
        private bool _disposed;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private ushort _transactionId;

        private static readonly IReadOnlyList<DeviceAddressMapping> AddressMappings = new List<DeviceAddressMapping>
        {
            // 位设备（线圈类）
            new DeviceAddressMapping("Y", ModbusArea.Coil, 0),           
            new DeviceAddressMapping("SM", ModbusArea.Coil, 5000),
            new DeviceAddressMapping("T", ModbusArea.Coil, 6000),
            new DeviceAddressMapping("C", ModbusArea.Coil, 7000),
            new DeviceAddressMapping("M", ModbusArea.Coil, 10000),
            new DeviceAddressMapping("S", ModbusArea.Coil, 30000),

            // 输入点（离散量）
            new DeviceAddressMapping("X", ModbusArea.DiscreteInput, 0),

            // 字设备（寄存器）
            new DeviceAddressMapping("TN", ModbusArea.HoldingRegister, 0),
            new DeviceAddressMapping("CN", ModbusArea.HoldingRegister, 1000),
            new DeviceAddressMapping("SD", ModbusArea.InputRegister, 1300),
            new DeviceAddressMapping("D", ModbusArea.HoldingRegister, 2000),
            new DeviceAddressMapping("R", ModbusArea.HoldingRegister, 20000),
        };

        public event EventHandler<string>? OnError;
        public event EventHandler<bool>? OnConnectionChanged;

        public static PLCService Instance => _instance ??= new PLCService();

        public static PLCService? Current => _instance;

        private PLCService()
        {
            _reconnectTokenSource = new CancellationTokenSource();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PLCService));
            }
        }

        public bool IsConnected => _isConnected;

        public async Task<bool> ConnectAsync(string ipAddress, int port, byte unitId = IPLCService.DefaultUnitId)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                throw new ArgumentException("IP地址不能为空", nameof(ipAddress));
            }

            if (port <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "端口号必须大于0");
            }

            if (unitId < 1 || unitId > 247)
            {
                throw new ArgumentOutOfRangeException(nameof(unitId), "站号必须在1-247之间");
            }

            _ipAddress = ipAddress.Trim();
            _port = port;
            _unitId = unitId;
            return await Task.Run(InternalConnect).ConfigureAwait(false);
        }

        private bool InternalConnect()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_ipAddress))
                {
                    throw new InvalidOperationException("未配置PLC IP地址，无法建立连接");
                }

                DisconnectInternal();

                _tcpClient = new TcpClient();
                var connectTask = _tcpClient.ConnectAsync(_ipAddress, _port);
                if (!connectTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    throw new TimeoutException("连接PLC超时");
                }

                _stream = _tcpClient.GetStream();
                _stream.ReadTimeout = 3000;
                _stream.WriteTimeout = 3000;

                _isConnected = true;
                _reconnectCount = 0;
                OnConnectionChanged?.Invoke(this, _isConnected);
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"连接PLC失败: {ex.Message}");
                _isConnected = false;
                OnConnectionChanged?.Invoke(this, _isConnected);
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _reconnectTokenSource.Cancel();
                await Task.Run(DisconnectInternal).ConfigureAwait(false);
                _isConnected = false;
                OnConnectionChanged?.Invoke(this, _isConnected);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"断开PLC连接失败: {ex.Message}");
            }
        }

        private Task StartAutoReconnect()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return Task.CompletedTask;
                }

                if (_reconnectTask != null && !_reconnectTask.IsCompleted)
                {
                    return _reconnectTask;
                }

                _reconnectTokenSource.Cancel();
                _reconnectTokenSource.Dispose();
                _reconnectTokenSource = new CancellationTokenSource();
                var token = _reconnectTokenSource.Token;

                Task? reconnectTask = null;
                reconnectTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!_isConnected && _reconnectCount < MAX_RECONNECT_COUNT && !token.IsCancellationRequested)
                        {
                            _reconnectCount++;
                            if (InternalConnect())
                            {
                                break;
                            }

                            try
                            {
                                await Task.Delay(3000, token).ConfigureAwait(false);
                            }
                            catch (TaskCanceledException)
                            {
                                break;
                            }
                        }

                        if (!_isConnected && _reconnectCount >= MAX_RECONNECT_COUNT)
                        {
                            OnError?.Invoke(this, "PLC重连失败，超过最大重试次数");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore cancellation
                    }
                    finally
                    {
                        lock (_stateLock)
                        {
                            if (ReferenceEquals(_reconnectTask, reconnectTask))
                            {
                                _reconnectTask = null;
                            }
                        }
                    }
                }, token);

                _reconnectTask = reconnectTask;
                return reconnectTask;
            }
        }

        public async Task<bool[]> ReadBitsAsync(string startAddress, ushort count)
        {
            ThrowIfDisposed();

            if (count == 0)
            {
                return Array.Empty<bool>();
            }

            if (!_isConnected)
            {
                OnError?.Invoke(this, "PLC未连接，无法异步读取位地址");
                return new bool[count];
            }

            try
            {
                int start = ResolveAddress(startAddress, out var area);
                if (area != ModbusArea.Coil && area != ModbusArea.DiscreteInput)
                {
                    throw new InvalidOperationException($"地址{startAddress}不属于位设备范围");
                }

                const int maxBitsPerRead = 2000;
                var result = new bool[count];
                int offset = 0;
                while (offset < count)
                {
                    ushort chunkSize = (ushort)Math.Min(maxBitsPerRead, count - offset);
                    var values = await ReadBitsChunkAsync(area, (ushort)(start + offset), chunkSize).ConfigureAwait(false);
                    Array.Copy(values, 0, result, offset, chunkSize);
                    offset += chunkSize;
                }

                return result;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"异步读取位地址{startAddress}异常: {ex.Message}");
                CheckConnectionAndReconnect();
                return new bool[count];
            }
        }

        public async Task<short[]> ReadWordsAsync(string startAddress, ushort count)
        {
            ThrowIfDisposed();

            if (count == 0)
            {
                return Array.Empty<short>();
            }

            if (!_isConnected)
            {
                OnError?.Invoke(this, "PLC未连接，无法异步读取字地址");
                return new short[count];
            }

            try
            {
                int start = ResolveAddress(startAddress, out var area);
                if (area != ModbusArea.HoldingRegister && area != ModbusArea.InputRegister)
                {
                    throw new InvalidOperationException($"地址{startAddress}不属于字设备范围");
                }

                const int maxWordsPerRead = 120;
                var buffer = new short[count];
                int offset = 0;
                while (offset < count)
                {
                    ushort chunkSize = (ushort)Math.Min(maxWordsPerRead, count - offset);
                    var registers = await ReadRegisterChunkAsync(area, (ushort)(start + offset), chunkSize).ConfigureAwait(false);
                    for (int i = 0; i < chunkSize; i++)
                    {
                        buffer[offset + i] = unchecked((short)registers[i]);
                    }

                    offset += chunkSize;
                }

                return buffer;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"异步读取字地址{startAddress}异常: {ex.Message}");
                CheckConnectionAndReconnect();
                return new short[count];
            }
        }

        public async Task WriteBitAsync(string address, bool value)
        {
            ThrowIfDisposed();

            if (!_isConnected)
            {
                OnError?.Invoke(this, "PLC未连接，无法写入位地址");
                return;
            }

            try
            {
                int start = ResolveAddress(address, out var area);
                if (area != ModbusArea.Coil)
                {
                    throw new InvalidOperationException($"地址{address}不支持线圈写入");
                }

                await WriteSingleCoilAsync((ushort)start, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"写入位地址{address}异常: {ex.Message}");
                CheckConnectionAndReconnect();
            }
        }

        public async Task WriteWordAsync(string address, ushort value)
        {
            ThrowIfDisposed();

            if (!_isConnected)
            {
                OnError?.Invoke(this, "PLC未连接，无法写入字地址");
                return;
            }

            try
            {
                int start = ResolveAddress(address, out var area);
                if (area != ModbusArea.HoldingRegister)
                {
                    throw new InvalidOperationException($"地址{address}不支持写入保持寄存器");
                }

                await WriteSingleRegisterAsync((ushort)start, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"写入字地址{address}异常: {ex.Message}");
                CheckConnectionAndReconnect();
            }
        }

        public async Task WriteWordsAsync(string startAddress, byte[] data)
        {
            ThrowIfDisposed();

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length % 2 != 0)
            {
                throw new ArgumentException("写入数据长度必须为偶数", nameof(data));
            }

            if (!_isConnected)
            {
                OnError?.Invoke(this, "PLC未连接，无法异步写入字地址");
                return;
            }

            try
            {
                int start = ResolveAddress(startAddress, out var area);
                if (area != ModbusArea.HoldingRegister)
                {
                    throw new InvalidOperationException($"地址{startAddress}不支持写入保持寄存器");
                }

                int registerCount = data.Length / 2;
                var registers = new ushort[registerCount];
                for (int i = 0; i < registerCount; i++)
                {
                    registers[i] = (ushort)(data[i * 2] | (data[i * 2 + 1] << 8));
                }

                await WriteMultipleRegistersAsync((ushort)start, registers).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"异步写入字地址{startAddress}异常: {ex.Message}");
                CheckConnectionAndReconnect();
            }
        }

        private int ResolveAddress(string address, out ModbusArea area)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("PLC地址不能为空", nameof(address));
            }

            if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                address.StartsWith("1x", StringComparison.OrdinalIgnoreCase))
            {
                string hexPrefix = address.Substring(0, 2);
                string hexNumericPart = address.Substring(2);
                if (string.IsNullOrWhiteSpace(hexNumericPart))
                {
                    throw new FormatException($"无效的PLC地址: {address}");
                }

                if (!hexNumericPart.All(Uri.IsHexDigit))
                {
                    throw new FormatException($"PLC地址格式错误: {address}");
                }

                if (!int.TryParse(hexNumericPart, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int addressIndex))
                {
                    throw new FormatException($"PLC地址格式错误: {address}");
                }

                if (addressIndex < 0)
                {
                    throw new FormatException($"PLC地址必须从0开始: {address}");
                }

                area = hexPrefix.Equals("0x", StringComparison.OrdinalIgnoreCase)
                    ? ModbusArea.Coil
                    : ModbusArea.DiscreteInput;

                return addressIndex;
            }

            string prefix = new string(address.TakeWhile(c => !char.IsDigit(c) && c != '-').ToArray());
            string numericPart = new string(address.Skip(prefix.Length).ToArray());

            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(numericPart))
            {
                throw new FormatException($"无效的PLC地址: {address}");
            }

            if (!int.TryParse(numericPart, out int index))
            {
                throw new FormatException($"PLC地址格式错误: {address}");
            }

            var mapping = AddressMappings
                .OrderByDescending(m => m.Prefix.Length)
                .FirstOrDefault(m => prefix.Equals(m.Prefix, StringComparison.OrdinalIgnoreCase));

            if (mapping == null)
            {
                throw new NotSupportedException($"不支持的PLC前缀: {prefix}");
            }

            area = mapping.Area;
            return mapping.BaseAddress + index;
        }

        private async Task<bool[]> ReadBitsChunkAsync(ModbusArea area, ushort startAddress, ushort count)
        {
            byte functionCode = area == ModbusArea.Coil ? (byte)0x01 : (byte)0x02;
            var response = await SendReadRequestAsync(functionCode, startAddress, count).ConfigureAwait(false);

            if (response.Length < 2)
            {
                throw new InvalidOperationException("Modbus响应长度异常");
            }

            byte byteCount = response[1];
            int expectedByteCount = (count + 7) / 8;
            if (byteCount != expectedByteCount || response.Length != expectedByteCount + 2)
            {
                throw new InvalidOperationException("Modbus响应位数据长度不匹配");
            }

            var values = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int byteIndex = 2 + (i / 8);
                int bitIndex = i % 8;
                values[i] = ((response[byteIndex] >> bitIndex) & 0x01) == 0x01;
            }

            return values;
        }

        private async Task<ushort[]> ReadRegisterChunkAsync(ModbusArea area, ushort startAddress, ushort count)
        {
            byte functionCode = area == ModbusArea.HoldingRegister ? (byte)0x03 : (byte)0x04;
            var response = await SendReadRequestAsync(functionCode, startAddress, count).ConfigureAwait(false);

            if (response.Length < 2)
            {
                throw new InvalidOperationException("Modbus响应长度异常");
            }

            byte byteCount = response[1];
            if (byteCount != count * 2 || response.Length != byteCount + 2)
            {
                throw new InvalidOperationException("Modbus响应寄存器字节数与请求不符");
            }

            var registers = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
            }

            return registers;
        }

        private Task WriteSingleCoilAsync(ushort address, bool value)
        {
            ushort coilValue = value ? (ushort)0xFF00 : (ushort)0x0000;
            return SendWriteSingleRequestAsync(0x05, address, coilValue);
        }

        private Task WriteSingleRegisterAsync(ushort address, ushort value)
        {
            return SendWriteSingleRequestAsync(0x06, address, value);
        }

        private Task WriteMultipleRegistersAsync(ushort startAddress, IReadOnlyList<ushort> values)
        {
            if (values == null || values.Count == 0)
            {
                return Task.CompletedTask;
            }

            return SendWriteMultipleRequestAsync(0x10, startAddress, values);
        }

        private async Task<byte[]> SendReadRequestAsync(byte functionCode, ushort startAddress, ushort quantity)
        {
            ushort transactionId = unchecked(++_transactionId);
            var request = new byte[12];

            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            request[2] = 0x00;
            request[3] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 0x0006);
            request[6] = _unitId;
            request[7] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), quantity);

            return await SendRequestAsync(request, functionCode).ConfigureAwait(false);
        }

        private async Task<byte[]> SendWriteSingleRequestAsync(byte functionCode, ushort address, ushort value)
        {
            ushort transactionId = unchecked(++_transactionId);
            var request = new byte[12];

            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            request[2] = 0x00;
            request[3] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 0x0006);
            request[6] = _unitId;
            request[7] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), value);

            return await SendRequestAsync(request, functionCode).ConfigureAwait(false);
        }

        private async Task<byte[]> SendWriteMultipleRequestAsync(byte functionCode, ushort startAddress, IReadOnlyList<ushort> values)
        {
            ushort transactionId = unchecked(++_transactionId);
            byte byteCount = (byte)(values.Count * 2);
            int pduLength = 6 + byteCount;
            var request = new byte[7 + pduLength];

            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            request[2] = 0x00;
            request[3] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)(pduLength + 1));
            request[6] = _unitId;

            int index = 7;
            request[index++] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(index, 2), startAddress);
            index += 2;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(index, 2), (ushort)values.Count);
            index += 2;
            request[index++] = byteCount;

            for (int i = 0; i < values.Count; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(index, 2), values[i]);
                index += 2;
            }

            return await SendRequestAsync(request, functionCode).ConfigureAwait(false);
        }

        private async Task<byte[]> SendRequestAsync(byte[] request, byte functionCode)
        {
            if (_stream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                throw new InvalidOperationException("PLC连接已断开");
            }

            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(request, 0, request.Length).ConfigureAwait(false);

                var header = new byte[7];
                await ReadExactAsync(header, 0, 7).ConfigureAwait(false);

                ushort responseLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                var responsePdu = new byte[responseLength - 1];
                await ReadExactAsync(responsePdu, 0, responsePdu.Length).ConfigureAwait(false);

                byte responseFunction = responsePdu[0];
                if (responseFunction == (byte)(functionCode | 0x80))
                {
                    byte exceptionCode = responsePdu.Length > 1 ? responsePdu[1] : (byte)0x00;
                    throw new InvalidOperationException($"Modbus异常响应: {exceptionCode}");
                }

                if (responseFunction != functionCode)
                {
                    throw new InvalidOperationException("Modbus响应功能码不匹配");
                }

                return responsePdu;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private async Task ReadExactAsync(byte[] buffer, int offset, int count)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("PLC连接已断开");
            }

            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await _stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("Modbus连接已断开");
                }

                totalRead += read;
            }
        }

        private void DisconnectInternal()
        {
            try
            {
                _stream?.Dispose();
                _tcpClient?.Dispose();
            }
            catch
            {
                // ignored
            }
            finally
            {
                _stream = null;
                _tcpClient = null;
            }
        }

        private void CheckConnectionAndReconnect()
        {
            if (_disposed)
            {
                return;
            }

            _isConnected = false;
            OnConnectionChanged?.Invoke(this, _isConnected);
            _ = StartAutoReconnect();
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                _reconnectTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already disposed
            }

            Task? reconnectTask;
            lock (_stateLock)
            {
                reconnectTask = _reconnectTask;
            }

            if (reconnectTask != null)
            {
                try
                {
                    reconnectTask.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is TaskCanceledException || inner is OperationCanceledException))
                {
                    // ignore cancellation
                }
            }

            _reconnectTokenSource.Dispose();
            DisconnectInternal();
            _ioLock.Dispose();
            _instance = null;

            GC.SuppressFinalize(this);
        }

        private class DeviceAddressMapping
        {
            public DeviceAddressMapping(string prefix, ModbusArea area, int baseAddress)
            {
                Prefix = prefix;
                Area = area;
                BaseAddress = baseAddress;
            }

            public string Prefix { get; }
            public ModbusArea Area { get; }
            public int BaseAddress { get; }
        }

        private enum ModbusArea
        {
            Coil,
            DiscreteInput,
            InputRegister,
            HoldingRegister
        }
    }
}
