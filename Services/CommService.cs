// Services/CommService.cs
using System;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Services
{
    public class CommService
    {
        private readonly IPLCService _plcService;
        private readonly ILogService _logService;

        private static readonly ChannelAddressMap Channel1Map = PLCAddressMapper.Channel1Addresses;
        private static readonly ChannelAddressMap Channel2Map = PLCAddressMapper.Channel2Addresses;

        private static readonly int Channel1SendStart = Channel1Map.CommSendStart;
        private static readonly int Channel2SendStart = Channel2Map.CommSendStart;
        private const int CommRegisterCount = 20;
        private const int ManualChannelSelectM = 81;
        private const int ManualSingleSendFlagM = 80;
        private const int ManualContinuousSendFlagM = 79;

        public CommService(IPLCService plcService)
            : this(plcService, LogService.Instance)
        {
        }

        public CommService(IPLCService plcService, ILogService? logService)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _logService = logService ?? LogService.Instance;
        }

        public Task<bool> SendMassageCommand(int channel, ushort[] payload)
        {
            return SendMessageAsync(channel, payload);
        }

        public Task ClearChannelSendBufferAsync(int channel)
        {
            return WriteChannelSendBufferAsync(channel, Array.Empty<ushort>());
        }

        public async Task<bool> SendMessageAsync(int channel, ushort[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            try
            {
                await WriteChannelSendBufferAsync(channel, payload).ConfigureAwait(false);
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
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.PowerOnMessage);
        }

        public Task<bool> SendSleepMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.SleepMessage);
        }

        public Task<bool> SendStopMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.StopMessage);
        }

        public Task<bool> SendMassageMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.MassageMessage);
        }

        public Task<bool> SendMassageMessage2Async(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.MassageMessage2);
        }

        public Task<bool> SendReadMessageAsync(int channel, ChannelConfig channelConfig)
        {
            return SendConfiguredMessageAsync(channel, channelConfig, c => c.ReadMessage);
        }

        private async Task<bool> SendConfiguredMessageAsync(int channel, ChannelConfig channelConfig, Func<MessageConfig, ushort[]> selector)
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
            return await SendMessageAsync(channel, channelPayload).ConfigureAwait(false);
        }

        public async Task WriteChannelSendBufferAsync(int channel, ushort[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            int startAddress = channel switch
            {
                1 => Channel1SendStart,
                2 => Channel2SendStart,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "仅支持通道1或通道2")
            };

            var bytes = PreparePayloadBytes(payload, CommRegisterCount);

            string address = $"D{startAddress}";
            await _plcService.WriteWordsAsync(address, bytes).ConfigureAwait(false);
        }

        public Task TriggerSingleSendAsync(int channel)
        {
            int bitAddress = channel switch
            {
                1 => Channel1Map.CommSingleSend,
                2 => Channel2Map.CommSingleSend,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "仅支持通道1或通道2")
            };

            string address = $"M{bitAddress}";
            return ToggleManualSendAsync(address);
        }

        private async Task ToggleManualSendAsync(string address)
        {
            await _plcService.WriteBitAsync(address, true).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            await _plcService.WriteBitAsync(address, false).ConfigureAwait(false);
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
                    2 => (ushort)0x02,
                    1 when result[1] == 0x02 => (ushort)0x01,
                    _ => result[1]
                };
            }

            return result;
        }

        public async Task StartContinuousSendAsync(int channel)
        {
            await ToggleContinuousSendAsync(channel, true).ConfigureAwait(false);
        }

        public async Task StopContinuousSendAsync(int channel)
        {
            await ToggleContinuousSendAsync(channel, false).ConfigureAwait(false);
        }

        public async Task SetManualSendChannelAsync(int channel)
        {
            bool value = channel switch
            {
                1 => false,
                2 => true,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "仅支持通道1或通道2")
            };

            await WriteManualBitAsync(ManualChannelSelectM, value).ConfigureAwait(false);
        }

        public Task SignalManualSingleSendAsync()
        {
            return WriteManualBitAsync(ManualSingleSendFlagM, true);
        }

        public Task SetManualContinuousSendFlagAsync(bool isEnabled)
        {
            return WriteManualBitAsync(ManualContinuousSendFlagM, isEnabled);
        }

        public Task<byte[]?> ReceiveStatusMessageAsync(int channel)
        {
            try
            {
                // 从PLC接收区读取数据
                var response = new byte[20];
                //for (int i = 0; i < response.Length; i++)
                //{
                //    response[i] = (byte)await _plcService.ReadWord($"D{250 + i}");
                //}
                return Task.FromResult<byte[]?>(response);
            }
            catch (Exception ex)
            {
                _logService.LogError("485接收错误", ex);
                return Task.FromResult<byte[]?>(null);
            }
        }

        private static byte[] PreparePayloadBytes(ushort[] payload, int registerCount)
        {
            var bytes = new byte[registerCount * 2];

            if (payload == null)
            {
                return bytes;
            }

            int length = Math.Min(payload.Length, registerCount);

            for (int i = 0; i < length; i++)
            {
                ushort value = payload[i];
                bytes[i * 2] = (byte)(value & 0xFF);
                bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return bytes;
        }

        private async Task ToggleContinuousSendAsync(int channel, bool value)
        {
            int bitAddress = channel switch
            {
                1 => Channel1Map.CommContinuousSend,
                2 => Channel2Map.CommContinuousSend,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "仅支持通道1或通道2")
            };

            string address = $"M{bitAddress}";
            await _plcService.WriteBitAsync(address, value).ConfigureAwait(false);
        }

        private async Task WriteManualBitAsync(int address, bool value)
        {
            string target = $"M{address}";
            await _plcService.WriteBitAsync(target, value).ConfigureAwait(false);
        }
    }
}

