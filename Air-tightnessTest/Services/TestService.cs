using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public class TestService : IDisposable
    {
        private readonly IPLCService _plcService;
        private readonly PressureModbusService _pressureService;
        private readonly ILogService _logService;
        private readonly Dictionary<int, ChannelTestContext> _activeChannels = new();
        private readonly Dictionary<string, int> _barcodeHistory = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _syncRoot = new();
        private bool _disposed;

        public event EventHandler<TestStageChangedEventArgs>? OnTestStageChanged;
        public event EventHandler<TestRecord>? OnTestCompleted;
        public event EventHandler<TestMessageEventArgs>? OnTestMessage;
        public event EventHandler<ChannelTestResultEventArgs>? OnTestResultDisplay;
        public event EventHandler<PressureSampleEventArgs>? OnPressureSample;

        public TestService(IPLCService plcService, ILogService? logService = null)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _logService = logService ?? LogService.Instance;
            _pressureService = new PressureModbusService(SerialPortConfig.CreateDefaultDevice2(), _logService);
        }

        public void ConfigurePressureModule(AppConfig config)
        {
            _pressureService.UpdateConfig(config);
        }

        public async Task<bool> StartTestAsync(TestStartOptions options)
        {
            ThrowIfDisposed();
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (options.Model == null)
            {
                RaiseTestMessage("未选择产品型号");
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.WorkOrder))
            {
                RaiseTestMessage("工单号不能为空");
                return false;
            }

            options.Barcode = CodeScanService.SanitizeBarcode(options.Barcode);
            var process = options.Model.ProcessConfig ?? new TestProcessConfig();
            if (process.EnableBarcodeCheck && string.IsNullOrWhiteSpace(options.Barcode))
            {
                RaiseTestMessage("请先扫码", options.Channel);
                return false;
            }

            ChannelConfig? channelConfig = options.Channel switch
            {
                1 => options.Model.Channel1Config,
                2 => options.Model.Channel2Config,
                3 => options.Model.Channel3Config,
                4 => options.Model.Channel4Config,
                _ => null
            };

            if (channelConfig == null)
            {
                RaiseTestMessage($"通道{options.Channel}缺少配置", options.Channel);
                return false;
            }

            lock (_syncRoot)
            {
                if (_activeChannels.ContainsKey(options.Channel))
                {
                    RaiseTestMessage($"通道{options.Channel}正在测试中", options.Channel);
                    return false;
                }
            }

            var cts = new CancellationTokenSource();
            var record = new TestRecord
            {
                TestTime = DateTime.Now,
                WorkOrder = options.WorkOrder,
                ProductModel = options.Model.ModelName,
                ProductCode = options.Barcode,
                Operator = options.Operator,
                Channel = options.Channel,
                TestVoltage = 0,
                Result = TestResult.Testing,
                FailReason = string.Empty
            };

            int duplicateCount = RegisterBarcode(options.Barcode);
            record.TestCount = duplicateCount;
            record.DuplicateCount = duplicateCount;

            var context = new ChannelTestContext
            {
                Channel = options.Channel,
                Model = options.Model,
                ChannelConfig = channelConfig,
                Options = options,
                Record = record,
                Cancellation = cts
            };

            lock (_syncRoot)
            {
                _activeChannels[options.Channel] = context;
            }

            try
            {
                RaiseTestMessage($"通道{options.Channel}开始气密性测试", options.Channel);
                return await RunChannelTestAsync(context).ConfigureAwait(false);
            }
            finally
            {
                lock (_syncRoot)
                {
                    _activeChannels.Remove(options.Channel);
                }
            }
        }

        public void StopTest(int channel)
        {
            ThrowIfDisposed();
            lock (_syncRoot)
            {
                if (_activeChannels.TryGetValue(channel, out var context))
                {
                    context.Cancellation.Cancel();
                }
            }
        }

        public void StopAllTests()
        {
            if (_disposed) return;
            List<ChannelTestContext> contexts;
            lock (_syncRoot)
            {
                contexts = _activeChannels.Values.ToList();
                _activeChannels.Clear();
            }

            foreach (var context in contexts)
            {
                context.Cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            StopAllTests();
            _pressureService.Dispose();
            _barcodeHistory.Clear();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private async Task<bool> RunChannelTestAsync(ChannelTestContext context)
        {
            try
            {
                if (!await ExecuteStageAsync(context, TestStage.Standby, EnsureStandbyAsync).ConfigureAwait(false))
                    return await FailAsync(context, "待机检查失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.ScanBarcode, ConfirmBarcodeAsync).ConfigureAwait(false))
                    return await FailAsync(context, "扫码失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.StartTest, BeginAirLeakTestAsync).ConfigureAwait(false))
                    return await FailAsync(context, "启动测试失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.HighPressureInflate, PerformHighPressureInflateAsync).ConfigureAwait(false))
                    return await FailAsync(context, "高压进气失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.HighPressureStabilize, PerformHighPressureStabilizeAsync).ConfigureAwait(false))
                    return await FailAsync(context, "高压静置失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.HighPressureLeakCheck, PerformHighPressureLeakCheckAsync).ConfigureAwait(false))
                {
                    await ExecuteStageAsync(context, TestStage.HighPressureExhaust, PerformHighPressureExhaustAsync).ConfigureAwait(false);
                    return await FailAsync(context, BuildStageFailReason(context, TestStage.HighPressureLeakCheck, "高压气密性不合格")).ConfigureAwait(false);
                }

                if (!await ExecuteStageAsync(context, TestStage.HighPressureExhaust, PerformHighPressureExhaustAsync).ConfigureAwait(false))
                    return await FailAsync(context, "高压排气失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.LowPressureInflate, PerformLowPressureInflateAsync).ConfigureAwait(false))
                    return await FailAsync(context, "低压进气失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.LowPressureStabilize, PerformLowPressureStabilizeAsync).ConfigureAwait(false))
                    return await FailAsync(context, "低压静置失败").ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.LowPressureLeakCheck, PerformLowPressureLeakCheckAsync).ConfigureAwait(false))
                {
                    await ExecuteStageAsync(context, TestStage.LowPressureExhaust, PerformLowPressureExhaustAsync).ConfigureAwait(false);
                    return await FailAsync(context, BuildStageFailReason(context, TestStage.LowPressureLeakCheck, "低压气密性不合格")).ConfigureAwait(false);
                }

                if (!await ExecuteStageAsync(context, TestStage.LowPressureExhaust, PerformLowPressureExhaustAsync).ConfigureAwait(false))
                    return await FailAsync(context, "低压排气失败").ConfigureAwait(false);

                await ExecuteStageAsync(context, TestStage.Completed, CompleteStageAsync).ConfigureAwait(false);
                await FinalizeTestAsync(context, true, "气密性测试完成").ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                await FinalizeTestAsync(context, false, "测试被终止", aborted: true).ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                _logService.LogError("气密性测试异常", ex);
                await FinalizeTestAsync(context, false, $"测试异常: {ex.Message}").ConfigureAwait(false);
                return false;
            }
        }

        private Task<StageExecutionResult> EnsureStandbyAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(StageExecutionResult.Pass("待机正常"));
        }

        private Task<StageExecutionResult> ConfirmBarcodeAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var process = context.Model.ProcessConfig ?? new TestProcessConfig();
            if (!process.EnableBarcodeCheck)
            {
                return Task.FromResult(StageExecutionResult.Pass("已跳过扫码检查"));
            }

            if (string.IsNullOrWhiteSpace(context.Options.Barcode))
            {
                return Task.FromResult(StageExecutionResult.Fail("未扫描条码"));
            }

            if (process.EnableBarcodePrefixCheck)
            {
                string prefix = process.BarcodePrefix?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(prefix) && !context.Options.Barcode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(StageExecutionResult.Fail($"条码前缀不匹配，要求: {prefix}"));
                }
            }

            if (context.Record.DuplicateCount > 1 && process.PromptOnDuplicateBarcode && !context.Options.ContinueOnDuplicate)
            {
                return Task.FromResult(StageExecutionResult.Fail($"重复条码，第{context.Record.DuplicateCount}次测试已取消"));
            }

            return Task.FromResult(StageExecutionResult.Pass("扫码成功"));
        }

        private Task<StageExecutionResult> BeginAirLeakTestAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(StageExecutionResult.Pass($"通道{context.Channel}启动气密性测试"));
        }

        private Task<StageExecutionResult> PerformHighPressureInflateAsync(ChannelTestContext context, CancellationToken token)
            => InflateAsync(context, true, token);

        private Task<StageExecutionResult> PerformLowPressureInflateAsync(ChannelTestContext context, CancellationToken token)
            => InflateAsync(context, false, token);

        private Task<StageExecutionResult> PerformHighPressureStabilizeAsync(ChannelTestContext context, CancellationToken token)
            => DelayStageAsync(context, TestStage.HighPressureStabilize, "高压静置", GetSettings(context).HighStabilizeDurationMs, token);

        private Task<StageExecutionResult> PerformLowPressureStabilizeAsync(ChannelTestContext context, CancellationToken token)
            => DelayStageAsync(context, TestStage.LowPressureStabilize, "低压静置", GetSettings(context).LowStabilizeDurationMs, token);

        private Task<StageExecutionResult> PerformHighPressureLeakCheckAsync(ChannelTestContext context, CancellationToken token)
        {
            var settings = GetSettings(context);
            return LeakCheckAsync(context, TestStage.HighPressureLeakCheck, "高压", settings.HighDetectDurationMs, settings.HighMaxDropKPa, "KPa", 1.0, token);
        }

        private Task<StageExecutionResult> PerformLowPressureLeakCheckAsync(ChannelTestContext context, CancellationToken token)
        {
            var settings = GetSettings(context);
            return LeakCheckAsync(context, TestStage.LowPressureLeakCheck, "低压", settings.LowDetectDurationMs, settings.LowMaxDropPa, "Pa", 1000.0, token);
        }

        private Task<StageExecutionResult> PerformHighPressureExhaustAsync(ChannelTestContext context, CancellationToken token)
            => ExhaustAsync(context, true, token);

        private Task<StageExecutionResult> PerformLowPressureExhaustAsync(ChannelTestContext context, CancellationToken token)
            => ExhaustAsync(context, false, token);

        private async Task<StageExecutionResult> InflateAsync(ChannelTestContext context, bool highPressure, CancellationToken token)
        {
            var settings = GetSettings(context);
            int duration = highPressure ? settings.HighInflateDurationMs : settings.LowInflateDurationMs;
            string name = highPressure ? "高压" : "低压";
            var stage = highPressure ? TestStage.HighPressureInflate : TestStage.LowPressureInflate;

            double outputPressure = highPressure ? settings.HighOutputPressureKPa : settings.LowOutputPressureKPa;
            await _pressureService.WriteOutputPressureAsync(context.Channel, outputPressure, context.ChannelConfig.PressureConfig, token).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, highPressure), false).ConfigureAwait(false);
            await WriteValveAsync(GetInletValveAddress(context, highPressure), true).ConfigureAwait(false);
            RaiseStageChanged(context, stage, StepExecutionState.Running, $"{name}进气中 {duration}ms，输出设定 {outputPressure:F1}KPa");
            await Task.Delay(Math.Max(0, duration), token).ConfigureAwait(false);
            await WriteValveAsync(GetInletValveAddress(context, highPressure), false).ConfigureAwait(false);
            return StageExecutionResult.Pass($"{name}进气完成");
        }

        private async Task<StageExecutionResult> ExhaustAsync(ChannelTestContext context, bool highPressure, CancellationToken token)
        {
            var settings = GetSettings(context);
            int duration = highPressure ? settings.HighExhaustDurationMs : settings.LowExhaustDurationMs;
            string name = highPressure ? "高压" : "低压";
            var stage = highPressure ? TestStage.HighPressureExhaust : TestStage.LowPressureExhaust;

            await WriteValveAsync(GetInletValveAddress(context, highPressure), false).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, highPressure), true).ConfigureAwait(false);
            RaiseStageChanged(context, stage, StepExecutionState.Running, $"{name}排气中 {duration}ms");
            await Task.Delay(Math.Max(0, duration), token).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, highPressure), false).ConfigureAwait(false);
            return StageExecutionResult.Pass($"{name}排气完成");
        }

        private async Task<StageExecutionResult> DelayStageAsync(ChannelTestContext context, TestStage stage, string name, int duration, CancellationToken token)
        {
            RaiseStageChanged(context, stage, StepExecutionState.Running, $"{name} {duration}ms");
            await Task.Delay(Math.Max(0, duration), token).ConfigureAwait(false);
            return StageExecutionResult.Pass($"{name}完成");
        }

        private async Task<StageExecutionResult> LeakCheckAsync(
            ChannelTestContext context,
            TestStage stage,
            string phase,
            int detectDurationMs,
            double limit,
            string unit,
            double scale,
            CancellationToken token)
        {
            var pressureConfig = context.ChannelConfig.PressureConfig ?? new PressureChannelConfig();
            DateTime startTime = DateTime.Now;
            double startKPa = await _pressureService.ReadPressureKPaAsync(context.Channel, pressureConfig, token).ConfigureAwait(false);
            RaisePressureSample(context.Channel, startKPa);
            RaiseStageChanged(context, stage, StepExecutionState.Running, $"{phase}起始压力 {(startKPa * scale):F2}{unit}");

            int interval = Math.Max(50, GetSettings(context).PressureSampleIntervalMs);
            int elapsed = 0;
            int total = Math.Max(0, detectDurationMs);
            while (elapsed < total)
            {
                int slice = Math.Min(interval, total - elapsed);
                await Task.Delay(slice, token).ConfigureAwait(false);
                elapsed += slice;
                double sampleKPa = await _pressureService.ReadPressureKPaAsync(context.Channel, pressureConfig, token).ConfigureAwait(false);
                RaisePressureSample(context.Channel, sampleKPa);
            }

            double endKPa = await _pressureService.ReadPressureKPaAsync(context.Channel, pressureConfig, token).ConfigureAwait(false);
            RaisePressureSample(context.Channel, endKPa);
            DateTime endTime = DateTime.Now;
            double start = startKPa * scale;
            double end = endKPa * scale;
            double drop = Math.Max(0, start - end);
            bool passed = drop <= limit;
            string message = passed
                ? $"{phase}压差{drop:F2}{unit}，合格(≤{limit:F2}{unit})"
                : $"{phase}压差{drop:F2}{unit}，不合格(>{limit:F2}{unit})";

            context.Record.AirLeakResults.Add(new AirLeakPressureResult
            {
                Phase = phase,
                StartPressure = start,
                EndPressure = end,
                PressureDrop = drop,
                Limit = limit,
                Unit = unit,
                Passed = passed,
                StartTime = startTime,
                EndTime = endTime,
                Message = message
            });

            return passed
                ? StageExecutionResult.Pass(message, pressureStart: start, pressureEnd: end, pressureDrop: drop, pressureUnit: unit)
                : StageExecutionResult.Fail(message, pressureStart: start, pressureEnd: end, pressureDrop: drop, pressureUnit: unit);
        }

        private Task<StageExecutionResult> CompleteStageAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(StageExecutionResult.Pass("所有项目完成"));
        }

        private async Task<bool> ExecuteStageAsync(ChannelTestContext context, TestStage stage, Func<ChannelTestContext, CancellationToken, Task<StageExecutionResult>> action)
        {
            var result = new TestStageResult
            {
                Stage = stage,
                State = StepExecutionState.Running,
                StartTime = DateTime.Now
            };
            context.Record.StageResults.Add(result);
            RaiseStageChanged(context, stage, StepExecutionState.Running, null);

            try
            {
                StageExecutionResult execution = await action(context, context.Cancellation.Token).ConfigureAwait(false);
                result.EndTime = DateTime.Now;
                result.State = execution.Success ? StepExecutionState.Passed : StepExecutionState.Failed;
                result.Message = execution.Message;
                result.PressureStart = execution.PressureStart;
                result.PressureEnd = execution.PressureEnd;
                result.PressureDrop = execution.PressureDrop;
                result.PressureUnit = execution.PressureUnit;
                RaiseStageChanged(context, stage, result.State, result.Message);
                return execution.Success;
            }
            catch (OperationCanceledException)
            {
                result.EndTime = DateTime.Now;
                result.State = StepExecutionState.Failed;
                result.Message = "测试取消";
                RaiseStageChanged(context, stage, StepExecutionState.Failed, result.Message);
                throw;
            }
        }

        private async Task<bool> FailAsync(ChannelTestContext context, string reason)
        {
            await FinalizeTestAsync(context, false, reason).ConfigureAwait(false);
            return false;
        }

        private async Task FinalizeTestAsync(ChannelTestContext context, bool success, string message, bool aborted = false)
        {
            await CloseAllPressureValvesAsync(context).ConfigureAwait(false);

            var record = context.Record;
            record.Result = success ? TestResult.Pass : aborted ? TestResult.Aborted : TestResult.Fail;
            record.FailReason = success ? string.Empty : message;
            record.TestDuration = (DateTime.Now - record.TestTime).TotalSeconds;
            record.WasAborted = aborted;

            if (!record.StageResults.Any(r => r.Stage == TestStage.Completed || r.Stage == TestStage.Aborted))
            {
                record.StageResults.Add(new TestStageResult
                {
                    Stage = aborted ? TestStage.Aborted : TestStage.Completed,
                    State = success ? StepExecutionState.Passed : StepExecutionState.Failed,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                    Message = message
                });
            }

            OnTestResultDisplay?.Invoke(this, new ChannelTestResultEventArgs { Channel = context.Channel, IsOk = success });
            OnTestCompleted?.Invoke(this, record);
            RaiseTestMessage($"通道{context.Channel}测试结束: {message}", context.Channel);
        }

        private async Task CloseAllPressureValvesAsync(ChannelTestContext context)
        {
            await WriteValveAsync(GetInletValveAddress(context, true), false).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, true), false).ConfigureAwait(false);
            await WriteValveAsync(GetInletValveAddress(context, false), false).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, false), false).ConfigureAwait(false);
        }

        private async Task WriteValveAsync(string? address, bool value)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            await _plcService.WriteBitAsync(address.Trim(), value).ConfigureAwait(false);
        }

        private AirLeakTestSettings GetSettings(ChannelTestContext context)
            => context.ChannelConfig.AirLeakTestSettings ?? new AirLeakTestSettings();

        private string GetInletValveAddress(ChannelTestContext context, bool highPressure)
        {
            var manual = context.ChannelConfig.ManualControl ?? new ManualControlAddressConfig();
            return highPressure
                ? FirstNonEmpty(manual.HighPressureInletValveAddress, manual.UpInflateDownDeflateAddress)
                : FirstNonEmpty(manual.LowPressureInletValveAddress, manual.BothInflateAddress);
        }

        private string GetExhaustValveAddress(ChannelTestContext context, bool highPressure)
        {
            var manual = context.ChannelConfig.ManualControl ?? new ManualControlAddressConfig();
            return highPressure
                ? FirstNonEmpty(manual.HighPressureExhaustValveAddress, manual.DownInflateUpDeflateAddress)
                : FirstNonEmpty(manual.LowPressureExhaustValveAddress, manual.BothDeflateAddress);
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return string.Empty;
        }

        private static string BuildStageFailReason(ChannelTestContext context, TestStage stage, string fallback)
        {
            string? message = context.Record.StageResults.LastOrDefault(result => result.Stage == stage)?.Message;
            return string.IsNullOrWhiteSpace(message) ? fallback : message;
        }

        private int RegisterBarcode(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return 1;
            lock (_barcodeHistory)
            {
                if (_barcodeHistory.TryGetValue(barcode, out int count))
                {
                    count++;
                    _barcodeHistory[barcode] = count;
                    return count;
                }

                _barcodeHistory[barcode] = 1;
                return 1;
            }
        }

        private void RaisePressureSample(int channel, double pressureKPa)
        {
            OnPressureSample?.Invoke(this, new PressureSampleEventArgs(channel, Math.Clamp(pressureKPa, 0, 200), DateTime.Now));
        }
        private void RaiseTestMessage(string message, int? channel = null)
        {
            OnTestMessage?.Invoke(this, new TestMessageEventArgs(message ?? string.Empty, channel));
        }

        private void RaiseStageChanged(ChannelTestContext context, TestStage stage, StepExecutionState state, string? message)
        {
            OnTestStageChanged?.Invoke(this, new TestStageChangedEventArgs
            {
                Channel = context.Channel,
                Stage = stage,
                State = state,
                Message = message
            });
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TestService));
        }

        private sealed class ChannelTestContext
        {
            public int Channel { get; init; }
            public required ProductModel Model { get; init; }
            public required ChannelConfig ChannelConfig { get; init; }
            public required TestStartOptions Options { get; init; }
            public required TestRecord Record { get; init; }
            public required CancellationTokenSource Cancellation { get; init; }
        }

        private sealed class StageExecutionResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public double? PressureStart { get; init; }
            public double? PressureEnd { get; init; }
            public double? PressureDrop { get; init; }
            public string? PressureUnit { get; init; }

            public static StageExecutionResult Pass(string message, double? pressureStart = null, double? pressureEnd = null, double? pressureDrop = null, string? pressureUnit = null)
                => new() { Success = true, Message = message, PressureStart = pressureStart, PressureEnd = pressureEnd, PressureDrop = pressureDrop, PressureUnit = pressureUnit };

            public static StageExecutionResult Fail(string message, double? pressureStart = null, double? pressureEnd = null, double? pressureDrop = null, string? pressureUnit = null)
                => new() { Success = false, Message = message, PressureStart = pressureStart, PressureEnd = pressureEnd, PressureDrop = pressureDrop, PressureUnit = pressureUnit };
        }
    }
}
