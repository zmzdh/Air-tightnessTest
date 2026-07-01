using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public sealed class SensorModbusService : IDisposable
    {
        private const ushort MeasurementStartAddress = 0x0000;
        private const ushort MeasurementRegisterCount = 2;
        private const int DefaultChannelCount = 4;
        private const int MaxChannelCount = 16;
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan InterMeasurementDelay = TimeSpan.FromMilliseconds(20);
        private static readonly TimeSpan InterChannelDelay = TimeSpan.FromMilliseconds(80);
        private readonly ModbusRtuClient _client;
        private readonly ILogService _logService;
        private readonly Dictionary<int, SensorMeasurement> _measurements = new();
        private readonly object _syncRoot = new();
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        private int _channelCount;
        private volatile bool _enableHeightReading = true;

        public event EventHandler? MeasurementsUpdated;

        public bool EnableHeightReading
        {
            get => _enableHeightReading;
            set => _enableHeightReading = value;
        }

        public SensorModbusService(SerialPortConfig config, ILogService? logService = null, int channelCount = DefaultChannelCount)
        {
            _logService = logService ?? LogService.Instance;
            _client = new ModbusRtuClient(config, _logService);
            _channelCount = channelCount;
            if (_channelCount <= 0 || _channelCount > MaxChannelCount)
            {
                throw new ArgumentOutOfRangeException(nameof(channelCount), $"通道数量必须在 1-{MaxChannelCount} 之间");
            }

            for (int channel = 1; channel <= _channelCount; channel++)
            {
                _measurements[channel] = new SensorMeasurement();
            }
        }

        public void UpdateConfig(SerialPortConfig config)
        {
            _client.UpdateConfig(config);
        }

        public void UpdateChannelCount(int channelCount)
        {
            if (channelCount <= 0 || channelCount > MaxChannelCount)
            {
                throw new ArgumentOutOfRangeException(nameof(channelCount), $"通道数量必须在 1-{MaxChannelCount} 之间");
            }

            lock (_syncRoot)
            {
                _channelCount = channelCount;
                for (int channel = 1; channel <= _channelCount; channel++)
                {
                    if (!_measurements.ContainsKey(channel))
                    {
                        _measurements[channel] = new SensorMeasurement();
                    }
                }
            }
        }

        public void StartPolling(TimeSpan interval)
        {
            if (_pollingTask != null && !_pollingTask.IsCompleted)
            {
                return;
            }

            _pollingCts?.Cancel();
            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;

            _pollingTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await PollAllChannelsAsync(token).ConfigureAwait(false);
                        MeasurementsUpdated?.Invoke(this, EventArgs.Empty);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError("高度/电流采集失败", ex);
                    }

                    try
                    {
                        await Task.Delay(interval, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        public void StopPolling()
        {
            _pollingCts?.Cancel();
        }

        public bool TryGetMeasurement(int channel, out SensorMeasurement measurement)
        {
            lock (_syncRoot)
            {
                if (_measurements.TryGetValue(channel, out var stored))
                {
                    measurement = stored;
                    return true;
                }
            }

            measurement = default;
            return false;
        }

        public bool TryGetMeasurement(int channel, TimeSpan maxAge, out SensorMeasurement measurement)
        {
            if (!TryGetMeasurement(channel, out measurement))
            {
                return false;
            }

            if (maxAge <= TimeSpan.Zero)
            {
                return true;
            }

            return DateTime.Now - measurement.Timestamp <= maxAge;
        }

        public async Task<SensorMeasurement?> WaitForMeasurementAsync(
            int channel,
            TimeSpan maxAge,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (TryGetMeasurement(channel, maxAge, out var measurement))
            {
                return measurement;
            }

            var tcs = new TaskCompletionSource<SensorMeasurement?>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, EventArgs args)
            {
                if (TryGetMeasurement(channel, maxAge, out var updated))
                {
                    tcs.TrySetResult(updated);
                }
            }

            MeasurementsUpdated += Handler;

            try
            {
                var delayTask = Task.Delay(timeout, cancellationToken);
                var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                if (completed == tcs.Task)
                {
                    return await tcs.Task.ConfigureAwait(false);
                }

                return null;
            }
            finally
            {
                MeasurementsUpdated -= Handler;
            }
        }

        public async Task<double> ReadCurrentMilliAmpsAsync(int channel, CancellationToken cancellationToken)
        {
            int raw = await ReadRawValueAsync(GetCurrentStationId(channel), cancellationToken).ConfigureAwait(false);
            return raw / 100.0;
        }

        public async Task<double> ReadHeightMillimetersAsync(int channel, CancellationToken cancellationToken)
        {
            int raw = await ReadRawValueAsync(GetHeightStationId(channel), cancellationToken).ConfigureAwait(false);
            return raw / 100.0;
        }

        public Task<int> ReadHeightRawAsync(int channel, CancellationToken cancellationToken)
        {
            return ReadRawValueAsync(GetHeightStationId(channel), cancellationToken);
        }

        public void Dispose()
        {
            StopPolling();
            _client.Dispose();
        }

        private async Task PollAllChannelsAsync(CancellationToken cancellationToken)
        {
            for (int channel = 1; channel <= _channelCount; channel++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool updated = false;
                SensorMeasurement measurement;
                lock (_syncRoot)
                {
                    measurement = _measurements[channel];
                }

                int? currentRaw = await TryReadRawValueAsync(channel, GetCurrentStationId(channel), "电流", cancellationToken).ConfigureAwait(false);
                if (currentRaw.HasValue)
                {
                    measurement.CurrentRaw = currentRaw.Value;
                    updated = true;
                }

                if (_enableHeightReading)
                {
                    await Task.Delay(InterMeasurementDelay, cancellationToken).ConfigureAwait(false);
                    int? heightRaw = await TryReadRawValueAsync(channel, GetHeightStationId(channel), "高度", cancellationToken)
                        .ConfigureAwait(false);
                    if (heightRaw.HasValue)
                    {
                        measurement.HeightRaw = heightRaw.Value;
                        updated = true;
                    }
                }

                if (updated)
                {
                    measurement.Timestamp = DateTime.Now;
                    lock (_syncRoot)
                    {
                        _measurements[channel] = measurement;
                    }
                }

                if (channel < _channelCount)
                {
                    await Task.Delay(InterChannelDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<int?> TryReadRawValueAsync(int channel, byte stationId, string measurementName, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReadTimeout);

            try
            {
                return await ReadRawValueAsync(stationId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logService.LogError($"设备2通道{channel}{measurementName}读取超时(站号{stationId})");
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError($"设备2通道{channel}{measurementName}读取失败(站号{stationId})", ex);
                return null;
            }
        }

        private async Task<int> ReadRawValueAsync(byte stationId, CancellationToken cancellationToken)
        {
            ushort[] registers = await _client.ReadHoldingRegistersAsync(
                stationId,
                MeasurementStartAddress,
                MeasurementRegisterCount,
                cancellationToken).ConfigureAwait(false);

            if (registers.Length < 2)
            {
                return 0;
            }

            var buffer = new byte[4];
            buffer[0] = (byte)(registers[0] >> 8);
            buffer[1] = (byte)(registers[0] & 0xFF);
            buffer[2] = (byte)(registers[1] >> 8);
            buffer[3] = (byte)(registers[1] & 0xFF);

            return BinaryPrimitives.ReadInt32BigEndian(buffer);
        }

        private static byte GetCurrentStationId(int channel)
        {
            if (channel <= 0 || channel > MaxChannelCount)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), $"仅支持通道1-{MaxChannelCount}");
            }

            return (byte)(10 + channel);
        }

        private static byte GetHeightStationId(int channel)
        {
            if (channel <= 0 || channel > MaxChannelCount)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), $"仅支持通道1-{MaxChannelCount}");
            }

            return (byte)(20 + channel);
        }
    }

    public struct SensorMeasurement
    {
        public int CurrentRaw { get; set; }
        public int HeightRaw { get; set; }
        public DateTime Timestamp { get; set; }

        public double CurrentMilliAmps => CurrentRaw / 100.0;
        public double HeightMillimeters => HeightRaw / 100.0;
    }
}
