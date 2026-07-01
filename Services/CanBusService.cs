using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Services
{
    public enum SeatChannel
    {
        Left,
        Right
    }

    public class CanBusService : IDisposable
    {
        private readonly uint _deviceIndex;
        private readonly CanTestConfig _config;
        private readonly ILogService _logService;
        private bool _opened;
        private readonly object _syncRoot = new();

        private static readonly byte[] ModeBitmaps = { 0x00, 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18 };
        private const uint TestModeId = 0x15;
        private const uint StrengthId = 0x161;
        private const uint FrequencyStrengthId = 0x172;
        private const uint StatusFeedbackId = 0x357;

        public CanBusService(CanTestConfig config, ILogService? logService = null, uint deviceIndex = 0)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logService = logService ?? LogService.Instance;
            _deviceIndex = deviceIndex;
        }

        public bool Open()
        {
            lock (_syncRoot)
            {
                if (_opened)
                {
                    return true;
                }

                try
                {
                    var init = new Can_Config
                    {
                        Baudrate = (uint)Math.Max(1, _config.BaudRate),
                        Pres = 0,
                        Tseg1 = 0,
                        Tseg2 = 0,
                        SJW = 0,
                        Config = 0x01,
                        Model = 0,
                        Reserved = 0
                    };

                    int openResult = hcanbusdll.CAN_OpenDevice(_deviceIndex);
                    if (openResult != (int)CanStatus.STATUS_OK)
                    {
                        _logService.LogError($"打开 CAN 设备失败: 设备{_deviceIndex}, 返回码 {openResult}");
                        return false;
                    }

                    int initResult = hcanbusdll.CAN_Init(_deviceIndex, ref init);
                    if (initResult != (int)CanStatus.STATUS_OK)
                    {
                        _logService.LogError($"初始化 CAN 设备失败: 设备{_deviceIndex}, 返回码 {initResult}");
                        return false;
                    }

                    _opened = true;
                    _logService.LogInfo($"CAN 设备{_deviceIndex}初始化成功，波特率 {_config.BaudRate}");
                    return true;
                }
                catch (DllNotFoundException ex)
                {
                    _logService.LogError("未找到 hcanbus.dll，无法初始化 CAN 设备。", ex);
                    return false;
                }
                catch (BadImageFormatException ex)
                {
                    _logService.LogError("hcanbus.dll 位数或签名不匹配，请确认驱动文件。", ex);
                    return false;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                _logService.LogWarning("释放 CAN 资源时出现异常", ex);
            }
        }

        public void Close()
        {
            lock (_syncRoot)
            {
                if (!_opened)
                {
                    return;
                }

                hcanbusdll.CAN_CloseDevice(_deviceIndex);
                _opened = false;
            }
        }

        public Task<bool> SendTestModeAsync(byte modeIndex, CancellationToken token)
        {
            byte bitmap = ModeBitmaps.ElementAtOrDefault(modeIndex);
            var payload = new byte[8];
            payload[3] = bitmap;
            return Task.FromResult(Transmit(TestModeId, payload, token));
        }

        public Task<bool> SendStrengthPresetAsync(int leftLevel, int rightLevel, CancellationToken token)
        {
            byte leftBits = MapLeftStrength(leftLevel);
            byte rightBits = MapRightStrength(rightLevel);
            var payload = new byte[8];
            payload[4] = leftBits;
            payload[5] = rightBits;
            return Task.FromResult(Transmit(StrengthId, payload, token));
        }

        public Task<bool> SendFrequencyStrengthAsync(int frequencyHz, int strength, CancellationToken token)
        {
            byte freqByte = (byte)Math.Max(20, Math.Min(255, frequencyHz));
            byte strengthByte = (byte)Math.Max(0, Math.Min(255, strength));
            var payload = new byte[8];
            payload[0] = freqByte;
            payload[1] = strengthByte;
            return Task.FromResult(Transmit(FrequencyStrengthId, payload, token));
        }

        public async Task<bool> WaitForSeatStatusAsync(Func<Can_Msg, bool>? predicate, CancellationToken token)
        {
            int timeout = Math.Max(1, _config.ListenTimeoutMs);
            int retries = Math.Max(0, _config.ListenRetryCount);

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                token.ThrowIfCancellationRequested();
                bool matched = await Task.Run(() => WaitInternal(StatusFeedbackId, predicate, timeout, token), token);
                if (matched)
                {
                    return true;
                }
            }

            return false;
        }

        private bool WaitInternal(uint expectedId, Func<Can_Msg, bool>? predicate, int timeoutMs, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var messages = Receive(20, timeoutMs);
                foreach (var msg in messages)
                {
                    if (msg.ID != expectedId)
                    {
                        continue;
                    }

                    if (predicate == null || predicate(msg))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private IReadOnlyList<Can_Msg> Receive(int maxMessages, int timeoutMs)
        {
            if (!_opened)
            {
                return Array.Empty<Can_Msg>();
            }

            int msgSize = Marshal.SizeOf<Can_Msg>();
            IntPtr buffer = Marshal.AllocHGlobal(msgSize * maxMessages);
            try
            {
                int count = hcanbusdll.CAN_Receive(_deviceIndex, buffer, (ushort)maxMessages, (uint)timeoutMs);
                if (count <= 0)
                {
                    return Array.Empty<Can_Msg>();
                }

                var list = new List<Can_Msg>(count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr ptr = buffer + i * msgSize;
                    var msg = Marshal.PtrToStructure<Can_Msg>(ptr);
                    msg.data ??= new byte[8];
                    list.Add(msg);
                }

                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private bool Transmit(uint id, byte[] payload, CancellationToken token)
        {
            if (!_opened && !Open())
            {
                return false;
            }

            token.ThrowIfCancellationRequested();

            var msg = new Can_Msg
            {
                ID = id,
                TimeStamp = 0,
                FrameType = 0,
                DataLen = (byte)Math.Min(8, payload.Length),
                data = NormalizePayload(payload),
                ExternFlag = 0,
                RemoteFlag = 0,
                BusSatus = 0,
                ErrSatus = 0,
                TECounter = 0,
                RECounter = 0
            };

            int size = Marshal.SizeOf<Can_Msg>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(msg, buffer, false);
                int result = hcanbusdll.CAN_Transmit(_deviceIndex, buffer, 1, 10);
                if (result != (int)CanStatus.STATUS_OK)
                {
                    _logService.LogError($"发送 CAN 报文失败，ID=0x{id:X}, 返回码 {result}");
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static byte[] NormalizePayload(byte[] payload)
        {
            var data = new byte[8];
            Array.Copy(payload, data, Math.Min(8, payload.Length));
            return data;
        }

        private static byte MapLeftStrength(int level)
        {
            return level switch
            {
                >= 3 => 0x06,
                2 => 0x04,
                1 => 0x02,
                _ => 0x00
            };
        }

        private static byte MapRightStrength(int level)
        {
            return level switch
            {
                >= 3 => 0xC0,
                2 => 0x80,
                1 => 0x40,
                _ => 0x00
            };
        }
    }
}
