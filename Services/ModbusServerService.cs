using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Services
{
    public class ModbusServerService : IDisposable
    {
        private const int RegisterBaseAddress = 40001;
        private const int HoldingRegisterCount = 200;
        private static readonly int[] WritableRegisters = { 40159, 40160 };

        private readonly object _registerLock = new();
        private readonly object _stateLock = new();
        private readonly ushort[] _holdingRegisters = new ushort[HoldingRegisterCount];
        private readonly List<Task> _clientTasks = new();
        private readonly ILogService _logService;

        private CancellationTokenSource? _cts;
        private TcpListener? _listener;
        private Task? _listenerTask;
        private string _bindAddress = "0.0.0.0";
        private int _port;
        private bool _disposed;

        public event EventHandler<bool>? OnServerStateChanged;

        public ModbusServerService(ILogService? logService = null)
        {
            _logService = logService ?? LogService.Instance;
        }

        public bool IsRunning { get; private set; }

        public MesIntegrationMode CurrentMode { get; private set; } = MesIntegrationMode.HttpPush;

        public async Task ApplyConfigurationAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            CurrentMode = config.MesIntegrationMode;
            if (config.MesIntegrationMode == MesIntegrationMode.ModbusServer)
            {
                string bindAddress = string.IsNullOrWhiteSpace(config.ModbusServerIp)
                    ? "0.0.0.0"
                    : config.ModbusServerIp.Trim();
                int port = NormalizePort(config.ModbusServerPort);

                await StartServerAsync(bindAddress, port, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StopServerAsync().ConfigureAwait(false);

                lock (_registerLock)
                {
                    WriteInt32(40159, 0);
                }
            }

            RaiseServerStateChanged();
        }

        public void UpdateFromTestRecord(TestRecord record, bool markPendingForMes)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            lock (_registerLock)
            {
                WriteString(40080, record.ProductCode ?? string.Empty, 20);

                var simultaneousDeflateResults = GetActionResults(record, LumbarActionType.SimultaneousDeflate);
                var simultaneousInflateResults = GetActionResults(record, LumbarActionType.SimultaneousInflate);

                WriteFloat(40121, simultaneousDeflateResults.ElementAtOrDefault(0)?.PeakCurrent);
                WriteFloat(40123, ToSeconds(simultaneousInflateResults.ElementAtOrDefault(0)?.ActualTime));
                WriteFloat(40125, simultaneousInflateResults.ElementAtOrDefault(0)?.PeakCurrent);
                WriteFloat(40127, ToSeconds(simultaneousDeflateResults.ElementAtOrDefault(0)?.ActualTime));
                WriteFloat(40129, record.StaticCurrent);
                WriteFloat(40131, record.TestVoltage);
                WriteAsciiStatus(40133, record.Result == TestResult.Pass ? "OK" : "NG");

                var upInflate = GetActionResults(record, LumbarActionType.UpInflateDownDeflate).FirstOrDefault();
                var downInflate = GetActionResults(record, LumbarActionType.DownInflateUpDeflate).FirstOrDefault();
                var simultaneousDeflate2 = simultaneousDeflateResults.ElementAtOrDefault(1);

                WriteInt32(40135, upInflate?.TargetTime ?? 0);
                WriteInt32(40137, downInflate?.TargetTime ?? 0);
                WriteInt32(40139, simultaneousDeflateResults.ElementAtOrDefault(0)?.TargetTime ?? 0);
                WriteInt32(40141, simultaneousInflateResults.ElementAtOrDefault(0)?.TargetTime ?? 0);
                WriteInt32(40143, simultaneousDeflate2?.TargetTime ?? 0);

                WriteInt32(40145, ToInt(upInflate?.TargetHeight));
                WriteInt32(40147, ToInt(downInflate?.TargetHeight));
                WriteInt32(40149, ToInt(simultaneousDeflateResults.ElementAtOrDefault(0)?.TargetHeight));
                WriteInt32(40151, ToInt(simultaneousInflateResults.ElementAtOrDefault(0)?.TargetHeight));
                WriteInt32(40153, ToInt(simultaneousDeflate2?.TargetHeight));

                WriteInt32(40159, markPendingForMes ? 1 : 0);
            }
        }

        public async Task StopServerAsync()
        {
            CancellationTokenSource? cts;
            Task? listenerTask;
            List<Task> clients;

            lock (_stateLock)
            {
                if (_listener == null)
                {
                    IsRunning = false;
                    return;
                }

                cts = _cts;
                listenerTask = _listenerTask;
                clients = new List<Task>(_clientTasks);
                _clientTasks.Clear();

                try
                {
                    _listener.Stop();
                }
                catch (Exception ex)
                {
                    _logService.LogWarning("停止 Modbus 服务器时出现警告", ex);
                }

                _listener = null;
                _listenerTask = null;
                _cts = null;
                _bindAddress = "0.0.0.0";
                _port = 0;
                IsRunning = false;
            }

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    _logService.LogWarning("取消 Modbus 服务器任务时出现异常", ex);
                }
                cts.Dispose();
            }

            if (listenerTask != null)
            {
                try
                {
                    await listenerTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logService.LogWarning("等待 Modbus 监听任务结束时出现异常", ex);
                }
            }

            foreach (var clientTask in clients)
            {
                try
                {
                    await clientTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 忽略客户端异常
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                StopServerAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logService.LogWarning("释放 Modbus 服务器资源时出现异常", ex);
            }
        }

        private async Task StartServerAsync(string bindAddress, int port, CancellationToken cancellationToken)
        {
            bool needsRestart = false;

            lock (_stateLock)
            {
                if (_listener != null)
                {
                    if (IsRunning && string.Equals(_bindAddress, bindAddress, StringComparison.OrdinalIgnoreCase) && _port == port)
                    {
                        return;
                    }

                    needsRestart = true;
                }
            }

            if (needsRestart)
            {
                await StopServerAsync().ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!TryParseAddress(bindAddress, out var ipAddress))
            {
                _logService.LogWarning($"Modbus 服务端绑定地址无效，使用0.0.0.0替代: {bindAddress}");
                ipAddress = IPAddress.Any;
            }

            var listener = new TcpListener(ipAddress, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                _logService.LogError($"启动 Modbus 服务器失败，端口: {port}", ex);
                throw;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cts.Token;

            Task listenerTask = Task.Run(() => AcceptLoopAsync(listener, token), token);

            lock (_stateLock)
            {
                _listener = listener;
                _cts = cts;
                _listenerTask = listenerTask;
                _bindAddress = bindAddress;
                _port = port;
                IsRunning = true;
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    var acceptTask = listener.AcceptTcpClientAsync();
                    var completed = await Task.WhenAny(acceptTask, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
                    if (completed != acceptTask)
                    {
                        break;
                    }

                    client = acceptTask.Result;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logService.LogWarning("Modbus 服务器接受客户端连接失败", ex);
                    continue;
                }

                if (client == null)
                {
                    continue;
                }

                var clientTask = HandleClientAsync(client, token);

                lock (_stateLock)
                {
                    _clientTasks.Add(clientTask);
                }

                _ = clientTask.ContinueWith(t =>
                {
                    lock (_stateLock)
                    {
                        _clientTasks.Remove(t);
                    }
                }, TaskScheduler.Default);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                client.NoDelay = true;

                NetworkStream stream = client.GetStream();
                var headerBuffer = new byte[7];

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!await ReadExactAsync(stream, headerBuffer, token).ConfigureAwait(false))
                        {
                            break;
                        }

                        ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(headerBuffer.AsSpan(0, 2));
                        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(headerBuffer.AsSpan(2, 2));
                        ushort lengthField = BinaryPrimitives.ReadUInt16BigEndian(headerBuffer.AsSpan(4, 2));
                        byte unitId = headerBuffer[6];

                        if (protocolId != 0 || lengthField == 0)
                        {
                            await SendExceptionResponseAsync(stream, transactionId, unitId, 0x01, 0x04, token).ConfigureAwait(false);
                            break;
                        }

                        int pduLength = lengthField - 1; // exclude unit id
                        if (pduLength <= 0)
                        {
                            await SendExceptionResponseAsync(stream, transactionId, unitId, 0x01, 0x03, token).ConfigureAwait(false);
                            break;
                        }

                        var pdu = new byte[pduLength];
                        if (!await ReadExactAsync(stream, pdu, token).ConfigureAwait(false))
                        {
                            break;
                        }

                        byte functionCode = pdu[0];

                        switch (functionCode)
                        {
                            case 0x03:
                                await HandleReadHoldingRegistersAsync(stream, transactionId, unitId, pdu, token).ConfigureAwait(false);
                                break;
                            case 0x06:
                                await HandleWriteSingleRegisterAsync(stream, transactionId, unitId, pdu, token).ConfigureAwait(false);
                                break;
                            case 0x10:
                                await HandleWriteMultipleRegistersAsync(stream, transactionId, unitId, pdu, token).ConfigureAwait(false);
                                break;
                            default:
                                await SendExceptionResponseAsync(stream, transactionId, unitId, functionCode, 0x01, token).ConfigureAwait(false);
                                break;
                        }
                    }
                }
                catch (IOException)
                {
                    // 连接中断
                }
                catch (ObjectDisposedException)
                {
                    // 流已关闭
                }
                catch (Exception ex)
                {
                    _logService.LogWarning("处理 Modbus 客户端请求时出现异常", ex);
                }
            }
        }

        private async Task HandleReadHoldingRegistersAsync(NetworkStream stream, ushort transactionId, byte unitId, byte[] pdu, CancellationToken token)
        {
            if (pdu.Length < 5)
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x03, 0x03, token).ConfigureAwait(false);
                return;
            }

            int startOffset = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
            int quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));

            if (quantity <= 0 || quantity > 125)
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x03, 0x03, token).ConfigureAwait(false);
                return;
            }

            if (!TryReadHoldingRegisters(startOffset, quantity, out var registers, out byte exception))
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x03, exception, token).ConfigureAwait(false);
                return;
            }

            int byteCount = quantity * 2;
            var response = new byte[9 + byteCount];

            ushort length = (ushort)(byteCount + 3);

            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = (byte)(length >> 8);
            response[5] = (byte)(length & 0xFF);
            response[6] = unitId;
            response[7] = 0x03;
            response[8] = (byte)byteCount;

            for (int i = 0; i < quantity; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + i * 2, 2), registers[i]);
            }

            await stream.WriteAsync(response, 0, response.Length, token).ConfigureAwait(false);
        }

        private async Task HandleWriteSingleRegisterAsync(NetworkStream stream, ushort transactionId, byte unitId, byte[] pdu, CancellationToken token)
        {
            if (pdu.Length < 5)
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x06, 0x03, token).ConfigureAwait(false);
                return;
            }

            int startOffset = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));

            if (!TryWriteHoldingRegisters(startOffset, stackalloc ushort[] { value }, out byte exception))
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x06, exception, token).ConfigureAwait(false);
                return;
            }

            var response = new byte[12];
            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = 0x00;
            response[5] = 0x06;
            response[6] = unitId;
            response[7] = 0x06;
            response[8] = pdu[1];
            response[9] = pdu[2];
            response[10] = pdu[3];
            response[11] = pdu[4];

            await stream.WriteAsync(response, 0, response.Length, token).ConfigureAwait(false);
        }

        private async Task HandleWriteMultipleRegistersAsync(NetworkStream stream, ushort transactionId, byte unitId, byte[] pdu, CancellationToken token)
        {
            if (pdu.Length < 6)
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x10, 0x03, token).ConfigureAwait(false);
                return;
            }

            int startOffset = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
            int quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
            int byteCount = pdu[5];

            if (quantity <= 0 || quantity > 123 || byteCount != quantity * 2 || pdu.Length < 6 + byteCount)
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x10, 0x03, token).ConfigureAwait(false);
                return;
            }

            var values = new ushort[quantity];
            for (int i = 0; i < quantity; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2, 2));
            }

            if (!TryWriteHoldingRegisters(startOffset, values, out byte exception))
            {
                await SendExceptionResponseAsync(stream, transactionId, unitId, 0x10, exception, token).ConfigureAwait(false);
                return;
            }

            var response = new byte[12];
            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = 0x00;
            response[5] = 0x06;
            response[6] = unitId;
            response[7] = 0x10;
            response[8] = pdu[1];
            response[9] = pdu[2];
            response[10] = pdu[3];
            response[11] = pdu[4];

            await stream.WriteAsync(response, 0, response.Length, token).ConfigureAwait(false);
        }

        private bool TryReadHoldingRegisters(int startOffset, int quantity, out ushort[] registers, out byte exceptionCode)
        {
            registers = Array.Empty<ushort>();
            exceptionCode = 0x02;

            if (startOffset < 0 || quantity <= 0 || startOffset + quantity > _holdingRegisters.Length)
            {
                return false;
            }

            registers = new ushort[quantity];

            lock (_registerLock)
            {
                Array.Copy(_holdingRegisters, startOffset, registers, 0, quantity);
            }

            exceptionCode = 0x00;
            return true;
        }

        private bool TryWriteHoldingRegisters(int startOffset, ReadOnlySpan<ushort> values, out byte exceptionCode)
        {
            exceptionCode = 0x02;

            if (startOffset < 0 || startOffset + values.Length > _holdingRegisters.Length)
            {
                return false;
            }

            for (int i = 0; i < values.Length; i++)
            {
                int register = RegisterBaseAddress + startOffset + i;
                if (!WritableRegisters.Contains(register))
                {
                    exceptionCode = 0x03;
                    return false;
                }
            }

            lock (_registerLock)
            {
                values.CopyTo(_holdingRegisters.AsSpan(startOffset));
            }

            exceptionCode = 0x00;
            return true;
        }

        private void WriteString(int registerAddress, string value, int registerCount)
        {
            Span<byte> buffer = stackalloc byte[registerCount * 2];
            buffer.Clear();
            if (!string.IsNullOrEmpty(value))
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                int length = Math.Min(bytes.Length, buffer.Length);
                bytes.AsSpan(0, length).CopyTo(buffer);
            }

            var registers = new ushort[registerCount];
            for (int i = 0; i < registerCount; i++)
            {
                registers[i] = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(i * 2, 2));
            }

            WriteRegisters(registerAddress, registers);
        }

        private void WriteAsciiStatus(int registerAddress, string text)
        {
            Span<byte> bytes = stackalloc byte[4];
            bytes.Clear();

            if (!string.IsNullOrEmpty(text))
            {
                var ascii = Encoding.ASCII.GetBytes(text);
                int length = Math.Min(ascii.Length, bytes.Length);
                ascii.AsSpan(0, length).CopyTo(bytes);
            }

            var registers = new ushort[2];
            registers[0] = BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]);
            registers[1] = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2));

            WriteRegisters(registerAddress, registers);
        }

        private void WriteFloat(int registerAddress, double? value)
        {
            float number = value.HasValue ? (float)value.Value : 0f;
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(number));

            var registers = new ushort[2];
            registers[0] = BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]);
            registers[1] = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2));

            WriteRegisters(registerAddress, registers);
        }

        private void WriteInt32(int registerAddress, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);

            var registers = new ushort[2];
            registers[0] = BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]);
            registers[1] = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2));

            WriteRegisters(registerAddress, registers);
        }

        private void WriteRegisters(int registerAddress, ReadOnlySpan<ushort> values)
        {
            int offset = registerAddress - RegisterBaseAddress;
            if (offset < 0 || offset + values.Length > _holdingRegisters.Length)
            {
                return;
            }

            values.CopyTo(_holdingRegisters.AsSpan(offset));
        }

        private static double? ToSeconds(int? milliseconds)
        {
            return milliseconds.HasValue ? milliseconds.Value / 1000.0 : null;
        }

        private static int ToInt(double? value)
        {
            return value.HasValue ? (int)Math.Round(value.Value) : 0;
        }

        private static IEnumerable<LumbarActionResult> GetActionResults(TestRecord record, LumbarActionType action)
        {
            return record.LumbarResults
                .Where(r => r?.Action == action)
                .OrderBy(r => r?.Order ?? int.MaxValue)
                .Where(r => r != null)!;
        }

        private static bool TryParseAddress(string value, out IPAddress address)
        {
            if (string.Equals(value, "0.0.0.0", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "::", StringComparison.OrdinalIgnoreCase))
            {
                address = IPAddress.Any;
                return true;
            }

            return IPAddress.TryParse(value, out address!);
        }

        private static int NormalizePort(int port)
        {
            if (port <= 0 || port > 65535)
            {
                return 502;
            }

            return port;
        }

        private async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            int remaining = buffer.Length;

            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer, offset, remaining, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
                remaining -= read;
            }

            return true;
        }

        private async Task SendExceptionResponseAsync(NetworkStream stream, ushort transactionId, byte unitId, byte functionCode, byte exceptionCode, CancellationToken token)
        {
            var response = new byte[9];
            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = 0x00;
            response[5] = 0x03;
            response[6] = unitId;
            response[7] = (byte)(functionCode | 0x80);
            response[8] = exceptionCode;

            try
            {
                await stream.WriteAsync(response, 0, response.Length, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略发送异常
            }
        }

        private void RaiseServerStateChanged()
        {
            try
            {
                OnServerStateChanged?.Invoke(this, IsRunning);
            }
            catch (Exception ex)
            {
                _logService.LogWarning("Modbus 状态通知事件处理失败", ex);
            }
        }
    }
}
