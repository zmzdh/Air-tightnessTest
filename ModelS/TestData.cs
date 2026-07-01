// Models/TestData.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AudioActuatorCanTest.Services;

namespace AudioActuatorCanTest.Models
{
    public enum MesIntegrationMode
    {
        HttpPush,
        ModbusServer
    }

    public class AppConfig
    {
        public string PLCIPAddress { get; set; } = "192.168.1.188";
        public int PLCPort { get; set; } = 502;
        public byte PLCStationId { get; set; } = IPLCService.DefaultUnitId;
        public bool AutoSave { get; set; } = true;
        public string LastWorkOrder { get; set; } = string.Empty;
        public string LastProductModel { get; set; } = string.Empty;
        public string MesServerIp { get; set; } = "127.0.0.1";
        public int MesServerPort { get; set; } = 8080;
        public string MesProtocol { get; set; } = "TCP";
        public MesIntegrationMode MesIntegrationMode { get; set; } = MesIntegrationMode.HttpPush;
        public string ModbusServerIp { get; set; } = "0.0.0.0";
        public int ModbusServerPort { get; set; } = 502;

        public SwdConfig Swd { get; set; } = new SwdConfig();
        public CanTestConfig Can { get; set; } = new CanTestConfig();
        public ModbusRtuConfig Modbus { get; set; } = new ModbusRtuConfig();

        /// <summary>
        /// 扫码必选，失败则判定不合格。
        /// </summary>
        public bool RequireBarcodeForTest { get; set; } = true;

        /// <summary>
        /// 参数编辑需管理员权限。
        /// </summary>
        public bool AdminOnlyParameterEdit { get; set; } = true;
    }

    public class SwdConfig
    {
        public string FirmwareDirectory { get; set; } = "Firmware";
        public int EraseTimeoutMs { get; set; } = 5000;
        public int ProgramTimeoutMs { get; set; } = 20000;
        public bool RequireChipDetected { get; set; } = true;
        public bool AllowEraseRetry { get; set; } = false;
        public bool AllowProgramRetry { get; set; } = false;
    }

    public class CanTestConfig
    {
        public int BaudRate { get; set; } = 500000;
        public int LeftChannelIndex { get; set; } = 0;
        public int RightChannelIndex { get; set; } = 1;
        public int ListenTimeoutMs { get; set; } = 5000;
        public int ListenRetryCount { get; set; } = 1;
        public int DefaultFrequencyHz { get; set; } = 80;
        public int DefaultStrengthLow { get; set; } = 100;
        public int DefaultStrengthMid { get; set; } = 150;
        public int DefaultStrengthHigh { get; set; } = 250;
        public int CurrentTargetLowMa { get; set; } = 50;
        public int CurrentTargetMidMa { get; set; } = 100;
        public int CurrentTargetHighMa { get; set; } = 300;
        public int CurrentToleranceMa { get; set; } = 50;
        public double VoltageMin { get; set; } = 13.0;
        public double VoltageMax { get; set; } = 14.0;
        public bool PassengerOnlyShortOpenTest { get; set; } = true;
    }

    public class ModbusRtuConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public string Parity { get; set; } = "N";
        public int StopBits { get; set; } = 1;
        private byte _powerDeviceAddress = 1;
        private byte _switchDeviceAddress = 2;

        public byte PowerDeviceAddress
        {
            get => _powerDeviceAddress == 0 ? DeviceAddress : _powerDeviceAddress;
            set => _powerDeviceAddress = value;
        }

        public byte SwitchDeviceAddress
        {
            get => _switchDeviceAddress == 0 ? DeviceAddress : _switchDeviceAddress;
            set => _switchDeviceAddress = value;
        }

        [Obsolete("保持旧配置文件兼容，优先使用 PowerDeviceAddress/SwitchDeviceAddress。")]
        public byte DeviceAddress { get; set; } = 1;
        public int ReadTimeoutMs { get; set; } = 2000;
        public int VoltageRegister { get; set; } = 40002;
        public int CurrentRegister { get; set; } = 40001;
        public int PowerControlRegister { get; set; } = 40018;
        public int ShortCircuitCoil { get; set; } = 1;
        public int OpenCircuitCoil { get; set; } = 2;
    }

    public class TestStageResult
    {
        public TestStage Stage { get; set; }
        public StepExecutionState State { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string? Message { get; set; }
        public double? PeakCurrent { get; set; }
        public double? AverageCurrent { get; set; }
        public double? MeasuredHeight { get; set; }
        public bool PressureSwitchTriggered { get; set; }
        public string? PressureSwitchAddress { get; set; }
        public double? PressureSwitchDelaySeconds { get; set; }
        public double? CurrentDropDurationSeconds { get; set; }
    }

    public class PLCData
    {
        public ChannelData Channel1 { get; set; } = new ChannelData();
        public ChannelData Channel2 { get; set; } = new ChannelData();
        public SystemData System { get; set; } = new SystemData();
        public DateTime LastUpdate { get; set; }
    }

    public class ChannelData
    {
        // 按摩点 (16个)
        public bool[] MassagePoints { get; set; } = new bool[16];

        // 控制按钮
        public bool StopButton { get; set; }
        public bool FullTestStart { get; set; }
        public bool MassageStart { get; set; }
        public bool SideWingStart { get; set; }
        public bool MassageKey { get; set; }

        // 备用点
        public bool[] SparePoints { get; set; } = new bool[4];

        // 系统控制
        public bool PowerOff { get; set; }
        public bool CylinderOpen { get; set; }
        public bool CylinderClose { get; set; }
        public bool DriverSwitch { get; set; }

        // 指示灯
        public bool FullTestLight { get; set; }
        public bool MassageLight { get; set; }
        public bool SideWingLight { get; set; }
        public bool TestOKLight { get; set; }
        public bool TestNGLight { get; set; }

        // 气袋控制
        public bool UpInflateDownDeflate { get; set; }
        public bool DownInflateUpDeflate { get; set; }
        public bool BothInflate { get; set; }
        public bool BothDeflate { get; set; }

        // 通讯控制
        public bool CommSingleSend { get; set; }
        public bool CommContinuousSend { get; set; }

        // 输出备用
        public bool OutputSpare1 { get; set; }

        // 数据寄存器
        public ushort HeightValue { get; set; }
        public int CurrentRawValue { get; set; }
        public double CurrentValue => CurrentRawValue / 100.0;

        // 485通讯数据区
        public ushort[] CommSendData { get; set; } = new ushort[20];
        public ushort[] CommRecvData { get; set; } = new ushort[20];
    }

    public class SystemData
    {
        public ushort TestStep { get; set; }
        public ushort TestResult { get; set; }
    }

    public class MessageConfig
    {
        private const int MessageLength = 20;

        private ushort[] _powerOnMessage = new ushort[MessageLength];
        private ushort[] _sleepMessage = new ushort[MessageLength];
        private ushort[] _stopMessage = new ushort[MessageLength];
        private ushort[] _massageMessage = new ushort[MessageLength];
        private ushort[] _massageMessage2 = new ushort[MessageLength];
        private ushort[] _readMessage = new ushort[MessageLength];

        public ushort[] PowerOnMessage
        {
            get => _powerOnMessage;
            set => _powerOnMessage = NormalizeMessage(value);
        }

        public ushort[] SleepMessage
        {
            get => _sleepMessage;
            set => _sleepMessage = NormalizeMessage(value);
        }

        public ushort[] StopMessage
        {
            get => _stopMessage;
            set => _stopMessage = NormalizeMessage(value);
        }

        public ushort[] MassageMessage
        {
            get => _massageMessage;
            set => _massageMessage = NormalizeMessage(value);
        }

        public ushort[] MassageMessage2
        {
            get => _massageMessage2;
            set => _massageMessage2 = NormalizeMessage(value);
        }

        public ushort[] ReadMessage
        {
            get => _readMessage;
            set => _readMessage = NormalizeMessage(value);
        }

        private static ushort[] NormalizeMessage(ushort[]? source)
        {
            var normalized = new ushort[MessageLength];
            if (source == null)
                return normalized;

            Array.Copy(source, normalized, Math.Min(source.Length, MessageLength));
            return normalized;
        }
    }

    // PLC地址映射和转换类
    public static class PLCAddressMapper
    {
        public static ChannelAddressMap Channel1Addresses { get; } = new ChannelAddressMap
        {
            MassagePoints = Enumerable.Range(0, 16).ToArray(),
            StopButton = 16,
            FullTestStart = 17,
            MassageStart = 18,
            SideWingStart = 19,
            MassageKey = 61,
            SparePoints = Enumerable.Range(20, 4).ToArray(),
            PowerOff = 48,
            CylinderOpen = 49,
            CylinderClose = 50,
            DriverSwitch = 51,
            FullTestLight = 52,
            MassageLight = 53,
            SideWingLight = 54,
            TestOKLight = 55,
            TestNGLight = 56,
            UpInflateDownDeflate = 57,
            DownInflateUpDeflate = 58,
            BothInflate = 59,
            BothDeflate = 60,
            CommSingleSend = 80,
            CommContinuousSend = 62,
            OutputSpare1 = 63,
            HeightValueD = 10,
            CurrentValueD = 14,
            CommSendStart = 160,
            CommRecvStart = 260
        };

        public static ChannelAddressMap Channel2Addresses { get; } = new ChannelAddressMap
        {
            MassagePoints = Enumerable.Range(24, 16).ToArray(),
            StopButton = 40,
            FullTestStart = 41,
            MassageStart = 42,
            SideWingStart = 43,
            MassageKey = 77,
            SparePoints = Enumerable.Range(44, 4).ToArray(),
            PowerOff = 64,
            CylinderOpen = 65,
            CylinderClose = 66,
            DriverSwitch = 67,
            FullTestLight = 68,
            MassageLight = 69,
            SideWingLight = 70,
            TestOKLight = 71,
            TestNGLight = 72,
            UpInflateDownDeflate = 73,
            DownInflateUpDeflate = 74,
            BothInflate = 75,
            BothDeflate = 76,
            CommSingleSend = 80,
            CommContinuousSend = 86,
            OutputSpare1 = 87,
            HeightValueD = 12,
            CurrentValueD = 16,
            CommSendStart = 180,
            CommRecvStart = 280
        };

        // 添加缺失的方法：MapMRegistersToStructuredData
        public static void MapMRegistersToStructuredData(bool[] mRegisters, PLCData plcData)
        {
            if (mRegisters == null)
                throw new ArgumentNullException(nameof(mRegisters));
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));

            MapChannelMRegisters(mRegisters, Channel1Addresses, plcData.Channel1);
            MapChannelMRegisters(mRegisters, Channel2Addresses, plcData.Channel2);
        }

        private static void MapChannelMRegisters(bool[] mRegisters, ChannelAddressMap map, ChannelData channel)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            int maxAddress = map.GetAllBitAddresses().Max();
            if (mRegisters.Length <= maxAddress)
                throw new ArgumentException("mRegisters数组长度不足");

            for (int i = 0; i < map.MassagePoints.Length && i < channel.MassagePoints.Length; i++)
            {
                channel.MassagePoints[i] = mRegisters[map.MassagePoints[i]];
            }

            channel.StopButton = mRegisters[map.StopButton];
            channel.FullTestStart = mRegisters[map.FullTestStart];
            channel.MassageStart = mRegisters[map.MassageStart];
            channel.SideWingStart = mRegisters[map.SideWingStart];
            channel.MassageKey = mRegisters[map.MassageKey];

            for (int i = 0; i < map.SparePoints.Length && i < channel.SparePoints.Length; i++)
            {
                channel.SparePoints[i] = mRegisters[map.SparePoints[i]];
            }

            channel.PowerOff = mRegisters[map.PowerOff];
            channel.CylinderOpen = mRegisters[map.CylinderOpen];
            channel.CylinderClose = mRegisters[map.CylinderClose];
            channel.DriverSwitch = mRegisters[map.DriverSwitch];

            channel.FullTestLight = mRegisters[map.FullTestLight];
            channel.MassageLight = mRegisters[map.MassageLight];
            channel.SideWingLight = mRegisters[map.SideWingLight];
            channel.TestOKLight = mRegisters[map.TestOKLight];
            channel.TestNGLight = mRegisters[map.TestNGLight];

            channel.UpInflateDownDeflate = mRegisters[map.UpInflateDownDeflate];
            channel.DownInflateUpDeflate = mRegisters[map.DownInflateUpDeflate];
            channel.BothInflate = mRegisters[map.BothInflate];
            channel.BothDeflate = mRegisters[map.BothDeflate];

            channel.CommSingleSend = mRegisters[map.CommSingleSend];
            channel.CommContinuousSend = mRegisters[map.CommContinuousSend];
            channel.OutputSpare1 = mRegisters[map.OutputSpare1];
        }

        public static void MapStructuredDataToMRegisters(PLCData plcData, bool[] mRegisters)
        {
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));
            if (mRegisters == null)
                throw new ArgumentNullException(nameof(mRegisters));

            MapChannelToMRegisters(plcData.Channel1, Channel1Addresses, mRegisters);
            MapChannelToMRegisters(plcData.Channel2, Channel2Addresses, mRegisters);
        }

        private static void MapChannelToMRegisters(ChannelData channel, ChannelAddressMap map, bool[] mRegisters)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            foreach (int address in map.GetAllBitAddresses())
            {
                if (address >= mRegisters.Length)
                    throw new ArgumentException("mRegisters数组长度不足");
            }

            for (int i = 0; i < map.MassagePoints.Length && i < channel.MassagePoints.Length; i++)
            {
                mRegisters[map.MassagePoints[i]] = channel.MassagePoints[i];
            }

            mRegisters[map.StopButton] = channel.StopButton;
            mRegisters[map.FullTestStart] = channel.FullTestStart;
            mRegisters[map.MassageStart] = channel.MassageStart;
            mRegisters[map.SideWingStart] = channel.SideWingStart;
            mRegisters[map.MassageKey] = channel.MassageKey;

            for (int i = 0; i < map.SparePoints.Length && i < channel.SparePoints.Length; i++)
            {
                mRegisters[map.SparePoints[i]] = channel.SparePoints[i];
            }

            mRegisters[map.PowerOff] = channel.PowerOff;
            mRegisters[map.CylinderOpen] = channel.CylinderOpen;
            mRegisters[map.CylinderClose] = channel.CylinderClose;
            mRegisters[map.DriverSwitch] = channel.DriverSwitch;

            mRegisters[map.FullTestLight] = channel.FullTestLight;
            mRegisters[map.MassageLight] = channel.MassageLight;
            mRegisters[map.SideWingLight] = channel.SideWingLight;
            mRegisters[map.TestOKLight] = channel.TestOKLight;
            mRegisters[map.TestNGLight] = channel.TestNGLight;

            mRegisters[map.UpInflateDownDeflate] = channel.UpInflateDownDeflate;
            mRegisters[map.DownInflateUpDeflate] = channel.DownInflateUpDeflate;
            mRegisters[map.BothInflate] = channel.BothInflate;
            mRegisters[map.BothDeflate] = channel.BothDeflate;

            mRegisters[map.CommSingleSend] = channel.CommSingleSend;
            mRegisters[map.CommContinuousSend] = channel.CommContinuousSend;
            mRegisters[map.OutputSpare1] = channel.OutputSpare1;
        }

        // 添加缺失的方法：MapDRegistersToStructuredData
        public static void MapDRegistersToStructuredData(ushort[] dRegisters, PLCData plcData)
        {
            if (dRegisters == null)
                throw new ArgumentNullException(nameof(dRegisters));
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));

            MapChannelDRegisters(dRegisters, plcData.Channel1, Channel1Addresses, "Channel1");
            MapChannelDRegisters(dRegisters, plcData.Channel2, Channel2Addresses, "Channel2");

            if (TryReadRegister(dRegisters, 100, out var testStep, "System.TestStep"))
            {
                plcData.System.TestStep = testStep;
            }

            if (TryReadRegister(dRegisters, 101, out var testResult, "System.TestResult"))
            {
                plcData.System.TestResult = testResult;
            }
        }

        private static void MapChannelDRegisters(ushort[] dRegisters, ChannelData channel, ChannelAddressMap map, string channelName)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            if (TryReadRegister(dRegisters, map.HeightValueD, out var heightValue, $"{channelName}.HeightValue"))
            {
                channel.HeightValue = heightValue;
            }

            if (TryReadRegister32(dRegisters, map.CurrentValueD, out var currentRaw, $"{channelName}.CurrentValue"))
            {
                channel.CurrentRawValue = currentRaw;
            }

            for (int i = 0; i < channel.CommSendData.Length; i++)
            {
                int address = map.CommSendStart + i;
                if (TryReadRegister(dRegisters, address, out var sendValue, $"{channelName}.CommSendData[{i}]"))
                {
                    channel.CommSendData[i] = sendValue;
                }
                else
                {
                    break;
                }
            }

            for (int i = 0; i < channel.CommRecvData.Length; i++)
            {
                int address = map.CommRecvStart + i;
                if (TryReadRegister(dRegisters, address, out var recvValue, $"{channelName}.CommRecvData[{i}]"))
                {
                    channel.CommRecvData[i] = recvValue;
                }
                else
                {
                    break;
                }
            }
        }

        private static bool TryReadRegister(ushort[] dRegisters, int index, out ushort value, string description)
        {
            if (index >= 0 && index < dRegisters.Length)
            {
                value = dRegisters[index];
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadRegister32(ushort[] dRegisters, int startIndex, out int value, string description)
        {
            value = 0;

            if (startIndex < 0 || startIndex + 1 >= dRegisters.Length)
            {
                return false;
            }

            uint low = dRegisters[startIndex];
            uint high = dRegisters[startIndex + 1];
            uint combined = (high << 16) | low;
            value = unchecked((int)combined);
            return true;
        }

        public static void MapStructuredDataToDRegisters(PLCData plcData, ushort[] dRegisters)
        {
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));
            if (dRegisters == null)
                throw new ArgumentNullException(nameof(dRegisters));

            MapChannelToDRegisters(plcData.Channel1, Channel1Addresses, dRegisters);
            MapChannelToDRegisters(plcData.Channel2, Channel2Addresses, dRegisters);

            dRegisters[100] = plcData.System.TestStep;
            dRegisters[101] = plcData.System.TestResult;
        }

        private static void MapChannelToDRegisters(ChannelData channel, ChannelAddressMap map, ushort[] dRegisters)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            dRegisters[map.HeightValueD] = channel.HeightValue;
            WriteRegister32(dRegisters, map.CurrentValueD, channel.CurrentRawValue);

            for (int i = 0; i < channel.CommSendData.Length; i++)
            {
                dRegisters[map.CommSendStart + i] = channel.CommSendData[i];
            }

            for (int i = 0; i < channel.CommRecvData.Length; i++)
            {
                dRegisters[map.CommRecvStart + i] = channel.CommRecvData[i];
            }
        }

        private static void WriteRegister32(ushort[] dRegisters, int startIndex, int value)
        {
            if (startIndex < 0 || startIndex + 1 >= dRegisters.Length)
            {
                return;
            }

            unchecked
            {
                dRegisters[startIndex] = (ushort)(value & 0xFFFF);
                dRegisters[startIndex + 1] = (ushort)((value >> 16) & 0xFFFF);
            }
        }

        // 添加缺失的方法：TryUpdateChannelBit
        public static bool TryUpdateChannelBit(ChannelData channel, ChannelAddressMap map, int address, bool value)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));
            if (map == null)
                throw new ArgumentNullException(nameof(map));

            for (int i = 0; i < map.MassagePoints.Length && i < channel.MassagePoints.Length; i++)
            {
                if (map.MassagePoints[i] == address)
                {
                    channel.MassagePoints[i] = value;
                    return true;
                }
            }

            if (map.StopButton == address) { channel.StopButton = value; return true; }
            if (map.FullTestStart == address) { channel.FullTestStart = value; return true; }
            if (map.MassageStart == address) { channel.MassageStart = value; return true; }
            if (map.SideWingStart == address) { channel.SideWingStart = value; return true; }
            if (map.MassageKey == address) { channel.MassageKey = value; return true; }

            for (int i = 0; i < map.SparePoints.Length && i < channel.SparePoints.Length; i++)
            {
                if (map.SparePoints[i] == address)
                {
                    channel.SparePoints[i] = value;
                    return true;
                }
            }

            if (map.PowerOff == address) { channel.PowerOff = value; return true; }
            if (map.CylinderOpen == address) { channel.CylinderOpen = value; return true; }
            if (map.CylinderClose == address) { channel.CylinderClose = value; return true; }
            if (map.DriverSwitch == address) { channel.DriverSwitch = value; return true; }
            if (map.FullTestLight == address) { channel.FullTestLight = value; return true; }
            if (map.MassageLight == address) { channel.MassageLight = value; return true; }
            if (map.SideWingLight == address) { channel.SideWingLight = value; return true; }
            if (map.TestOKLight == address) { channel.TestOKLight = value; return true; }
            if (map.TestNGLight == address) { channel.TestNGLight = value; return true; }
            if (map.UpInflateDownDeflate == address) { channel.UpInflateDownDeflate = value; return true; }
            if (map.DownInflateUpDeflate == address) { channel.DownInflateUpDeflate = value; return true; }
            if (map.BothInflate == address) { channel.BothInflate = value; return true; }
            if (map.BothDeflate == address) { channel.BothDeflate = value; return true; }
            if (map.CommSingleSend == address) { channel.CommSingleSend = value; return true; }
            if (map.CommContinuousSend == address) { channel.CommContinuousSend = value; return true; }
            if (map.OutputSpare1 == address) { channel.OutputSpare1 = value; return true; }

            return false;
        }

        // 添加缺失的方法：TryUpdateChannelWord
        public static bool TryUpdateChannelWord(ChannelData channel, ChannelAddressMap map, int address, ushort value)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));
            if (map == null)
                throw new ArgumentNullException(nameof(map));

            if (map.HeightValueD == address)
            {
                channel.HeightValue = value;
                return true;
            }

            if (map.CurrentValueD == address)
            {
                int existingHigh = unchecked((int)((uint)channel.CurrentRawValue & 0xFFFF0000));
                channel.CurrentRawValue = existingHigh | value;
                return true;
            }

            if (map.CurrentValueD + 1 == address)
            {
                uint low = unchecked((uint)channel.CurrentRawValue & 0xFFFF);
                uint combined = ((uint)value << 16) | low;
                channel.CurrentRawValue = unchecked((int)combined);
                return true;
            }

            if (address >= map.CommSendStart && address < map.CommSendStart + channel.CommSendData.Length)
            {
                channel.CommSendData[address - map.CommSendStart] = value;
                return true;
            }

            if (address >= map.CommRecvStart && address < map.CommRecvStart + channel.CommRecvData.Length)
            {
                channel.CommRecvData[address - map.CommRecvStart] = value;
                return true;
            }

            return false;
        }
    }

    public class ChannelAddressMap
    {
        public int[] MassagePoints { get; init; } = Array.Empty<int>();
        public int StopButton { get; init; }
        public int FullTestStart { get; init; }
        public int MassageStart { get; init; }
        public int SideWingStart { get; init; }
        public int MassageKey { get; init; }
        public int[] SparePoints { get; init; } = Array.Empty<int>();
        public int PowerOff { get; init; }
        public int CylinderOpen { get; init; }
        public int CylinderClose { get; init; }
        public int DriverSwitch { get; init; }
        public int FullTestLight { get; init; }
        public int MassageLight { get; init; }
        public int SideWingLight { get; init; }
        public int TestOKLight { get; init; }
        public int TestNGLight { get; init; }
        public int UpInflateDownDeflate { get; init; }
        public int DownInflateUpDeflate { get; init; }
        public int BothInflate { get; init; }
        public int BothDeflate { get; init; }
        public int CommSingleSend { get; init; }
        public int CommContinuousSend { get; init; }
        public int OutputSpare1 { get; init; }
        public int HeightValueD { get; init; }
        public int CurrentValueD { get; init; }
        public int CommSendStart { get; init; }
        public int CommRecvStart { get; init; }

        public IEnumerable<int> GetAllBitAddresses()
        {
            foreach (var addr in MassagePoints)
                yield return addr;
                yield return StopButton;
                yield return FullTestStart;
                yield return MassageStart;
                yield return SideWingStart;
                yield return MassageKey;
            foreach (var addr in SparePoints)
                yield return addr;
                yield return PowerOff;
                yield return CylinderOpen;
                yield return CylinderClose;
                yield return DriverSwitch;
                yield return FullTestLight;
                yield return MassageLight;
                yield return SideWingLight;
                yield return TestOKLight;
                yield return TestNGLight;
                yield return UpInflateDownDeflate;
                yield return DownInflateUpDeflate;
                yield return BothInflate;
                yield return BothDeflate;
                yield return CommSingleSend;
                yield return CommContinuousSend;
                yield return OutputSpare1;
        }
    }

    public enum TestResult
    {
        None = 0,
        Testing = 1,
        Pass = 2,
        Fail = 3,
        Aborted = 4
    }

    public enum TestStage
    {
        Standby = 0,
        ScanBarcode = 1,
        StartTest = 2,
        SleepTest = 3,
        StaticCurrentTest = 4,
        StatusMessageCheck = 5,
        LumbarTest = 6,
        MassageTest = 7,
        MasterSlaveDecision = 8,
        MasterModeMassage = 9,
        Completed = 10,
        Aborted = 11
    }

    public enum StepExecutionState
    {
        Pending,
        Running,
        Passed,
        Failed,
        Skipped
    }

    public enum LumbarActionType
    {
        UpInflateDownDeflate = 1,
        DownInflateUpDeflate = 2,
        SimultaneousInflate = 3,
        SimultaneousDeflate = 4,
        FrameHeaderSwitch = 5
    }

    public static class LumbarActionExtensions
    {
        public static string ToDisplayName(this LumbarActionType action)
        {
            return action switch
            {
                LumbarActionType.UpInflateDownDeflate => "上充下放",
                LumbarActionType.DownInflateUpDeflate => "下充上放",
                LumbarActionType.SimultaneousInflate => "同时充气",
                LumbarActionType.SimultaneousDeflate => "同时放气",
                LumbarActionType.FrameHeaderSwitch => "帧头切换",
                _ => action.ToString()
            };
        }

        public static string FormatWithOrder(this LumbarActionType action, int order)
        {
            string displayName = action.ToDisplayName();
            return string.IsNullOrWhiteSpace(displayName)
                ? $"腰托动作{order}"
                : $"腰托动作{order}({displayName})";
        }
    }

    public class ProductModel : INotifyPropertyChanged
    {
        private string _modelName = string.Empty;
        private string _description = string.Empty;
        private string _imagePath = string.Empty;
        private ChannelConfig _channel1Config = new() { ChannelName = "左通道" };
        private ChannelConfig _channel2Config = new() { ChannelName = "右通道" };
        private TestProcessConfig _processConfig = new();

        public string ModelName
        {
            get => _modelName;
            set
            {
                string newValue = value ?? string.Empty;
                if (_modelName != newValue)
                {
                    _modelName = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public string Description
        {
            get => _description;
            set
            {
                string newValue = value ?? string.Empty;
                if (_description != newValue)
                {
                    _description = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                string newValue = value ?? string.Empty;
                if (_imagePath != newValue)
                {
                    _imagePath = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public ChannelConfig Channel1Config
        {
            get => _channel1Config;
            set
            {
                var newValue = value ?? _channel1Config;

                if (!ReferenceEquals(_channel1Config, newValue))
                {
                    _channel1Config = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public ChannelConfig Channel2Config
        {
            get => _channel2Config;
            set
            {
                var newValue = value ?? _channel2Config;

                if (!ReferenceEquals(_channel2Config, newValue))
                {
                    _channel2Config = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public TestProcessConfig ProcessConfig
        {
            get => _processConfig;
            set
            {
                var newValue = value ?? _processConfig;

                if (!ReferenceEquals(_processConfig, newValue))
                {
                    _processConfig = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class LumbarTestConfig
    {
        public int Order { get; set; }
        public LumbarActionType Action { get; set; }
        public double TargetHeight { get; set; }
        public int TargetTime { get; set; }
        public bool Enabled { get; set; } = true;
        public string MRegisterAddress { get; set; } = "M0";
        public ushort[] SendMessage { get; set; } = new ushort[20];
    }

    public class MassageConfig
    {
        public int Point { get; set; }

#pragma warning disable CS0618 // 使用兼容旧版本配置的属性
        [Obsolete("按摩动作顺序已弃用，使用点位定义执行顺序。")]
        public int Order { get; set; }

        [Obsolete("单动作最小时长已弃用，改为在 MassageTestSettings 中配置共享参数。")]
        public int MinDuration { get; set; } = 1000;

        [Obsolete("单动作最大时长已弃用，改为在 MassageTestSettings 中配置共享参数。")]
        public int MaxDuration { get; set; } = 5000;

        [Obsolete("峰值电流下限已迁移到共享参数配置。")]
        public double PeakCurrentMin { get; set; } = 100;

        [Obsolete("峰值电流上限已迁移到共享参数配置。")]
        public double PeakCurrentMax { get; set; } = 2000;

        [Obsolete("平均电流下限已迁移到共享参数配置。")]
        public double AverageCurrentMin { get; set; } = 100;

        [Obsolete("平均电流上限已迁移到共享参数配置。")]
        public double AverageCurrentMax { get; set; } = 1500;
#pragma warning restore CS0618

        public string PressureSwitchAddress { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public class MassageTestSettings
    {
        private int _totalDuration = 60000;
        private int _highLevelDurationMin = 500;
        private int _highLevelDurationMax = 5000;

        public int TotalDuration
        {
            get => _totalDuration;
            set => _totalDuration = Math.Max(0, value);
        }

        public int HighLevelDurationMin
        {
            get => _highLevelDurationMin;
            set => _highLevelDurationMin = Math.Max(0, value);
        }

        public int HighLevelDurationMax
        {
            get => _highLevelDurationMax;
            set => _highLevelDurationMax = Math.Max(0, value);
        }

        public double PeakCurrentMin { get; set; } = 100;
        public double PeakCurrentMax { get; set; } = 2000;
        public double AverageCurrentMin { get; set; } = 100;
        public double AverageCurrentMax { get; set; } = 1500;
    }

    public class ChannelConfig
    {
        public string ChannelName { get; set; } = "通道1";

        public double StaticCurrentMin { get; set; } = 0;
        public double StaticCurrentMax { get; set; } = 100;
        public double WorkCurrentMin { get; set; } = 100;
        public double WorkCurrentMax { get; set; } = 2000;
        public double CurrentOverLimit { get; set; } = 2500;

        public double SleepCurrentThreshold { get; set; } = 0.5;
        public int SleepTestTimeout { get; set; } = 5000;

        public int PowerOffDurationMin { get; set; } = 5000;
        public int PowerOffDurationMax { get; set; } = 30000;

        public List<LumbarTestConfig> LumbarTestConfigs { get; set; } = new List<LumbarTestConfig>();
        public List<MassageConfig> MassageConfigs { get; set; } = new List<MassageConfig>();

        public MassageTestSettings MassageTestSettings { get; set; } = new MassageTestSettings();

        public ushort[] StatusMessagePreset { get; set; } = new ushort[20];

        public MessageConfig MessageConfig { get; set; } = new MessageConfig();
    }

    public class TestProcessConfig
    {
        private int _modeSwitchPowerOffDuration = 5000;

        public bool EnableBarcodeCheck { get; set; } = true;
        public int MaxTestCount { get; set; } = 3;
        public bool EnableCurrentMonitoring { get; set; } = true;
        public bool RecordCurrentBeforeStart { get; set; } = false;
        public bool CheckSameModel { get; set; } = true;
        public bool PromptOnDuplicateBarcode { get; set; } = true;
        public bool MeasureSleepCurrent { get; set; } = true;
        public bool MeasureStaticCurrent { get; set; } = true;
        public bool EnableLumbarTest { get; set; } = true;
        public bool EnableMassageTest { get; set; } = true;

        public int ModeSwitchPowerOffDuration
        {
            get => _modeSwitchPowerOffDuration;
            set => _modeSwitchPowerOffDuration = Math.Max(0, value);
        }
    }

    public class TestRecord
    {
        public int Id { get; set; }
        public DateTime TestTime { get; set; }
        public string WorkOrder { get; set; }
        public string ProductModel { get; set; }
        public string ProductCode { get; set; }
        public string Operator { get; set; }
        public int Channel { get; set; }
        public int TestCount { get; set; }
        public double TestVoltage { get; set; }
        public int DuplicateCount { get; set; }

        public double? SleepCurrent { get; set; }
        public double? StaticCurrent { get; set; }

        public List<LumbarActionResult> LumbarResults { get; set; } = new List<LumbarActionResult>();
        public List<MassagePointResult> MassageResults { get; set; } = new List<MassagePointResult>();
        public List<TestStageResult> StageResults { get; set; } = new List<TestStageResult>();
        public List<CurrentMeasurement> CurrentTimeline { get; set; } = new List<CurrentMeasurement>();

        public List<double> LumbarCurrents { get; set; } = new List<double>();
        public List<double> MassageCurrents { get; set; } = new List<double>();

        public double? LumbarAverageCurrent { get; set; }
        public double? LumbarMaxCurrent { get; set; }
        public double? MassageAverageCurrent { get; set; }
        public double? MassageMaxCurrent { get; set; }

        public TestResult Result { get; set; }
        public string FailReason { get; set; }
        public double TestDuration { get; set; }
        public bool WasAborted { get; set; }

        public List<SummaryLogEntry> SummaryLogs { get; set; } = new List<SummaryLogEntry>();
    }

    public class SummaryLogEntry
    {
        public string StepName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int DurationMs { get; set; }
        public int ResultCode { get; set; }
        public string? Message { get; set; }
    }

    public class LumbarActionResult
    {
        public int Order { get; set; }
        public LumbarActionType Action { get; set; }
        public double TargetHeight { get; set; }
        public double? ActualHeight { get; set; }
        public int TargetTime { get; set; }
        public int? ActualTime { get; set; }
        public double? PeakCurrent { get; set; }
        public double? AverageCurrent { get; set; }
        public bool Passed { get; set; }
        public string? Message { get; set; }
    }

    public class MassagePointResult
    {
        public int Point { get; set; }
        public int Order { get; set; }
        public bool ValveOpened { get; set; }
        public bool PressureSwitchTriggered { get; set; }
        public int? Duration { get; set; }
        public double? PeakCurrent { get; set; }
        public double? AverageCurrent { get; set; }
        public int TriggerCount { get; set; }
        public double? TotalHighDuration { get; set; }
        public double? MaxHighDuration { get; set; }
        public double? MinHighDuration { get; set; }
        public double? LastHighDuration { get; set; }
        public bool Passed { get; set; }
        public string Message { get; set; }
    }

    public class CurrentMeasurement
    {
        public DateTime Timestamp { get; set; }
        public string Stage { get; set; }
        public double CurrentValue { get; set; }
    }

    public class TestStageChangedEventArgs : EventArgs
    {
        public int Channel { get; set; }
        public TestStage Stage { get; set; }
        public StepExecutionState State { get; set; }
        public string? Message { get; set; }
    }

    public class ChannelTestResultEventArgs : EventArgs
    {
        public int Channel { get; set; }
        public bool IsOk { get; set; }
    }

    public class TestMessageEventArgs : EventArgs
    {
        public TestMessageEventArgs(string message, int? channel = null)
        {
            Message = message ?? string.Empty;
            Channel = channel;
        }

        public string Message { get; }
        public int? Channel { get; }
    }

    public class TestStartOptions
    {
        public int Channel { get; set; }
        public ProductModel Model { get; set; }
        public string WorkOrder { get; set; }
        public string Barcode { get; set; }
        public string Operator { get; set; }
        public bool ContinueOnDuplicate { get; set; }
    }

    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginAt { get; set; }
        public int LoginCount { get; set; }
    }
}
