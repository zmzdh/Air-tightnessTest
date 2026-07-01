// Services/SwdCanModbusTestService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Services
{
    /// <summary>
    /// 新测试流程的参数汇总与基础校验逻辑。后续可在此衔接 pyOCD、CAN、Modbus 具体实现。
    /// </summary>
    public class SwdCanModbusTestService
    {
        private readonly AppConfig _config;
        private readonly ILogService _logService;

        public SwdCanModbusTestService(AppConfig config, ILogService? logService = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logService = logService ?? LogService.Instance;
        }

        public CanTestProfile BuildDefaultCanProfile()
        {
            var canConfig = _config.Can ?? new CanTestConfig();
            var tolerance = Math.Max(0, canConfig.CurrentToleranceMa);

            return new CanTestProfile
            {
                BaudRate = canConfig.BaudRate,
                ListenTimeoutMs = canConfig.ListenTimeoutMs,
                ListenRetryCount = canConfig.ListenRetryCount,
                FrequencyHz = canConfig.DefaultFrequencyHz,
                Strengths = new List<StrengthSetting>
                {
                    BuildStrength("低", canConfig.DefaultStrengthLow, canConfig.CurrentTargetLowMa, tolerance),
                    BuildStrength("中", canConfig.DefaultStrengthMid, canConfig.CurrentTargetMidMa, tolerance),
                    BuildStrength("高", canConfig.DefaultStrengthHigh, canConfig.CurrentTargetHighMa, tolerance)
                }
            };
        }

        private static StrengthSetting BuildStrength(string label, int strength, int targetCurrent, int tolerance)
        {
            return new StrengthSetting
            {
                Label = label,
                Strength = strength,
                CurrentTargetMa = targetCurrent,
                CurrentMinMa = targetCurrent - tolerance,
                CurrentMaxMa = targetCurrent + tolerance
            };
        }

        public bool IsVoltageWithinRange(double voltage)
        {
            var canConfig = _config.Can ?? new CanTestConfig();
            return voltage >= canConfig.VoltageMin && voltage <= canConfig.VoltageMax;
        }

        public SummaryLogEntry CreateSummaryLog(string stepName, int resultCode, DateTime start, DateTime end, string? message = null)
        {
            return new SummaryLogEntry
            {
                StepName = stepName,
                ResultCode = resultCode,
                StartTime = start,
                EndTime = end,
                DurationMs = (int)Math.Max(0, (end - start).TotalMilliseconds),
                Message = message
            };
        }

        public async Task<SwdCanModbusTestResult> RunIntegratedPlanAsync(
            string barcode,
            string firmwarePath,
            string probeUid,
            CancellationToken token)
        {
            if (_config.RequireBarcodeForTest && string.IsNullOrWhiteSpace(barcode))
            {
                throw new InvalidOperationException("必须扫码才能进入测试流程");
            }

            var result = new SwdCanModbusTestResult();
            var canConfig = _config.Can ?? new CanTestConfig();
            var modbusConfig = _config.Modbus ?? new ModbusRtuConfig();
            var swdConfig = _config.Swd ?? new SwdConfig();

            using var modbus = new ModbusRtuService(modbusConfig, _logService);
            using var can = new CanBusService(canConfig, _logService);

            await modbus.OpenAsync(token).ConfigureAwait(false);
            can.Open();

            DateTime start;

            start = DateTime.Now;
            ProcessResult eraseResult = await PyOcdService.EraseChip(probeUid, swdConfig.EraseTimeoutMs).ConfigureAwait(false);
            result.Steps.Add(CreateSummaryLog("SWD擦除", eraseResult.IsSuccess ? 0 : eraseResult.ExitCode, start, DateTime.Now, TrimLog(eraseResult.Output)));

            start = DateTime.Now;
            ProcessResult programResult = await PyOcdService.FlashHex(probeUid, firmwarePath, swdConfig.ProgramTimeoutMs).ConfigureAwait(false);
            result.Steps.Add(CreateSummaryLog("SWD烧录", programResult.IsSuccess ? 0 : programResult.ExitCode, start, DateTime.Now, TrimLog(programResult.Output)));
            if (!programResult.IsSuccess)
            {
                result.Success = false;
                result.ErrorMessage = "烧录失败，已终止后续测试";
                return result;
            }

            start = DateTime.Now;
            await modbus.SetPowerAsync(true, token).ConfigureAwait(false);
            double voltage = await modbus.ReadVoltageAsync(token).ConfigureAwait(false);
            result.Voltage = voltage;
            bool voltageOk = IsVoltageWithinRange(voltage);
            result.Steps.Add(CreateSummaryLog("上电与电压检测", voltageOk ? 0 : 1, start, DateTime.Now, $"电压={voltage:F2}V"));
            if (!voltageOk)
            {
                result.ErrorMessage = "电压不在合格范围";
            }

            var profile = BuildDefaultCanProfile();
            foreach (var seat in new[] { SeatChannel.Left, SeatChannel.Right })
            {
                foreach (var strength in profile.Strengths)
                {
                    token.ThrowIfCancellationRequested();
                    await can.SendFrequencyStrengthAsync(profile.FrequencyHz, strength.Strength, token).ConfigureAwait(false);
                    await can.SendStrengthPresetAsync(
                        seat == SeatChannel.Left ? strength.Strength : 0,
                        seat == SeatChannel.Right ? strength.Strength : 0,
                        token).ConfigureAwait(false);

                    double currentMa = await modbus.ReadCurrentAsync(token).ConfigureAwait(false) * 1000.0;
                    bool pass = currentMa >= strength.CurrentMinMa && currentMa <= strength.CurrentMaxMa;

                    result.Measurements.Add(new StrengthMeasurement
                    {
                        Seat = seat,
                        Label = strength.Label,
                        FrequencyHz = profile.FrequencyHz,
                        Strength = strength.Strength,
                        CurrentMa = currentMa,
                        Passed = pass,
                        TargetMinMa = strength.CurrentMinMa,
                        TargetMaxMa = strength.CurrentMaxMa
                    });

                    result.Steps.Add(CreateSummaryLog(
                        $"{seat}侧{strength.Label}档电流检测",
                        pass ? 0 : 1,
                        DateTime.Now,
                        DateTime.Now,
                        $"测得 {currentMa:F1} mA，目标 {strength.CurrentMinMa}-{strength.CurrentMaxMa} mA"));
                }
            }

            if (canConfig.PassengerOnlyShortOpenTest)
            {
                start = DateTime.Now;
                await modbus.ActivateShortCircuitAsync(token).ConfigureAwait(false);
                bool shortOk = await can.WaitForSeatStatusAsync(_ => true, token).ConfigureAwait(false);
                result.Steps.Add(CreateSummaryLog("短路报文监听", shortOk ? 0 : 1, start, DateTime.Now, "期望在5秒内收到报警报文"));

                start = DateTime.Now;
                await modbus.ActivateOpenCircuitAsync(token).ConfigureAwait(false);
                bool openOk = await can.WaitForSeatStatusAsync(_ => true, token).ConfigureAwait(false);
                result.Steps.Add(CreateSummaryLog("开路报文监听", openOk ? 0 : 1, start, DateTime.Now, "期望在5秒内收到状态恢复报文"));

                result.Success = programResult.IsSuccess && result.Measurements.All(m => m.Passed) && voltageOk && shortOk && openOk;
            }
            else
            {
                result.Success = programResult.IsSuccess && result.Measurements.All(m => m.Passed) && voltageOk;
            }

            start = DateTime.Now;
            await modbus.SetPowerAsync(false, token).ConfigureAwait(false);
            result.Steps.Add(CreateSummaryLog("关电", 0, start, DateTime.Now));

            return result;
        }

        private static string TrimLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return text.Trim().Length > 500 ? text.Trim()[..500] : text.Trim();
        }
    }

    public class CanTestProfile
    {
        public int BaudRate { get; set; }
        public int ListenTimeoutMs { get; set; }
        public int ListenRetryCount { get; set; }
        public int FrequencyHz { get; set; }
        public List<StrengthSetting> Strengths { get; set; } = new List<StrengthSetting>();
    }

    public class StrengthSetting
    {
        public string Label { get; set; } = string.Empty;
        public int Strength { get; set; }
        public int CurrentTargetMa { get; set; }
        public int CurrentMinMa { get; set; }
        public int CurrentMaxMa { get; set; }
    }

    public class SwdCanModbusTestResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public double? Voltage { get; set; }
        public List<StrengthMeasurement> Measurements { get; } = new();
        public List<SummaryLogEntry> Steps { get; } = new();
    }

    public class StrengthMeasurement
    {
        public SeatChannel Seat { get; set; }
        public string Label { get; set; } = string.Empty;
        public int FrequencyHz { get; set; }
        public int Strength { get; set; }
        public double CurrentMa { get; set; }
        public double TargetMinMa { get; set; }
        public double TargetMaxMa { get; set; }
        public bool Passed { get; set; }
    }
}
