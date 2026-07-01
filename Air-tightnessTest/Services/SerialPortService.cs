using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public sealed class SerialPortService : IDisposable
    {
        private readonly ILogService _logService;
        private readonly object _syncRoot = new();
        private readonly ConcurrentQueue<SendRequest> _highPriorityQueue = new();
        private readonly ConcurrentQueue<SendRequest> _normalPriorityQueue = new();
        private readonly ConcurrentQueue<SendRequest> _lowPriorityQueue = new();
        private readonly SemaphoreSlim _sendSignal = new(0);
        private readonly CancellationTokenSource _sendLoopCts = new();
        private readonly Task _sendLoopTask;
        private readonly TimeSpan _minSendInterval = TimeSpan.FromMilliseconds(200);
        private SerialPortConfig _config;
        private SerialPort? _serialPort;
        private bool _disposed;
        private DateTime _lastSendUtc = DateTime.MinValue;
        private int _consecutiveFailures;
        private int _highPriorityBurst;

        private const int MaxSendRetries = 3;
        private const int MaxHighPriorityBurst = 5;

        public event EventHandler<byte[]>? DataReceived;

        public SerialPortService(SerialPortConfig config, ILogService? logService = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logService = logService ?? LogService.Instance;
            _sendLoopTask = Task.Run(() => ProcessSendQueueAsync(_sendLoopCts.Token));
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

        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            return SendAsync(data, SendPriority.Normal, cancellationToken);
        }

        public async Task SendAsync(byte[] data, SendPriority priority, CancellationToken cancellationToken = default)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length == 0)
            {
                return;
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SerialPortService));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var payload = new byte[data.Length];
            Array.Copy(data, payload, data.Length);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new SendRequest(payload, completion, cancellationToken, priority);

            EnqueueRequest(request);
            await completion.Task.ConfigureAwait(false);
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
                _sendLoopCts.Cancel();
                _sendSignal.Release();
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }

            _sendLoopCts.Dispose();
        }

        private async Task ProcessSendQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await _sendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    while (TryDequeue(out var request))
                    {
                        if (request.CancellationToken.IsCancellationRequested)
                        {
                            request.Completion.TrySetCanceled(request.CancellationToken);
                            continue;
                        }

                        try
                        {
                            await EnsureSendIntervalAsync(cancellationToken).ConfigureAwait(false);
                            await SendWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            request.Completion.TrySetCanceled(cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            request.Completion.TrySetException(ex);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void EnqueueRequest(SendRequest request)
        {
            switch (request.Priority)
            {
                case SendPriority.High:
                    _highPriorityQueue.Enqueue(request);
                    break;
                case SendPriority.Low:
                    _lowPriorityQueue.Enqueue(request);
                    break;
                default:
                    _normalPriorityQueue.Enqueue(request);
                    break;
            }

            _sendSignal.Release();
        }

        private bool TryDequeue(out SendRequest request)
        {
            if (_highPriorityQueue.TryDequeue(out request) &&
                (_highPriorityBurst < MaxHighPriorityBurst || (_normalPriorityQueue.IsEmpty && _lowPriorityQueue.IsEmpty)))
            {
                _highPriorityBurst++;
                return true;
            }

            if (_normalPriorityQueue.TryDequeue(out request))
            {
                _highPriorityBurst = 0;
                return true;
            }

            if (_lowPriorityQueue.TryDequeue(out request))
            {
                _highPriorityBurst = 0;
                return true;
            }

            if (_highPriorityQueue.TryDequeue(out request))
            {
                _highPriorityBurst++;
                return true;
            }

            return false;
        }

        private async Task EnsureSendIntervalAsync(CancellationToken cancellationToken)
        {
            if (_lastSendUtc == DateTime.MinValue)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - _lastSendUtc;
            if (elapsed < _minSendInterval)
            {
                await Task.Delay(_minSendInterval - elapsed, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SendWithRetriesAsync(SendRequest request, CancellationToken cancellationToken)
        {
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxSendRetries; attempt++)
            {
                try
                {
                    SerialPort port = EnsurePort();
                    await port.BaseStream.WriteAsync(request.Data, 0, request.Data.Length, cancellationToken).ConfigureAwait(false);
                    await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    _lastSendUtc = DateTime.UtcNow;
                    _consecutiveFailures = 0;
                    request.Completion.TrySetResult(true);
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastException = ex;
                    _consecutiveFailures++;
                    _logService.LogWarning($"串口发送失败，准备第 {attempt} 次重试", ex);
                    ReopenPort();
                    var delay = TimeSpan.FromMilliseconds(100 * attempt);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (lastException != null)
            {
                _logService.LogError("串口发送失败，已达到重试上限", lastException);
                throw lastException;
            }
        }

        private SerialPort EnsurePort()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SerialPortService));
                }

                if (_serialPort == null)
                {
                    _serialPort = CreatePort(_config);
                    _serialPort.DataReceived += SerialPort_DataReceived;
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
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (_serialPort != null)
                {
                    try
                    {
                        _serialPort.DataReceived -= SerialPort_DataReceived;
                        _serialPort.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning("关闭旧串口失败", ex);
                    }
                    finally
                    {
                        _serialPort = null;
                    }
                }

                try
                {
                    _serialPort = CreatePort(_config);
                    _serialPort.DataReceived += SerialPort_DataReceived;
                    _serialPort.Open();
                }
                catch (Exception ex)
                {
                    _logService.LogError("打开串口失败", ex);
                }
            }
        }

        private SerialPort CreatePort(SerialPortConfig config)
        {
            var port = new SerialPort
            {
                PortName = config.PortName,
                BaudRate = config.BaudRate,
                DataBits = config.DataBits,
                Parity = ParseParity(config.Parity),
                StopBits = ParseStopBits(config.StopBits),
                ReadTimeout = 500,
                WriteTimeout = 500
            };

            return port;
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var port = _serialPort;
                if (port == null || !port.IsOpen)
                {
                    return;
                }

                int count = port.BytesToRead;
                if (count <= 0)
                {
                    return;
                }

                var buffer = new byte[count];
                int read = port.Read(buffer, 0, count);
                if (read > 0)
                {
                    if (read != buffer.Length)
                    {
                        Array.Resize(ref buffer, read);
                    }

                    DataReceived?.Invoke(this, buffer);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("串口接收失败", ex);
            }
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

        private readonly record struct SendRequest(byte[] Data, TaskCompletionSource<bool> Completion, CancellationToken CancellationToken, SendPriority Priority);
    }

    public enum SendPriority
    {
        Low = 0,
        Normal = 1,
        High = 2
    }
}
