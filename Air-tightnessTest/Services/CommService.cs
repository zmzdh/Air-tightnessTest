// Services/CommService.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public class CommService
    {
        private readonly SerialPortService _serialPortService;
        private readonly ILogService _logService;
        private readonly List<byte> _receiveBuffer = new();
        private readonly object _bufferLock = new();
        private readonly object _messageLock = new();
        private readonly Dictionary<int, CommMessageSnapshot> _latestMessages = new();

        private const int FrameLength = 20;
        private static readonly byte[] FrameEnd = { 0x0D, 0x0A };

        public event EventHandler<CommMessageReceivedEventArgs>? MessageReceived;

        public CommService(SerialPortService serialPortService)
            : this(serialPortService, LogService.Instance)
        {
        }

        public CommService(SerialPortService serialPortService, ILogService? logService)
        {
            _serialPortService = serialPortService ?? throw new ArgumentNullException(nameof(serialPortService));
            _logService = logService ?? LogService.Instance;
            _serialPortService.DataReceived += SerialPortService_DataReceived;
        }

        public Task<bool> SendMassageCommand(int channel, ushort[] payload)
        {
            return SendMessageAsync(channel, payload, SendPriority.Low);
        }

        public Task ClearChannelSendBufferAsync(int channel)
        {
            return Task.CompletedTask;
        }

        public Task<bool> SendMessageAsync(int channel, ushort[] payload)
        {
            return SendMessageAsync(channel, payload, SendPriority.Normal);
        }

        private async Task<bool> SendMessageAsync(int channel, ushort[] payload, SendPriority priority)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            try
            {
                byte[] bytes = ConvertToBytes(payload);
                await _serialPortService.SendAsync(bytes, priority).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("485通讯错误", ex);
                return false;
            }
        }

        public async Task<bool> SendRawMessageAsync(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            try
            {
                await _serialPortService.SendAsync(payload, SendPriority.Normal).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("485通讯错误", ex);
                return false;
            }
        }

        public Task<bool> SendPowerOnMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.PowerOnMessage, SendPriority.Normal);
        }

        public Task<bool> SendSleepMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.SleepMessage, SendPriority.Normal);
        }

        public Task<bool> SendStopMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.StopMessage, SendPriority.Normal);
        }

        public Task<bool> SendMassageMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.MassageMessage, SendPriority.Low);
        }

        public Task<bool> SendMassageMessage2Async(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.MassageMessage2, SendPriority.Low);
        }

        public Task<bool> SendReadMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.ReadMessage, SendPriority.High);
        }

        public bool TryGetLatestMessage(int channel, out CommMessageSnapshot snapshot)
        {
            lock (_messageLock)
            {
                return _latestMessages.TryGetValue(channel, out snapshot);
            }
        }

        private async Task<bool> SendConfiguredMessageAsync(int channel, ChannelConfig channelConfig, Func<MessageConfig, ushort[]> selector, SendPriority priority)
        {
            if (channelConfig?.MessageConfig == null)
            {
                return false;
            }

            var payload = selector(channelConfig.MessageConfig);
            if (payload == null)
            {
                return false;
            }

            var channelPayload = PrepareChannelMessagePayload(channel, payload);
            return await SendMessageAsync(channel, channelPayload, priority).ConfigureAwait(false);
        }

        public static ushort[] PrepareChannelMessagePayload(int channel, ushort[] payload)
        {
            if (payload == null)
            {
                return Array.Empty<ushort>();
            }

            var result = new ushort[payload.Length];
            Array.Copy(payload, result, payload.Length);

            if (result.Length > 1)
            {
                result[1] = channel switch
                {
                    1 => (ushort)0x01,
                    2 => (ushort)0x02,
                    3 => (ushort)0x03,
                    4 => (ushort)0x04,
                    _ => result[1]
                };
            }

            return result;
        }

        private static byte[] ConvertToBytes(ushort[] payload)
        {
            var bytes = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                bytes[i] = payload[i] > byte.MaxValue ? (byte)byte.MaxValue : (byte)payload[i];
            }

            return bytes;
        }

        private void SerialPortService_DataReceived(object? sender, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            lock (_bufferLock)
            {
                _receiveBuffer.AddRange(data);
                ProcessReceiveBuffer();
            }
        }

        private void ProcessReceiveBuffer()
        {
            while (_receiveBuffer.Count >= FrameLength)
            {
                int startIndex = _receiveBuffer.IndexOf(0x3A);
                if (startIndex < 0)
                {
                    _receiveBuffer.Clear();
                    return;
                }

                if (startIndex > 0)
                {
                    _receiveBuffer.RemoveRange(0, startIndex);
                }

                if (_receiveBuffer.Count < FrameLength)
                {
                    return;
                }

                if (_receiveBuffer[FrameLength - 2] != FrameEnd[0] || _receiveBuffer[FrameLength - 1] != FrameEnd[1])
                {
                    _receiveBuffer.RemoveAt(0);
                    continue;
                }

                var frame = _receiveBuffer.GetRange(0, FrameLength).ToArray();
                _receiveBuffer.RemoveRange(0, FrameLength);

                int address = frame[1];
                if (address < 1 || address > 4)
                {
                    continue;
                }

                lock (_messageLock)
                {
                    _latestMessages[address] = new CommMessageSnapshot(address, frame, DateTime.UtcNow);
                }

                MessageReceived?.Invoke(this, new CommMessageReceivedEventArgs(address, frame));
            }
        }
    }

    public sealed class CommMessageSnapshot
    {
        public CommMessageSnapshot(int channel, byte[] payload, DateTime receivedAt)
        {
            Channel = channel;
            Payload = payload ?? Array.Empty<byte>();
            ReceivedAt = receivedAt;
        }

        public int Channel { get; }
        public byte[] Payload { get; }
        public DateTime ReceivedAt { get; }
    }

    public sealed class CommMessageReceivedEventArgs : EventArgs
    {
        public CommMessageReceivedEventArgs(int channel, byte[] payload)
        {
            Channel = channel;
            Payload = payload ?? Array.Empty<byte>();
        }

        public int Channel { get; }
        public byte[] Payload { get; }
    }
}
