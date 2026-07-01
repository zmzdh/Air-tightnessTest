// Models/TestData.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using LumbarMassageTest.Services;
using Newtonsoft.Json;

namespace LumbarMassageTest.Models
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
        public ushort PlcDiscreteInputCount { get; set; } = 256;
        public ushort PlcCoilCount { get; set; } = 128;
        public bool AutoSave { get; set; } = true;
        public string LastWorkOrder { get; set; } = string.Empty;
        public string LastProductModel { get; set; } = string.Empty;
        public int TargetProduction { get; set; }
        public string DailyProductionDate { get; set; } = string.Empty;
        public int DailyTestCount { get; set; }
        public int DailyPassCount { get; set; }
        public int DailyFailCount { get; set; }
        public List<ChannelDailyProduction> DailyChannelProductions { get; set; } = new();
        public string MesServerIp { get; set; } = "127.0.0.1";
        public int MesServerPort { get; set; } = 8080;
        public string MesProtocol { get; set; } = "TCP";
        public MesIntegrationMode MesIntegrationMode { get; set; } = MesIntegrationMode.HttpPush;
        public string ModbusServerIp { get; set; } = "0.0.0.0";
        public int ModbusServerPort { get; set; } = 502;
        public SerialPortConfig SerialDevice1 { get; set; } = SerialPortConfig.CreateDefaultDevice1();
        public SerialPortConfig SerialDevice2 { get; set; } = SerialPortConfig.CreateDefaultDevice2();
        public int ChannelCount { get; set; } = 4;
        public byte PressureModuleStationId { get; set; } = 1;
        public int PressureInputStartAddress { get; set; } = 40097;
        public int PressureOutputStartAddress { get; set; } = 40023;
    }

    public class ChannelDailyProduction
    {
        public int Channel { get; set; }
        public int TestCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
    }

    public class SerialPortConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 19200;
        public int DataBits { get; set; } = 8;
        public string Parity { get; set; } = "None";
        public string StopBits { get; set; } = "One";

        public static SerialPortConfig CreateDefaultDevice1()
        {
            return new SerialPortConfig
            {
                PortName = "COM1",
                BaudRate = 19200,
                DataBits = 8,
                Parity = "None",
                StopBits = "One"
            };
        }

        public static SerialPortConfig CreateDefaultDevice2()
        {
            return new SerialPortConfig
            {
                PortName = "COM2",
                BaudRate = 19200,
                DataBits = 8,
                Parity = "None",
                StopBits = "One"
            };
        }
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
        public bool HeightSwitchTriggered { get; set; }
        public string? HeightSwitchAddress { get; set; }
        public double? HeightSwitchDelaySeconds { get; set; }
        public double? CurrentDropDurationSeconds { get; set; }
        public double? PressureStart { get; set; }
        public double? PressureEnd { get; set; }
        public double? PressureDrop { get; set; }
        public string? PressureUnit { get; set; }
    }

    public class PLCData
    {
        public ChannelData Channel1 { get; set; } = new ChannelData();
        public ChannelData Channel2 { get; set; } = new ChannelData();
        public ChannelData Channel3 { get; set; } = new ChannelData();
        public ChannelData Channel4 { get; set; } = new ChannelData();
        public SystemData System { get; set; } = new SystemData();
        public DateTime LastUpdate { get; set; }
    }

    public class ChannelData
    {
        // 鎸夋懇鐐?(32涓?
        public bool[] MassagePoints { get; set; } = new bool[32];

        // 鎺у埗鎸夐挳
        public bool StopButton { get; set; }
        public bool FullTestStart { get; set; }
        public bool MassageStart { get; set; }
        public bool SideWingStart { get; set; }
        public bool MassageKey { get; set; }

        // 澶囩敤鐐?
        public bool[] SparePoints { get; set; } = new bool[4];

        // 绯荤粺鎺у埗
        public bool PowerOff { get; set; }
        public bool CylinderOpen { get; set; }
        public bool CylinderClose { get; set; }
        public bool DriverSwitch { get; set; }

        // 鎸囩ず鐏?
        public bool FullTestLight { get; set; }
        public bool MassageLight { get; set; }
        public bool SideWingLight { get; set; }
        public bool TestOKLight { get; set; }
        public bool TestNGLight { get; set; }

        // 姘旇鎺у埗
        public bool UpInflateDownDeflate { get; set; }
        public bool DownInflateUpDeflate { get; set; }
        public bool BothInflate { get; set; }
        public bool BothDeflate { get; set; }

        // 閫氳鎺у埗
        public bool CommSingleSend { get; set; }
        public bool CommContinuousSend { get; set; }

        // 杈撳嚭澶囩敤
        public bool OutputSpare1 { get; set; }
        public bool OutputSpare2 { get; set; }

        public bool AirLeakStartButton { get; set; }
        public bool HighPressureInletValve { get; set; }
        public bool HighPressureExhaustValve { get; set; }
        public bool LowPressureInletValve { get; set; }
        public bool LowPressureExhaustValve { get; set; }

        // 鏁版嵁瀵勫瓨鍣?
        public int HeightRawValue { get; set; }
        public int CurrentRawValue { get; set; }
        public double HeightValue => HeightRawValue / 100.0;
        public double CurrentValue => CurrentRawValue / 100.0;

        // 485閫氳鏁版嵁鍖?
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

    public enum ModbusBitType
    {
        Coil,
        DiscreteInput
    }

    public readonly struct ModbusBitAddress
    {
        public ModbusBitAddress(ModbusBitType type, int index, int oneBasedAddress)
        {
            Type = type;
            Index = index;
            OneBasedAddress = oneBasedAddress;
        }

        public ModbusBitType Type { get; }
        public int Index { get; }
        public int OneBasedAddress { get; }
    }

    public static class ModbusAddressHelper
    {
        public static bool TryParseBitAddress(string? address, out ModbusBitAddress result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            string trimmed = address.Trim();
            if (trimmed.Length < 3)
            {
                return false;
            }

            string prefix = trimmed.Substring(0, 2);
            if (!prefix.Equals("0x", StringComparison.OrdinalIgnoreCase) &&
                !prefix.Equals("1x", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string numericPart = trimmed.Substring(2).Trim();
            if (string.IsNullOrWhiteSpace(numericPart))
            {
                return false;
            }

            if (!numericPart.All(Uri.IsHexDigit))
            {
                return false;
            }

            if (!int.TryParse(numericPart, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int numericValue))
            {
                return false;
            }

            if (numericValue < 0)
            {
                return false;
            }

            var type = prefix.Equals("0x", StringComparison.OrdinalIgnoreCase)
                ? ModbusBitType.Coil
                : ModbusBitType.DiscreteInput;

            result = new ModbusBitAddress(type, numericValue, numericValue);
            return true;
        }

        public static string? NormalizeBitAddressToken(string? address)
        {
            if (!TryParseBitAddress(address, out var parsed))
            {
                return null;
            }

            string prefix = parsed.Type == ModbusBitType.Coil ? "0x" : "1x";
            return $"{prefix}{parsed.OneBasedAddress:X4}";
        }

        public static IEnumerable<string> ParseAddressList(string? addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses))
            {
                yield break;
            }

            var separators = new[] { ',', ';', '|', ' ' };
            foreach (var part in addresses.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    yield return trimmed;
                }
            }
        }
    }

    // PLC鍦板潃鏄犲皠鍜岃浆鎹㈢被
    public static class PLCAddressMapper
    {
        private const int BitsPerChannel = 64;
        private const int DataBlockSize = 60;
        public const int SystemTestStepAddress = 300;
        public const int SystemTestResultAddress = 301;

        public static int RequiredBitRegisterCount => BitsPerChannel * 4;
        public static int RequiredDRegisterCount => SystemTestResultAddress + 1;

        public static ChannelAddressMap Channel1Addresses { get; } = BuildChannelAddressMap(0, 0);
        public static ChannelAddressMap Channel2Addresses { get; } = BuildChannelAddressMap(BitsPerChannel, DataBlockSize);
        public static ChannelAddressMap Channel3Addresses { get; } = BuildChannelAddressMap(BitsPerChannel * 2, DataBlockSize * 2);
        public static ChannelAddressMap Channel4Addresses { get; } = BuildChannelAddressMap(BitsPerChannel * 3, DataBlockSize * 3);

        private static ChannelAddressMap BuildChannelAddressMap(int bitBase, int dataBase)
        {
            return new ChannelAddressMap
            {
                MassagePoints = Enumerable.Range(bitBase, 16).ToArray(),
                StopButton = bitBase + 16,
                FullTestStart = bitBase + 17,
                MassageStart = bitBase + 18,
                SideWingStart = bitBase + 19,
                MassageKey = bitBase + 20,
                SparePoints = Enumerable.Range(bitBase + 21, 4).ToArray(),
                PowerOff = bitBase + 25,
                CylinderOpen = bitBase + 26,
                CylinderClose = bitBase + 27,
                DriverSwitch = bitBase + 28,
                FullTestLight = bitBase + 29,
                MassageLight = bitBase + 30,
                SideWingLight = bitBase + 31,
                TestOKLight = bitBase + 32,
                TestNGLight = bitBase + 33,
                UpInflateDownDeflate = bitBase + 34,
                DownInflateUpDeflate = bitBase + 35,
                BothInflate = bitBase + 36,
                BothDeflate = bitBase + 37,
                CommSingleSend = bitBase + 38,
                CommContinuousSend = bitBase + 39,
                OutputSpare1 = bitBase + 40,
                OutputSpare2 = bitBase + 41,
                HeightValueD = dataBase,
                CurrentValueD = dataBase + 2,
                CommSendStart = dataBase + 10,
                CommRecvStart = dataBase + 30
            };
        }

        // 娣诲姞缂哄け鐨勬柟娉曪細MapMRegistersToStructuredData
        public static void MapMRegistersToStructuredData(bool[] mRegisters, PLCData plcData)
        {
            if (mRegisters == null)
                throw new ArgumentNullException(nameof(mRegisters));
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));

            MapChannelMRegisters(mRegisters, Channel1Addresses, plcData.Channel1);
            MapChannelMRegisters(mRegisters, Channel2Addresses, plcData.Channel2);
            MapChannelMRegisters(mRegisters, Channel3Addresses, plcData.Channel3);
            MapChannelMRegisters(mRegisters, Channel4Addresses, plcData.Channel4);
        }

        public static void MapModbusBitsToStructuredData(bool[] inputBits, bool[] coilBits, PLCData plcData, ProductModel? model)
        {
            if (inputBits == null)
            {
                throw new ArgumentNullException(nameof(inputBits));
            }

            if (coilBits == null)
            {
                throw new ArgumentNullException(nameof(coilBits));
            }

            if (plcData == null)
            {
                throw new ArgumentNullException(nameof(plcData));
            }

            MapChannelModbusBits(inputBits, coilBits, plcData.Channel1, model?.Channel1Config);
            MapChannelModbusBits(inputBits, coilBits, plcData.Channel2, model?.Channel2Config);
            MapChannelModbusBits(inputBits, coilBits, plcData.Channel3, model?.Channel3Config);
            MapChannelModbusBits(inputBits, coilBits, plcData.Channel4, model?.Channel4Config);
        }

        private static void MapChannelModbusBits(bool[] inputBits, bool[] coilBits, ChannelData channel, ChannelConfig? config)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            var manual = config?.ManualControl;
            if (manual == null)
            {
                ClearChannelBits(channel);
                return;
            }

            channel.StopButton = TryReadBitValue(manual.StopButtonAddress, inputBits, coilBits);
            channel.FullTestStart = TryReadBitValue(manual.FullTestStartAddress, inputBits, coilBits);
            channel.MassageStart = TryReadBitValue(manual.MassageStartAddress, inputBits, coilBits);
            channel.SideWingStart = TryReadBitValue(manual.SideWingStartAddress, inputBits, coilBits);

            if (channel.MassagePoints != null && channel.MassagePoints.Length > 0)
            {
                Array.Fill(channel.MassagePoints, false);

                if (config?.MassageConfigs != null)
                {
                    foreach (var massage in config.MassageConfigs)
                    {
                        if (massage == null || massage.Point <= 0)
                        {
                            continue;
                        }

                        int index = massage.Point - 1;
                        if (index < 0 || index >= channel.MassagePoints.Length)
                        {
                            continue;
                        }

                        if (!ModbusAddressHelper.TryParseBitAddress(massage.HeightSwitchAddress, out var parsed))
                        {
                            continue;
                        }

                        if (parsed.Type != ModbusBitType.DiscreteInput)
                        {
                            continue;
                        }

                        if (parsed.Index < 0 || parsed.Index >= inputBits.Length)
                        {
                            continue;
                        }

                        channel.MassagePoints[index] = inputBits[parsed.Index];
                    }
                }
            }

            channel.PowerOff = TryReadBitValue(manual.PowerOffAddress, inputBits, coilBits);
            channel.CylinderOpen = TryReadBitValue(manual.ClampCylinderAddress, inputBits, coilBits);
            channel.CylinderClose = TryReadBitValue(manual.SpareCylinderAddress, inputBits, coilBits);
            channel.DriverSwitch = TryReadBitValue(manual.DriverSwitchAddress, inputBits, coilBits);
            channel.MassageKey = TryReadBitValue(manual.MassageKeyAddress, inputBits, coilBits);
            channel.UpInflateDownDeflate = TryReadBitValue(GetLumbarActionAddress(config, LumbarActionType.UpInflateDownDeflate), inputBits, coilBits);
            channel.DownInflateUpDeflate = TryReadBitValue(GetLumbarActionAddress(config, LumbarActionType.DownInflateUpDeflate), inputBits, coilBits);
            channel.BothInflate = TryReadBitValue(GetLumbarActionAddress(config, LumbarActionType.SimultaneousInflate), inputBits, coilBits);
            channel.BothDeflate = TryReadBitValue(GetLumbarActionAddress(config, LumbarActionType.SimultaneousDeflate), inputBits, coilBits);
            channel.FullTestLight = TryReadBitValue(manual.FullTestLightAddress, inputBits, coilBits);
            channel.MassageLight = TryReadBitValue(manual.MassageLightAddress, inputBits, coilBits);
            channel.SideWingLight = TryReadBitValue(manual.SideWingLightAddress, inputBits, coilBits);
            channel.TestOKLight = TryReadBitValue(manual.TestOkLightAddress, inputBits, coilBits);
            channel.TestNGLight = TryReadBitValue(manual.TestNgLightAddress, inputBits, coilBits);
            channel.AirLeakStartButton = TryReadBitValue(FirstNonEmpty(manual.AirLeakStartButtonAddress, manual.FullTestStartAddress), inputBits, coilBits);
            channel.HighPressureInletValve = TryReadBitValue(FirstNonEmpty(manual.HighPressureInletValveAddress, manual.UpInflateDownDeflateAddress), inputBits, coilBits);
            channel.HighPressureExhaustValve = TryReadBitValue(FirstNonEmpty(manual.HighPressureExhaustValveAddress, manual.DownInflateUpDeflateAddress), inputBits, coilBits);
            channel.LowPressureInletValve = TryReadBitValue(FirstNonEmpty(manual.LowPressureInletValveAddress, manual.BothInflateAddress), inputBits, coilBits);
            channel.LowPressureExhaustValve = TryReadBitValue(FirstNonEmpty(manual.LowPressureExhaustValveAddress, manual.BothDeflateAddress), inputBits, coilBits);
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string? GetLumbarActionAddress(ChannelConfig? config, LumbarActionType action)
        {
            if (config?.LumbarTestConfigs == null)
            {
                return null;
            }

            var match = config.LumbarTestConfigs
                .Where(c => c != null && c.Action == action && !string.IsNullOrWhiteSpace(c.MRegisterAddress))
                .OrderBy(c => c.Order)
                .FirstOrDefault();

            if (match == null)
            {
                return null;
            }

            return ModbusAddressHelper.ParseAddressList(match.MRegisterAddress)
                .Select(ModbusAddressHelper.NormalizeBitAddressToken)
                .FirstOrDefault(token => !string.IsNullOrWhiteSpace(token) &&
                                         token.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryReadBitValue(string? address, bool[] inputBits, bool[] coilBits)
        {
            if (!ModbusAddressHelper.TryParseBitAddress(address, out var parsed))
            {
                return false;
            }

            bool[] source = parsed.Type == ModbusBitType.Coil ? coilBits : inputBits;
            if (parsed.Index < 0 || parsed.Index >= source.Length)
            {
                return false;
            }

            return source[parsed.Index];
        }

        private static void ClearChannelBits(ChannelData channel)
        {
            channel.StopButton = false;
            channel.FullTestStart = false;
            channel.MassageStart = false;
            channel.SideWingStart = false;
            channel.MassageKey = false;
            channel.PowerOff = false;
            channel.CylinderOpen = false;
            channel.CylinderClose = false;
            channel.DriverSwitch = false;
            channel.FullTestLight = false;
            channel.MassageLight = false;
            channel.SideWingLight = false;
            channel.TestOKLight = false;
            channel.TestNGLight = false;
            channel.AirLeakStartButton = false;
            channel.HighPressureInletValve = false;
            channel.HighPressureExhaustValve = false;
            channel.LowPressureInletValve = false;
            channel.LowPressureExhaustValve = false;
            channel.UpInflateDownDeflate = false;
            channel.DownInflateUpDeflate = false;
            channel.BothInflate = false;
            channel.BothDeflate = false;

            if (channel.MassagePoints != null)
            {
                Array.Fill(channel.MassagePoints, false);
            }
        }

        private static void MapChannelMRegisters(bool[] mRegisters, ChannelAddressMap map, ChannelData channel)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            int maxAddress = map.GetAllBitAddresses().Max();
            if (mRegisters.Length <= maxAddress)
                throw new ArgumentException("mRegisters鏁扮粍闀垮害涓嶈冻");

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
            channel.OutputSpare2 = mRegisters[map.OutputSpare2];
        }

        public static void MapStructuredDataToMRegisters(PLCData plcData, bool[] mRegisters)
        {
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));
            if (mRegisters == null)
                throw new ArgumentNullException(nameof(mRegisters));

            MapChannelToMRegisters(plcData.Channel1, Channel1Addresses, mRegisters);
            MapChannelToMRegisters(plcData.Channel2, Channel2Addresses, mRegisters);
            MapChannelToMRegisters(plcData.Channel3, Channel3Addresses, mRegisters);
            MapChannelToMRegisters(plcData.Channel4, Channel4Addresses, mRegisters);
        }

        private static void MapChannelToMRegisters(ChannelData channel, ChannelAddressMap map, bool[] mRegisters)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            foreach (int address in map.GetAllBitAddresses())
            {
                if (address >= mRegisters.Length)
                    throw new ArgumentException("mRegisters鏁扮粍闀垮害涓嶈冻");
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
            mRegisters[map.OutputSpare2] = channel.OutputSpare2;
        }

        // 娣诲姞缂哄け鐨勬柟娉曪細MapDRegistersToStructuredData
        public static void MapDRegistersToStructuredData(ushort[] dRegisters, PLCData plcData)
        {
            if (dRegisters == null)
                throw new ArgumentNullException(nameof(dRegisters));
            if (plcData == null)
                throw new ArgumentNullException(nameof(plcData));

            MapChannelDRegisters(dRegisters, plcData.Channel1, Channel1Addresses, "Channel1");
            MapChannelDRegisters(dRegisters, plcData.Channel2, Channel2Addresses, "Channel2");
            MapChannelDRegisters(dRegisters, plcData.Channel3, Channel3Addresses, "Channel3");
            MapChannelDRegisters(dRegisters, plcData.Channel4, Channel4Addresses, "Channel4");

            if (TryReadRegister(dRegisters, SystemTestStepAddress, out var testStep, "System.TestStep"))
            {
                plcData.System.TestStep = testStep;
            }

            if (TryReadRegister(dRegisters, SystemTestResultAddress, out var testResult, "System.TestResult"))
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
                channel.HeightRawValue = heightValue * 100;
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
            MapChannelToDRegisters(plcData.Channel3, Channel3Addresses, dRegisters);
            MapChannelToDRegisters(plcData.Channel4, Channel4Addresses, dRegisters);

            dRegisters[SystemTestStepAddress] = plcData.System.TestStep;
            dRegisters[SystemTestResultAddress] = plcData.System.TestResult;
        }

        private static void MapChannelToDRegisters(ChannelData channel, ChannelAddressMap map, ushort[] dRegisters)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            dRegisters[map.HeightValueD] = (ushort)Math.Clamp(Math.Round(channel.HeightValue), 0, ushort.MaxValue);
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

        // 娣诲姞缂哄け鐨勬柟娉曪細TryUpdateChannelBit
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
            if (map.OutputSpare2 == address) { channel.OutputSpare2 = value; return true; }

            return false;
        }

        // 娣诲姞缂哄け鐨勬柟娉曪細TryUpdateChannelWord
        public static bool TryUpdateChannelWord(ChannelData channel, ChannelAddressMap map, int address, ushort value)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));
            if (map == null)
                throw new ArgumentNullException(nameof(map));

            if (map.HeightValueD == address)
            {
                channel.HeightRawValue = value * 100;
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
        public int OutputSpare2 { get; init; }
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
                yield return OutputSpare2;
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
        Aborted = 11,
        HighPressureInflate = 12,
        HighPressureStabilize = 13,
        HighPressureLeakCheck = 14,
        HighPressureExhaust = 15,
        LowPressureInflate = 16,
        LowPressureStabilize = 17,
        LowPressureLeakCheck = 18,
        LowPressureExhaust = 19
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
                LumbarActionType.UpInflateDownDeflate => "涓婂厖涓嬫斁",
                LumbarActionType.DownInflateUpDeflate => "涓嬪厖涓婃斁",
                LumbarActionType.SimultaneousInflate => "鍚屾椂鍏呮皵",
                LumbarActionType.SimultaneousDeflate => "鍚屾椂鏀炬皵",
                LumbarActionType.FrameHeaderSwitch => "甯уご鍒囨崲",
                _ => action.ToString()
            };
        }

        public static string FormatWithOrder(this LumbarActionType action, int order)
        {
            string displayName = action.ToDisplayName();
            return string.IsNullOrWhiteSpace(displayName)
                ? $"鑵版墭鍔ㄤ綔{order}"
                : $"鑵版墭鍔ㄤ綔{order}({displayName})";
        }
    }

    public class ProductModel : INotifyPropertyChanged
    {
        private string _modelName = string.Empty;
        private string _description = string.Empty;
        private string _imagePath = string.Empty;
        private ChannelConfig _channel1Config = new() { ChannelName = "閫氶亾1" };
        private ChannelConfig _channel2Config = new() { ChannelName = "閫氶亾2" };
        private ChannelConfig _channel3Config = new() { ChannelName = "閫氶亾3" };
        private ChannelConfig _channel4Config = new() { ChannelName = "閫氶亾4" };
        private CurrentSleepConfig _currentSleepConfig = new();
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

        public ChannelConfig Channel3Config
        {
            get => _channel3Config;
            set
            {
                var newValue = value ?? _channel3Config;

                if (!ReferenceEquals(_channel3Config, newValue))
                {
                    _channel3Config = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public ChannelConfig Channel4Config
        {
            get => _channel4Config;
            set
            {
                var newValue = value ?? _channel4Config;

                if (!ReferenceEquals(_channel4Config, newValue))
                {
                    _channel4Config = newValue;
                    OnPropertyChanged();
                }
            }
        }

        public CurrentSleepConfig CurrentSleepConfig
        {
            get => _currentSleepConfig;
            set
            {
                var newValue = value ?? _currentSleepConfig;
                if (!ReferenceEquals(_currentSleepConfig, newValue))
                {
                    _currentSleepConfig = newValue;
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
        public string MRegisterAddress { get; set; } = string.Empty;
        public ushort[] SendMessage { get; set; } = new ushort[20];
    }

    public class MassageConfig
    {
        public int Point { get; set; }

#pragma warning disable CS0618 // 浣跨敤鍏煎鏃х増鏈厤缃殑灞炴€?
        [Obsolete("鎸夋懇鍔ㄤ綔椤哄簭宸插純鐢紝浣跨敤鐐逛綅瀹氫箟鎵ц椤哄簭銆?)]
        public int Order { get; set; }

        [Obsolete("鍗曞姩浣滄渶灏忔椂闀垮凡寮冪敤锛屾敼涓哄湪 MassageTestSettings 涓厤缃叡浜弬鏁般€?)]
        public int MinDuration { get; set; } = 1000;

        [Obsolete("鍗曞姩浣滄渶澶ф椂闀垮凡寮冪敤锛屾敼涓哄湪 MassageTestSettings 涓厤缃叡浜弬鏁般€?)]
        public int MaxDuration { get; set; } = 5000;

        [Obsolete("宄板€肩數娴佷笅闄愬凡杩佺Щ鍒板叡浜弬鏁伴厤缃€?)]
        public double PeakCurrentMin { get; set; } = 100;

        [Obsolete("宄板€肩數娴佷笂闄愬凡杩佺Щ鍒板叡浜弬鏁伴厤缃€?)]
        public double PeakCurrentMax { get; set; } = 2000;

        [Obsolete("骞冲潎鐢垫祦涓嬮檺宸茶縼绉诲埌鍏变韩鍙傛暟閰嶇疆銆?)]
        public double AverageCurrentMin { get; set; } = 100;

        [Obsolete("骞冲潎鐢垫祦涓婇檺宸茶縼绉诲埌鍏变韩鍙傛暟閰嶇疆銆?)]
        public double AverageCurrentMax { get; set; } = 1500;
#pragma warning restore CS0618

        [JsonProperty("PressureSwitchAddress")]
        [JsonPropertyName("PressureSwitchAddress")]
        public string HeightSwitchAddress { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public class MassageTestSettings
    {
        private int _totalDuration = 60000;
        private int _highLevelDurationMin = 500;
        private int _highLevelDurationMax = 5000;
        private int _maxConcurrentPoints = 2;

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

        public int MaxConcurrentPoints
        {
            get => _maxConcurrentPoints;
            set => _maxConcurrentPoints = Math.Clamp(value, 2, 8);
        }

        public double PeakCurrentMin { get; set; } = 100;
        public double PeakCurrentMax { get; set; } = 2000;
        public double AverageCurrentMin { get; set; } = 100;
        public double AverageCurrentMax { get; set; } = 1500;
    }

    public class AirLeakTestSettings
    {
        public int HighInflateDurationMs { get; set; } = 3000;
        public int HighStabilizeDurationMs { get; set; } = 2000;
        public int HighDetectDurationMs { get; set; } = 5000;
        public int HighExhaustDurationMs { get; set; } = 3000;
        public double HighMaxDropKPa { get; set; } = 1.0;
        public int LowInflateDurationMs { get; set; } = 3000;
        public int LowStabilizeDurationMs { get; set; } = 2000;
        public int LowDetectDurationMs { get; set; } = 5000;
        public int LowExhaustDurationMs { get; set; } = 3000;
        public double LowMaxDropPa { get; set; } = 100.0;
        public int PressureSampleIntervalMs { get; set; } = 200;
    }

    public class PressureChannelConfig
    {
        public bool Enabled { get; set; } = true;
        public int InputRegisterAddress { get; set; } = 40097;
        public int OutputRegisterAddress { get; set; } = 40023;
        public int ZeroRaw { get; set; } = 0;
        public int FullScaleRaw { get; set; } = 10000;
        public double PressureZeroKPa { get; set; } = 0;
        public double PressureFullScaleKPa { get; set; } = 100;
        public double Output4mAPressureKPa { get; set; } = 0;
        public double Output20mAPressureKPa { get; set; } = 100;
    }
    public class CurrentSleepConfig
    {
        public double StaticCurrentMin { get; set; } = 0;
        public double StaticCurrentMax { get; set; } = 100;
        public double WorkCurrentMin { get; set; } = 100;
        public double WorkCurrentMax { get; set; } = 2000;
        public double CurrentOverLimit { get; set; } = 2500;
        public double SleepCurrentThreshold { get; set; } = 0.5;
        public int SleepTestTimeout { get; set; } = 5000;
        public int HeightCodeMin { get; set; } = 0;
        public int HeightCodeMax { get; set; } = 5000;
        public double HeightRangeMin { get; set; } = 0;
        public double HeightRangeMax { get; set; } = 50;

        public static CurrentSleepConfig FromChannel(ChannelConfig channel)
        {
            return new CurrentSleepConfig
            {
                StaticCurrentMin = channel.StaticCurrentMin,
                StaticCurrentMax = channel.StaticCurrentMax,
                WorkCurrentMin = channel.WorkCurrentMin,
                WorkCurrentMax = channel.WorkCurrentMax,
                CurrentOverLimit = channel.CurrentOverLimit,
                SleepCurrentThreshold = channel.SleepCurrentThreshold,
                SleepTestTimeout = channel.SleepTestTimeout,
                HeightCodeMin = channel.HeightCodeMin,
                HeightCodeMax = channel.HeightCodeMax,
                HeightRangeMin = channel.HeightRangeMin,
                HeightRangeMax = channel.HeightRangeMax
            };
        }

        public void ApplyToChannel(ChannelConfig channel)
        {
            channel.StaticCurrentMin = StaticCurrentMin;
            channel.StaticCurrentMax = StaticCurrentMax;
            channel.WorkCurrentMin = WorkCurrentMin;
            channel.WorkCurrentMax = WorkCurrentMax;
            channel.CurrentOverLimit = CurrentOverLimit;
            channel.SleepCurrentThreshold = SleepCurrentThreshold;
            channel.SleepTestTimeout = SleepTestTimeout;
            channel.HeightCodeMin = HeightCodeMin;
            channel.HeightCodeMax = HeightCodeMax;
            channel.HeightRangeMin = HeightRangeMin;
            channel.HeightRangeMax = HeightRangeMax;
        }

        public bool MatchesChannel(ChannelConfig channel)
        {
            return StaticCurrentMin.Equals(channel.StaticCurrentMin)
                && StaticCurrentMax.Equals(channel.StaticCurrentMax)
                && WorkCurrentMin.Equals(channel.WorkCurrentMin)
                && WorkCurrentMax.Equals(channel.WorkCurrentMax)
                && CurrentOverLimit.Equals(channel.CurrentOverLimit)
                && SleepCurrentThreshold.Equals(channel.SleepCurrentThreshold)
                && SleepTestTimeout == channel.SleepTestTimeout
                && HeightCodeMin == channel.HeightCodeMin
                && HeightCodeMax == channel.HeightCodeMax
                && HeightRangeMin.Equals(channel.HeightRangeMin)
                && HeightRangeMax.Equals(channel.HeightRangeMax);
        }

        public bool Matches(CurrentSleepConfig other)
        {
            return StaticCurrentMin.Equals(other.StaticCurrentMin)
                && StaticCurrentMax.Equals(other.StaticCurrentMax)
                && WorkCurrentMin.Equals(other.WorkCurrentMin)
                && WorkCurrentMax.Equals(other.WorkCurrentMax)
                && CurrentOverLimit.Equals(other.CurrentOverLimit)
                && SleepCurrentThreshold.Equals(other.SleepCurrentThreshold)
                && SleepTestTimeout == other.SleepTestTimeout
                && HeightCodeMin == other.HeightCodeMin
                && HeightCodeMax == other.HeightCodeMax
                && HeightRangeMin.Equals(other.HeightRangeMin)
                && HeightRangeMax.Equals(other.HeightRangeMax);
        }

        public double ConvertHeightCodeToMillimeters(int rawCode)
        {
            if (HeightCodeMax == HeightCodeMin)
            {
                return rawCode / 100.0;
            }

            double codeSpan = HeightCodeMax - HeightCodeMin;
            double normalized = (rawCode - HeightCodeMin) / codeSpan;
            double mapped = HeightRangeMin + normalized * (HeightRangeMax - HeightRangeMin);
            return Math.Clamp(mapped, Math.Min(HeightRangeMin, HeightRangeMax), Math.Max(HeightRangeMin, HeightRangeMax));
        }
    }

    public class ChannelConfig
    {
        public string ChannelName { get; set; } = "閫氶亾1";

        public double StaticCurrentMin { get; set; } = 0;
        public double StaticCurrentMax { get; set; } = 100;
        public double WorkCurrentMin { get; set; } = 100;
        public double WorkCurrentMax { get; set; } = 2000;
        public double CurrentOverLimit { get; set; } = 2500;

        public double SleepCurrentThreshold { get; set; } = 0.5;
        public int SleepTestTimeout { get; set; } = 5000;
        public int HeightCodeMin { get; set; } = 0;
        public int HeightCodeMax { get; set; } = 5000;
        public double HeightRangeMin { get; set; } = 0;
        public double HeightRangeMax { get; set; } = 50;

        public int PowerOffDurationMin { get; set; } = 5000;
        public int PowerOffDurationMax { get; set; } = 30000;

        public List<LumbarTestConfig> LumbarTestConfigs { get; set; } = new List<LumbarTestConfig>();
        public List<MassageConfig> MassageConfigs { get; set; } = new List<MassageConfig>();

        public MassageTestSettings MassageTestSettings { get; set; } = new MassageTestSettings();
        public AirLeakTestSettings AirLeakTestSettings { get; set; } = new AirLeakTestSettings();
        public PressureChannelConfig PressureConfig { get; set; } = new PressureChannelConfig();

        public ushort[] StatusMessagePreset { get; set; } = new ushort[20];

        public List<MessageKeyTestConfig> MessageKeyTestConfigs { get; set; } = new List<MessageKeyTestConfig>();

        public MessageConfig MessageConfig { get; set; } = new MessageConfig();

        public ManualControlAddressConfig ManualControl { get; set; } = new ManualControlAddressConfig();
    }

    public enum MessageKeyTriggerMode
    {
        Continuous = 0,
        Interval = 1
    }

    public class MessageKeyTestConfig
    {
        public int Order { get; set; }
        public bool Enabled { get; set; } = true;
        public string OutputRegisterAddress { get; set; } = string.Empty;
        public int ReadByteIndex { get; set; } = 21;
        public int ExpectedValue { get; set; } = 0;
        public MessageKeyTriggerMode TriggerMode { get; set; } = MessageKeyTriggerMode.Continuous;
    }

    public class ManualControlAddressConfig
    {
        public string StopButtonAddress { get; set; } = string.Empty;
        public string FullTestStartAddress { get; set; } = string.Empty;
        public string MassageStartAddress { get; set; } = string.Empty;
        public string SideWingStartAddress { get; set; } = string.Empty;
        public List<string> MassagePointAddresses { get; set; } = new List<string>();
        public string PowerOffAddress { get; set; } = string.Empty;
        public string ClampCylinderAddress { get; set; } = string.Empty;
        public string SpareCylinderAddress { get; set; } = string.Empty;
        public string DriverSwitchAddress { get; set; } = string.Empty;
        public string UpInflateDownDeflateAddress { get; set; } = string.Empty;
        public string DownInflateUpDeflateAddress { get; set; } = string.Empty;
        public string BothInflateAddress { get; set; } = string.Empty;
        public string BothDeflateAddress { get; set; } = string.Empty;
        public string MassageKeyAddress { get; set; } = string.Empty;
        public string FullTestLightAddress { get; set; } = string.Empty;
        public string MassageLightAddress { get; set; } = string.Empty;
        public string SideWingLightAddress { get; set; } = string.Empty;
        public string TestOkLightAddress { get; set; } = string.Empty;
        public string TestNgLightAddress { get; set; } = string.Empty;
        public string AirLeakStartButtonAddress { get; set; } = string.Empty;
        public string HighPressureInletValveAddress { get; set; } = string.Empty;
        public string HighPressureExhaustValveAddress { get; set; } = string.Empty;
        public string LowPressureInletValveAddress { get; set; } = string.Empty;
        public string LowPressureExhaustValveAddress { get; set; } = string.Empty;
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
        public bool EnableBarcodePrefixCheck { get; set; } = false;
        public string BarcodePrefix { get; set; } = string.Empty;
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
        public List<AirLeakPressureResult> AirLeakResults { get; set; } = new List<AirLeakPressureResult>();
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
        public bool HeightSwitchTriggered { get; set; }
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

    public class AirLeakPressureResult
    {
        public string Phase { get; set; } = string.Empty;
        public double StartPressure { get; set; }
        public double EndPressure { get; set; }
        public double PressureDrop { get; set; }
        public double Limit { get; set; }
        public string Unit { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string Message { get; set; } = string.Empty;
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

    public class MassagePointSampleEventArgs : EventArgs
    {
        public MassagePointSampleEventArgs(int channel, bool[] states)
        {
            Channel = channel;
            States = states ?? Array.Empty<bool>();
        }

        public int Channel { get; }
        public bool[] States { get; }
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




