// Services/TestService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.Services
{
    public class TestService : IDisposable
    {
        private readonly IPLCService _plcService;
        private readonly CommService _commService;
        private readonly SensorModbusService _sensorService;
        private readonly PressureModbusService _pressureService;
        private readonly ILogService _logService;
        private readonly Dictionary<int, ChannelTestContext> _activeChannels = new();
        private readonly Dictionary<string, int> _barcodeHistory = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _syncRoot = new();
        private readonly Random _random = new();
        private readonly object _systemBitLock = new();
        private readonly Dictionary<string, bool> _systemBits = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public event EventHandler<TestStageChangedEventArgs>? OnTestStageChanged;
        public event EventHandler<TestRecord>? OnTestCompleted;
        public event EventHandler<TestMessageEventArgs>? OnTestMessage;
        public event EventHandler<ChannelTestResultEventArgs>? OnTestResultDisplay;
        public event EventHandler<MassagePointSampleEventArgs>? OnMassagePointSampled;

        public TestService(IPLCService plcService, CommService commService, SensorModbusService sensorService)
            : this(plcService, commService, sensorService, LogService.Instance)
        {
        }

        public TestService(IPLCService plcService, CommService commService, SensorModbusService sensorService, ILogService? logService)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _commService = commService ?? throw new ArgumentNullException(nameof(commService));
            _sensorService = sensorService ?? throw new ArgumentNullException(nameof(sensorService));
            _logService = logService ?? LogService.Instance;
            _pressureService = new PressureModbusService(SerialPortConfig.CreateDefaultDevice2(), _logService);
        }

        public void ConfigurePressureModule(AppConfig config)
        {
            _pressureService.UpdateConfig(config);
        }

        private void RaiseTestMessage(string message, int? channel = null)
        {
            OnTestMessage?.Invoke(this, new TestMessageEventArgs(message ?? string.Empty, channel));
        }

        private void RaiseMassagePointSampled(int channel, bool[] states)
        {
            OnMassagePointSampled?.Invoke(this, new MassagePointSampleEventArgs(channel, states));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TestService));
            }
        }

        public async Task<bool> StartTestAsync(TestStartOptions options)
        {
            ThrowIfDisposed();

            if (options == null) throw new ArgumentNullException(nameof(options));

            if (options.Model == null)
            {
                RaiseTestMessage("鏈€夋嫨浜у搧鍨嬪彿");
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.WorkOrder))
            {
                RaiseTestMessage("閿欒锛氬伐鍗曞彿涓嶈兘涓虹┖");
                return false;
            }

            options.Barcode = CodeScanService.SanitizeBarcode(options.Barcode);

            if (options.Model.ProcessConfig.EnableBarcodeCheck && string.IsNullOrWhiteSpace(options.Barcode))
            {
                RaiseTestMessage("閿欒锛氶渶鎵弿浜у搧浜岀淮鐮?);
                return false;
            }

            // 鏈哄瀷鏈惎鐢ㄨ叞鎵樻祴璇曟椂锛屼笉璇诲彇缂栫爜鍣ㄣ€?
            _sensorService.EnableHeightReading = options.Model.ProcessConfig?.EnableLumbarTest ?? true;

            ChannelConfig channelConfig = options.Channel switch
            {
                1 => options.Model.Channel1Config,
                2 => options.Model.Channel2Config,
                3 => options.Model.Channel3Config,
                4 => options.Model.Channel4Config,
                _ => null
            };
            if (channelConfig == null)
            {
                RaiseTestMessage($"閫氶亾{options.Channel}缂哄皯閰嶇疆", options.Channel);
                return false;
            }

            lock (_syncRoot)
            {
                if (_activeChannels.ContainsKey(options.Channel))
                {
                    RaiseTestMessage($"閫氶亾{options.Channel}姝ｅ湪娴嬭瘯涓?, options.Channel);
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
                TestVoltage = 13.5,
                Result = TestResult.Testing
            };

            int duplicateCount = RegisterBarcode(options.Barcode);
            record.TestCount = duplicateCount;
            record.DuplicateCount = duplicateCount;

            var currentSleepConfig = options.Model.CurrentSleepConfig ?? CurrentSleepConfig.FromChannel(channelConfig);
            var context = new ChannelTestContext
            {
                Channel = options.Channel,
                Model = options.Model,
                ChannelConfig = channelConfig,
                CurrentSleepConfig = currentSleepConfig,
                Options = options,
                Record = record,
                Cancellation = cts
            };

            bool shouldSetAutoTestFlag;
            lock (_syncRoot)
            {
                _activeChannels[options.Channel] = context;
                shouldSetAutoTestFlag = _activeChannels.Count == 1;
            }

            RaiseTestMessage($"閫氶亾{options.Channel}寮€濮嬫祴璇曪紝鏉＄爜:{options.Barcode}", options.Channel);

            try
            {
                return await RunChannelTestAsync(context);
            }
            finally
            {
                bool shouldResetAutoTestFlag;

                lock (_syncRoot)
                {
                    _activeChannels.Remove(options.Channel);
                    shouldResetAutoTestFlag = _activeChannels.Count == 0;
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
            if (_disposed)
            {
                return;
            }

            List<ChannelTestContext> contexts;

            lock (_syncRoot)
            {
                if (_activeChannels.Count == 0)
                {
                    return;
                }

                contexts = _activeChannels.Values.ToList();
            }

            foreach (var context in contexts)
            {
                try
                {
                    context.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 宸茬粡閲婃斁
                }
            }

            lock (_syncRoot)
            {
                _activeChannels.Clear();
            }

        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            StopAllTests();
            _barcodeHistory.Clear();
            _pressureService.Dispose();
            _disposed = true;

            GC.SuppressFinalize(this);
        }

        private int RegisterBarcode(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return 1;

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

        private async Task<bool> RunChannelTestAsync(ChannelTestContext context)
        {
            try
            {
                if (!await ExecuteStageAsync(context, TestStage.Standby, EnsureStandbyAsync))
                    return await FailAsync(context, "待机状态检查失败");

                if (!await ExecuteStageAsync(context, TestStage.ScanBarcode, ConfirmBarcodeAsync))
                    return await FailAsync(context, "扫码验证失败");

                if (!await ExecuteStageAsync(context, TestStage.StartTest, BeginAirLeakTestAsync))
                    return await FailAsync(context, "启动气密性测试失败");

                if (!await ExecuteStageAsync(context, TestStage.HighPressureInflate, PerformHighPressureInflateAsync))
                    return await FailAsync(context, "高压进气失败");

                if (!await ExecuteStageAsync(context, TestStage.HighPressureStabilize, PerformHighPressureStabilizeAsync))
                    return await FailAsync(context, "高压静置失败");

                if (!await ExecuteStageAsync(context, TestStage.HighPressureLeakCheck, PerformHighPressureLeakCheckAsync))
                {
                    await ExecuteStageAsync(context, TestStage.HighPressureExhaust, PerformHighPressureExhaustAsync).ConfigureAwait(false);
                    return await FailAsync(context, BuildStageFailReason(context, TestStage.HighPressureLeakCheck, "高压气密性不合格"));
                }

                if (!await ExecuteStageAsync(context, TestStage.HighPressureExhaust, PerformHighPressureExhaustAsync))
                    return await FailAsync(context, "高压排气失败");

                if (!await ExecuteStageAsync(context, TestStage.LowPressureInflate, PerformLowPressureInflateAsync))
                    return await FailAsync(context, "低压进气失败");

                if (!await ExecuteStageAsync(context, TestStage.LowPressureStabilize, PerformLowPressureStabilizeAsync))
                    return await FailAsync(context, "低压静置失败");

                if (!await ExecuteStageAsync(context, TestStage.LowPressureLeakCheck, PerformLowPressureLeakCheckAsync))
                {
                    await ExecuteStageAsync(context, TestStage.LowPressureExhaust, PerformLowPressureExhaustAsync).ConfigureAwait(false);
                    return await FailAsync(context, BuildStageFailReason(context, TestStage.LowPressureLeakCheck, "低压气密性不合格"));
                }

                if (!await ExecuteStageAsync(context, TestStage.LowPressureExhaust, PerformLowPressureExhaustAsync))
                    return await FailAsync(context, "低压排气失败");

                await ExecuteStageAsync(context, TestStage.Completed, CompleteStageAsync);
                await FinalizeTest(context, true, "气密性测试完成");
                return true;
            }
            catch (OperationCanceledException)
            {
                await FinalizeTest(context, false, "测试被终止", aborted: true);
                return false;
            }
            catch (Exception ex)
            {
                await FinalizeTest(context, false, $"测试异常: {ex.Message}");
                return false;
            }
        }
        private Task<StageExecutionResult> BeginAirLeakTestAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(StageExecutionResult.Pass($"通道{context.Channel}气密性测试启动"));
        }

        private Task<StageExecutionResult> PerformHighPressureInflateAsync(ChannelTestContext context, CancellationToken token)
        {
            return InflateAsync(context, highPressure: true, token);
        }

        private Task<StageExecutionResult> PerformLowPressureInflateAsync(ChannelTestContext context, CancellationToken token)
        {
            return InflateAsync(context, highPressure: false, token);
        }

        private Task<StageExecutionResult> PerformHighPressureStabilizeAsync(ChannelTestContext context, CancellationToken token)
        {
            int delay = Math.Max(0, GetAirLeakSettings(context).HighStabilizeDurationMs);
            return DelayStageAsync($"高压静置{delay}ms", delay, token);
        }

        private Task<StageExecutionResult> PerformLowPressureStabilizeAsync(ChannelTestContext context, CancellationToken token)
        {
            int delay = Math.Max(0, GetAirLeakSettings(context).LowStabilizeDurationMs);
            return DelayStageAsync($"低压静置{delay}ms", delay, token);
        }

        private Task<StageExecutionResult> PerformHighPressureLeakCheckAsync(ChannelTestContext context, CancellationToken token)
        {
            var settings = GetAirLeakSettings(context);
            return LeakCheckAsync(context, "高压", TestStage.HighPressureLeakCheck, settings.HighDetectDurationMs, settings.HighMaxDropKPa, "KPa", 1.0, token);
        }

        private Task<StageExecutionResult> PerformLowPressureLeakCheckAsync(ChannelTestContext context, CancellationToken token)
        {
            var settings = GetAirLeakSettings(context);
            return LeakCheckAsync(context, "低压", TestStage.LowPressureLeakCheck, settings.LowDetectDurationMs, settings.LowMaxDropPa, "Pa", 1000.0, token);
        }

        private Task<StageExecutionResult> PerformHighPressureExhaustAsync(ChannelTestContext context, CancellationToken token)
        {
            return ExhaustAsync(context, highPressure: true, token);
        }

        private Task<StageExecutionResult> PerformLowPressureExhaustAsync(ChannelTestContext context, CancellationToken token)
        {
            return ExhaustAsync(context, highPressure: false, token);
        }

        private async Task<StageExecutionResult> InflateAsync(ChannelTestContext context, bool highPressure, CancellationToken token)
        {
            var settings = GetAirLeakSettings(context);
            int delay = highPressure ? settings.HighInflateDurationMs : settings.LowInflateDurationMs;
            string name = highPressure ? "高压" : "低压";
            string inlet = GetInletValveAddress(context, highPressure);
            string exhaust = GetExhaustValveAddress(context, highPressure);

            await WriteValveAsync(exhaust, false).ConfigureAwait(false);
            await WriteValveAsync(inlet, true).ConfigureAwait(false);
            RaiseStageChanged(context, highPressure ? TestStage.HighPressureInflate : TestStage.LowPressureInflate,
                StepExecutionState.Running, $"{name}进气中 {delay}ms");
            await Task.Delay(Math.Max(0, delay), token).ConfigureAwait(false);
            await WriteValveAsync(inlet, false).ConfigureAwait(false);
            return StageExecutionResult.Pass($"{name}进气完成");
        }

        private async Task<StageExecutionResult> ExhaustAsync(ChannelTestContext context, bool highPressure, CancellationToken token)
        {
            var settings = GetAirLeakSettings(context);
            int delay = highPressure ? settings.HighExhaustDurationMs : settings.LowExhaustDurationMs;
            string name = highPressure ? "高压" : "低压";
            string inlet = GetInletValveAddress(context, highPressure);
            string exhaust = GetExhaustValveAddress(context, highPressure);

            await WriteValveAsync(inlet, false).ConfigureAwait(false);
            await WriteValveAsync(exhaust, true).ConfigureAwait(false);
            RaiseStageChanged(context, highPressure ? TestStage.HighPressureExhaust : TestStage.LowPressureExhaust,
                StepExecutionState.Running, $"{name}排气中 {delay}ms");
            await Task.Delay(Math.Max(0, delay), token).ConfigureAwait(false);
            await WriteValveAsync(exhaust, false).ConfigureAwait(false);
            return StageExecutionResult.Pass($"{name}排气完成");
        }

        private async Task<StageExecutionResult> DelayStageAsync(string message, int delayMs, CancellationToken token)
        {
            await Task.Delay(Math.Max(0, delayMs), token).ConfigureAwait(false);
            return StageExecutionResult.Pass(message);
        }

        private async Task<StageExecutionResult> LeakCheckAsync(
            ChannelTestContext context,
            string phase,
            TestStage stage,
            int detectDurationMs,
            double limit,
            string unit,
            double pressureScale,
            CancellationToken token)
        {
            var pressureConfig = context.ChannelConfig.PressureConfig ?? new PressureChannelConfig();
            DateTime startTime = DateTime.Now;
            double startKPa = await _pressureService.ReadPressureKPaAsync(context.Channel, pressureConfig, token).ConfigureAwait(false);
            RaiseStageChanged(context, stage, StepExecutionState.Running, $"{phase}起始压力 {(startKPa * pressureScale):F2}{unit}");

            int sampleInterval = Math.Max(50, GetAirLeakSettings(context).PressureSampleIntervalMs);
            int remaining = Math.Max(0, detectDurationMs);
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int slice = Math.Min(sampleInterval, remaining);
                await Task.Delay(slice, token).ConfigureAwait(false);
                remaining -= slice;
            }

            double endKPa = await _pressureService.ReadPressureKPaAsync(context.Channel, pressureConfig, token).ConfigureAwait(false);
            DateTime endTime = DateTime.Now;
            double start = startKPa * pressureScale;
            double end = endKPa * pressureScale;
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

        private AirLeakTestSettings GetAirLeakSettings(ChannelTestContext context)
        {
            return context.ChannelConfig.AirLeakTestSettings ?? new AirLeakTestSettings();
        }

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
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private async Task WriteValveAsync(string? address, bool value)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            await _plcService.WriteBitAsync(address.Trim(), value).ConfigureAwait(false);
        }

        private async Task CloseAllPressureValvesAsync(ChannelTestContext context)
        {
            await WriteValveAsync(GetInletValveAddress(context, true), false).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, true), false).ConfigureAwait(false);
            await WriteValveAsync(GetInletValveAddress(context, false), false).ConfigureAwait(false);
            await WriteValveAsync(GetExhaustValveAddress(context, false), false).ConfigureAwait(false);
        }
        private async Task<bool> FailAsync(ChannelTestContext context, string reason)
        {
            await FinalizeTest(context, false, reason);
            return false;
        }

        private static string BuildStageFailReason(ChannelTestContext context, TestStage stage, string fallback)
        {
            var message = context.Record.StageResults.LastOrDefault(result => result.Stage == stage)?.Message;
            return string.IsNullOrWhiteSpace(message) ? fallback : message;
        }

        private async Task FinalizeTest(ChannelTestContext context, bool success, string message, bool aborted = false)
        {
            var record = context.Record;
            record.Result = success ? TestResult.Pass : aborted ? TestResult.Aborted : TestResult.Fail;
            record.FailReason = success ? string.Empty : message;
            record.TestDuration = (DateTime.Now - record.TestTime).TotalSeconds;
            record.WasAborted = aborted;

            await CloseAllPressureValvesAsync(context).ConfigureAwait(false);
            await SendStopMessageWithDelayAsync(context).ConfigureAwait(false);
            await ResetModeSwitchAsync(context).ConfigureAwait(false);
            await ResetClampCylinderAsync(context).ConfigureAwait(false);

            if (success)
            {
                EnsureCompletionStage(context, TestStage.Completed, StepExecutionState.Passed, message);
            }
            else if (aborted)
            {
                EnsureCompletionStage(context, TestStage.Aborted, StepExecutionState.Failed, message);
            }
            else
            {
                EnsureCompletionStage(context, TestStage.Completed, StepExecutionState.Failed, message);
            }

            OnTestResultDisplay?.Invoke(this, new ChannelTestResultEventArgs
            {
                Channel = context.Channel,
                IsOk = success
            });

            OnTestCompleted?.Invoke(this, record);
            RaiseTestMessage($"閫氶亾{context.Channel}娴嬭瘯缁撴潫: {message}", context.Channel);
        }

        private async Task SendStopMessageWithDelayAsync(ChannelTestContext context)
        {
            try
            {
                bool sent = await _commService.SendStopMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
                if (sent)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                RaiseTestMessage($"閫氶亾{context.Channel}鍋滄鎶ユ枃鍙戦€佸け璐? {ex.Message}", context.Channel);
            }
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
                var execution = await action(context, context.Cancellation.Token);
                result.EndTime = DateTime.Now;
                result.Message = execution.Message;
                result.PeakCurrent = execution.PeakCurrent;
                result.AverageCurrent = execution.AverageCurrent;
                result.MeasuredHeight = execution.Height;
                result.HeightSwitchTriggered = execution.HeightSwitchTriggered;
                result.HeightSwitchAddress = execution.HeightSwitchAddress;
                result.HeightSwitchDelaySeconds = execution.HeightSwitchDelay?.TotalSeconds;
                result.CurrentDropDurationSeconds = execution.CurrentDropDuration?.TotalSeconds;
                result.PressureStart = execution.PressureStart;
                result.PressureEnd = execution.PressureEnd;
                result.PressureDrop = execution.PressureDrop;
                result.PressureUnit = execution.PressureUnit;
                result.State = execution.Success ? StepExecutionState.Passed : StepExecutionState.Failed;

                if (execution.SleepCurrent.HasValue)
                    context.Record.SleepCurrent = execution.SleepCurrent;

                if (stage == TestStage.StaticCurrentTest && execution.AverageCurrent.HasValue)
                    context.Record.StaticCurrent = execution.AverageCurrent;

                RaiseStageChanged(context, stage, result.State, result.Message);
                return execution.Success;
            }
            catch (OperationCanceledException)
            {
                result.EndTime = DateTime.Now;
                result.State = StepExecutionState.Failed;
                result.Message = "娴嬭瘯鍙栨秷";
                RaiseStageChanged(context, stage, StepExecutionState.Failed, "娴嬭瘯鍙栨秷");
                throw;
            }
        }

        private void SkipStage(ChannelTestContext context, TestStage stage, string message)
        {
            var result = new TestStageResult
            {
                Stage = stage,
                State = StepExecutionState.Skipped,
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Message = message
            };
            context.Record.StageResults.Add(result);
            RaiseStageChanged(context, stage, StepExecutionState.Skipped, message);
        }

        private void EnsureCompletionStage(ChannelTestContext context, TestStage stage, StepExecutionState state, string? message)
        {
            if (context.Record.StageResults.Any(s => s.Stage == stage))
                return;

            var result = new TestStageResult
            {
                Stage = stage,
                State = state,
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Message = message
            };
            context.Record.StageResults.Add(result);
            RaiseStageChanged(context, stage, state, message);
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

        private Task<StageExecutionResult> EnsureStandbyAsync(ChannelTestContext context, CancellationToken token)
        {
            return Task.FromResult(StageExecutionResult.Pass("寰呮満鐘舵€佹甯?));
        }

        private Task<StageExecutionResult> ConfirmBarcodeAsync(ChannelTestContext context, CancellationToken token)
        {
            if (!context.Model.ProcessConfig.EnableBarcodeCheck)
            {
                return Task.FromResult(StageExecutionResult.Pass("宸茶烦杩囨壂鐮佹鏌?));
            }

            if (context.Model.ProcessConfig.EnableBarcodeCheck && string.IsNullOrWhiteSpace(context.Options.Barcode))
            {
                return Task.FromResult(StageExecutionResult.Fail("鏈壂鎻忔潯鐮?));
            }

            if (context.Model.ProcessConfig.EnableBarcodeCheck && context.Options.Barcode!.Length < 10)
            {
                return Task.FromResult(StageExecutionResult.Fail("鎵爜鏁版嵁闀垮害涓嶈冻10涓瓧绗?));
            }

            if (context.Model.ProcessConfig.EnableBarcodePrefixCheck)
            {
                string expectedPrefix = context.Model.ProcessConfig.BarcodePrefix?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(expectedPrefix))
                {
                    return Task.FromResult(StageExecutionResult.Fail("宸插惎鐢ㄦ壂鐮佸墠缂€妫€鏌ワ紝浣嗘湭閰嶇疆鍓嶇紑鍐呭"));
                }

                if (!context.Options.Barcode!.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(StageExecutionResult.Fail(
                        $"鎵爜鍓嶇紑涓嶅尮閰嶏紝鏈熸湜鍓嶇紑鈥渰expectedPrefix}鈥濓紝瀹為檯鏉＄爜鈥渰context.Options.Barcode}鈥?));
                }
            }

            if (context.Record.DuplicateCount > 1 && context.Model.ProcessConfig.PromptOnDuplicateBarcode && !context.Options.ContinueOnDuplicate)
            {
                return Task.FromResult(StageExecutionResult.Fail($"閲嶅鏉＄爜锛岀{context.Record.DuplicateCount}娆℃祴璇曞凡鍙栨秷"));
            }

            string message = context.Record.DuplicateCount > 1
                ? $"閲嶅娴嬭瘯绗瑊context.Record.DuplicateCount}娆?
                : "鎵爜鎴愬姛";
            return Task.FromResult(StageExecutionResult.Pass(message));
        }

        private async Task<StageExecutionResult> StartEquipmentAsync(ChannelTestContext context, CancellationToken token)
        {
            string clampCylinderAddress = context.ChannelConfig?.ManualControl?.ClampCylinderAddress ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(clampCylinderAddress))
            {
                try
                {
                    await WriteConfiguredBitAsync(clampCylinderAddress, true).ConfigureAwait(false);
                    context.ClampCylinderActive = true;
                }
                catch (Exception ex)
                {
                    return StageExecutionResult.Fail($"澶圭揣姘旂几閫氱數澶辫触: {ex.Message}");
                }
            }

            await Task.Delay(200, token);
            return StageExecutionResult.Pass("娴嬭瘯璁惧宸插惎鍔?);
        }

        private async Task<StageExecutionResult> PerformSleepTestAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            bool sent = await _commService.SendSleepMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
            if (!sent)
            {
                return StageExecutionResult.Fail("浼戠湢鎸囦护鍙戦€佸け璐?);
            }

            await Task.Delay(500, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            await _commService.ClearChannelSendBufferAsync(context.Channel).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            int timeout = Math.Max(100, context.CurrentSleepConfig.SleepTestTimeout);
            var endTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout);
            const int sampleInterval = 100;
            double threshold = context.CurrentSleepConfig.SleepCurrentThreshold;
            double? lastValue = null;

            while (DateTime.UtcNow <= endTime)
            {
                token.ThrowIfCancellationRequested();

                double currentMilliAmps = await ReadCurrentMilliAmpsAsync(context.Channel, token)
                    .ConfigureAwait(false);

                RecordCurrentSample(context, "浼戠湢娴嬭瘯", currentMilliAmps);

                lastValue = currentMilliAmps;

                if (currentMilliAmps <= threshold)
                {
                    string passMessage = $"浼戠湢鐢垫祦 {currentMilliAmps:F2}mA 浣庝簬闃堝€?{threshold:F2}mA";
                    return StageExecutionResult.Pass(passMessage, sleepCurrent: currentMilliAmps);
                }

                if (DateTime.UtcNow >= endTime)
                {
                    break;
                }

                await Task.Delay(sampleInterval, token).ConfigureAwait(false);
            }

            if (!lastValue.HasValue)
            {
                return StageExecutionResult.Fail("鏈鍙栧埌浼戠湢鐢垫祦鏁版嵁");
            }

            string failMessage = $"浼戠湢鐢垫祦 {lastValue.Value:F2}mA 瓒呰繃闃堝€?{threshold:F2}mA";
            return StageExecutionResult.Fail(failMessage, sleepCurrent: lastValue);
        }

        private async Task<StageExecutionResult> PerformStaticCurrentTestAsync(ChannelTestContext context, CancellationToken token)
        {
            bool messageSent = await _commService.SendReadMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
            if (!messageSent)
            {
                return StageExecutionResult.Fail("闈欐€佺數娴佽鍙栨姤鏂囧彂閫佸け璐?);
            }

            var samples = new List<double>();
            var endTime = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            const int sampleInterval = 100;

            while (DateTime.UtcNow <= endTime)
            {
                token.ThrowIfCancellationRequested();

                double currentMilliAmps = await ReadCurrentMilliAmpsAsync(context.Channel, token)
                    .ConfigureAwait(false);

                samples.Add(currentMilliAmps);
                RecordCurrentSample(context, "闈欐€佺數娴?, currentMilliAmps);

                if (DateTime.UtcNow >= endTime)
                {
                    break;
                }

                await Task.Delay(sampleInterval, token).ConfigureAwait(false);
            }

            if (samples.Count == 0)
            {
                return StageExecutionResult.Fail("鏈鍙栧埌闈欐€佺數娴佹暟鎹?);
            }

            double average = samples.Average();
            bool overLimit = average > context.CurrentSleepConfig.CurrentOverLimit;
            if (overLimit)
            {
                string overLimitMessage = $"闈欐€佺數娴?骞冲潎 {average:F2}mA 瓒呰繃涓婇檺 {context.CurrentSleepConfig.CurrentOverLimit:F2}mA";
                return StageExecutionResult.Fail(overLimitMessage, avg: average);
            }

            bool inRange = average >= context.CurrentSleepConfig.StaticCurrentMin && average <= context.CurrentSleepConfig.StaticCurrentMax;
            string message = $"闈欐€佺數娴?骞冲潎 {average:F2}mA";

            return inRange
                ? StageExecutionResult.Pass(message, avg: average)
                : StageExecutionResult.Fail($"{message} 瓒呭嚭鑼冨洿 {context.CurrentSleepConfig.StaticCurrentMin:F2}-{context.CurrentSleepConfig.StaticCurrentMax:F2}mA", avg: average);
        }

        private async Task<StageExecutionResult> PerformStatusMessageCheckAsync(ChannelTestContext context, CancellationToken token)
        {
            var messageConfig = context.ChannelConfig?.MessageConfig;
            if (messageConfig?.ReadMessage == null || messageConfig.ReadMessage.Length == 0)
            {
                return StageExecutionResult.Fail("鏈厤缃姸鎬佽鍙栨姤鏂?);
            }

            var keyTests = context.ChannelConfig?.MessageKeyTestConfigs?
                .Where(x => x is { Enabled: true })
                .OrderBy(x => x.Order)
                .Take(8)
                .ToList() ?? new List<MessageKeyTestConfig>();

            if (!keyTests.Any())
            {
                return StageExecutionResult.Pass("鏈厤缃寜閿姤鏂囨祴璇曢」锛屽凡璺宠繃");
            }

            foreach (var item in keyTests)
            {
                token.ThrowIfCancellationRequested();
                int byteIndex = item.ReadByteIndex;
                int expectedValue = Math.Clamp(item.ExpectedValue, 0, byte.MaxValue);
                string outputAddress = item.OutputRegisterAddress?.Trim() ?? string.Empty;
                bool enableOutput = !string.IsNullOrWhiteSpace(outputAddress);
                string triggerText = item.TriggerMode == MessageKeyTriggerMode.Interval ? "闂撮殧(寮€1绉掑叧1绉?" : "杩炵画";

                if (byteIndex <= 0)
                {
                    return StageExecutionResult.Fail($"鎸夐敭椤箋item.Order}璇诲彇瀛楄妭搴忓彿鏃犳晥");
                }

                RaiseTestMessage(
                    $"閫氶亾{context.Channel} 寮€濮嬫寜閿姤鏂囨祴璇昜{item.Order}] 瀛楄妭#{byteIndex} 鏈熸湜=0x{expectedValue:X2} 瑙﹀彂={triggerText}",
                    context.Channel);

                bool outputState = false;
                DateTime startTime = DateTime.UtcNow;
                DateTime nextReadSendAt = startTime;
                DateTime nextToggleAt = startTime;
                DateTime baseline = _commService.TryGetLatestMessage(context.Channel, out var snapshot)
                    ? snapshot.ReceivedAt
                    : DateTime.MinValue;
                int? lastReportedValue = null;
                bool hasResponse = false;

                try
                {
                    if (enableOutput)
                    {
                        outputState = true;
                        await WriteConfiguredBitAsync(outputAddress, true).ConfigureAwait(false);
                    }

                    while (DateTime.UtcNow - startTime <= TimeSpan.FromSeconds(5))
                    {
                        token.ThrowIfCancellationRequested();

                        if (DateTime.UtcNow >= nextReadSendAt)
                        {
                            bool sent = await _commService.SendReadMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
                            if (!sent)
                            {
                                return StageExecutionResult.Fail($"鎸夐敭椤箋item.Order}鐘舵€佹姤鏂囪鍙栧懡浠ゅ彂閫佸け璐?);
                            }

                            nextReadSendAt = DateTime.UtcNow.AddMilliseconds(200);
                        }

                        if (enableOutput && item.TriggerMode == MessageKeyTriggerMode.Interval && DateTime.UtcNow >= nextToggleAt)
                        {
                            outputState = !outputState;
                            await WriteConfiguredBitAsync(outputAddress, outputState).ConfigureAwait(false);
                            nextToggleAt = DateTime.UtcNow.AddSeconds(1);
                        }

                        if (_commService.TryGetLatestMessage(context.Channel, out var latestSnapshot)
                            && latestSnapshot.ReceivedAt > baseline
                            && latestSnapshot.Payload != null)
                        {
                            hasResponse = true;
                            byte[] payload = latestSnapshot.Payload;
                            int target = byteIndex - 1;
                            if (target >= payload.Length)
                            {
                                return StageExecutionResult.Fail($"鎸夐敭椤箋item.Order}瀛楄妭搴忓彿{byteIndex}瓒呭嚭鍝嶅簲闀垮害{payload.Length}");
                            }

                            int actual = payload[target];
                            if (lastReportedValue != actual)
                            {
                                RaiseTestMessage($"閫氶亾{context.Channel} 鎸夐敭椤箋item.Order}璇诲彇鍊?0x{actual:X2}", context.Channel);
                                lastReportedValue = actual;
                            }

                            if (actual == expectedValue)
                            {
                                RaiseTestMessage($"閫氶亾{context.Channel} 鎸夐敭椤箋item.Order}閫氳繃", context.Channel);
                                break;
                            }
                        }

                        await Task.Delay(100, token).ConfigureAwait(false);
                    }

                    if (lastReportedValue == expectedValue)
                    {
                        continue;
                    }

                    string responseState = hasResponse
                        ? $"鏈€杩戝€?x{(lastReportedValue ?? 0):X2}"
                        : "鏈敹鍒扮姸鎬佹姤鏂?;
                    return StageExecutionResult.Fail($"鎸夐敭椤箋item.Order}5绉掑唴鏈尮閰嶆湡鏈涘€?x{expectedValue:X2}锛寋responseState}");
                }
                finally
                {
                    if (enableOutput)
                    {
                        try
                        {
                            await WriteConfiguredBitAsync(outputAddress, false).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            RaiseTestMessage($"閫氶亾{context.Channel} 鎸夐敭椤箋item.Order}澶嶄綅杈撳嚭澶辫触: {ex.Message}", context.Channel);
                        }
                    }
                }
            }

            return StageExecutionResult.Pass($"鎸夐敭鎶ユ枃娴嬭瘯閫氳繃锛屽叡{keyTests.Count}椤?);
        }

        private async Task<StageExecutionResult> PerformLumbarTestAsync(ChannelTestContext context, CancellationToken token)
        {
            var enabledConfigs = context.ChannelConfig.LumbarTestConfigs
                .Select((config, index) => new { config, index })
                .Where(x => x.config is { Enabled: true })
                .Select(x => new { Config = x.config!, x.index })
                .OrderBy(x => x.Config.Order)
                .ThenBy(x => x.index)
                .Select(x => x.Config)
                .ToList();

            if (!enabledConfigs.Any())
            {
                return StageExecutionResult.Pass("鏈厤缃叞鎵樺姩浣?);
            }

            // 鏌ユ壘鍚屾椂鏀炬皵閰嶇疆锛岀敤浜庡垵濮嬮珮搴﹁繃楂樻椂涓诲姩鏀炬皵
            var deflateConfig = enabledConfigs.FirstOrDefault(c =>
                c.Action == LumbarActionType.SimultaneousDeflate
                && c.SendMessage != null
                && c.SendMessage.Length > 0);

            var initialHeightValidation = await ValidateLumbarInitialHeightAsync(context, deflateConfig, token).ConfigureAwait(false);
            if (!initialHeightValidation.Passed)
            {
                return StageExecutionResult.Fail(initialHeightValidation.Message);
            }

            var stageCurrents = new List<double>();
            double? lastMeasuredHeight = null;

            for (int actionIndex = 0; actionIndex < enabledConfigs.Count; actionIndex++)
            {
                var config = enabledConfigs[actionIndex];
                int actionOrder = config.Order;
                string actionLabel = config.Action.FormatWithOrder(actionOrder);
                bool isLastAction = actionIndex == enabledConfigs.Count - 1;
                token.ThrowIfCancellationRequested();

                // 璇︾粏鐨勫姩浣滀俊鎭緭鍑?
                RaiseTestMessage(
                    $"寮€濮媨actionLabel}锛岀洰鏍囬珮搴config.TargetHeight}mm锛岃秴鏃秢config.TargetTime}ms锛?x鍦板潃:{config.MRegisterAddress}",
                    context.Channel);

                if (config.SendMessage == null || config.SendMessage.Length == 0)
                {
                    return StageExecutionResult.Fail($"{actionLabel}鎶ユ枃鏈厤缃?);
                }

                bool isFrameHeaderSwitch = config.Action == LumbarActionType.FrameHeaderSwitch;
                var channelPayload = CommService.PrepareChannelMessagePayload(context.Channel, config.SendMessage);

                if (isFrameHeaderSwitch)
                {
                    // 甯уご鍒囨崲澶勭悊閫昏緫淇濇寔涓嶅彉
                    var frameSwitchCurrents = new List<double>();
                    DateTime frameSwitchStart = DateTime.UtcNow;
                    DateTime frameSwitchDeadline = frameSwitchStart + TimeSpan.FromSeconds(1);
                    const int frameSwitchSampleInterval = 100;
                    CancellationTokenSource? frameSendCts = null;
                    Task? frameSendTask = null;
                    try
                    {
                        if (!await _commService.SendMessageAsync(context.Channel, channelPayload).ConfigureAwait(false))
                        {
                            return StageExecutionResult.Fail($"{actionLabel}鎶ユ枃鍙戦€佸け璐?);
                        }

                        frameSendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        frameSendTask = SendChannelMessageLoopAsync(
                            context.Channel,
                            channelPayload,
                            actionLabel,
                            frameSendCts.Token);

                        while (DateTime.UtcNow <= frameSwitchDeadline)
                        {
                            token.ThrowIfCancellationRequested();

                            var (height, current) = await ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig).ConfigureAwait(false);
                            frameSwitchCurrents.Add(current);
                            stageCurrents.Add(current);
                            context.Record.LumbarCurrents.Add(current);
                            RecordCurrentSample(context, actionLabel, current);

                            RaiseStageChanged(context, TestStage.LumbarTest, StepExecutionState.Running,
                                $"{actionLabel} 甯уご鍒囨崲鐢垫祦: {current:F1} mA");

                            await Task.Delay(frameSwitchSampleInterval, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return StageExecutionResult.Fail($"{actionLabel}鎵ц寮傚父: {ex.Message}");
                    }
                    finally
                    {
                        if (frameSendCts != null)
                        {
                            frameSendCts.Cancel();
                        }

                        if (frameSendTask != null)
                        {
                            try
                            {
                                await frameSendTask.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // expected
                            }
                        }

                        frameSendCts?.Dispose();
                    }

                    if (frameSwitchCurrents.Count == 0)
                    {
                        return StageExecutionResult.Fail($"{actionLabel}鏈噰闆嗗埌鐢垫祦鏁版嵁");
                    }

                    int frameSwitchActualTime = (int)Math.Round((DateTime.UtcNow - frameSwitchStart).TotalMilliseconds);
                    double frameSwitchPeakCurrent = frameSwitchCurrents.Max();
                    double frameSwitchAverageCurrent = frameSwitchCurrents.Average();
                    bool frameSwitchCurrentOk = frameSwitchPeakCurrent <= context.CurrentSleepConfig.CurrentOverLimit;

                    var frameSwitchResult = new LumbarActionResult
                    {
                        Order = actionOrder,
                        Action = config.Action,
                        TargetHeight = config.TargetHeight,
                        ActualHeight = null,
                        TargetTime = 1000,
                        ActualTime = frameSwitchActualTime,
                        PeakCurrent = frameSwitchPeakCurrent,
                        AverageCurrent = frameSwitchAverageCurrent,
                        Passed = frameSwitchCurrentOk,
                        Message = frameSwitchCurrentOk ? "甯уご鍒囨崲瀹屾垚" : "鐢垫祦瓒呴檺"
                    };

                    context.Record.LumbarResults.Add(frameSwitchResult);

                    if (!frameSwitchCurrentOk)
                    {
                        return StageExecutionResult.Fail($"{actionLabel}鐢垫祦瓒呴檺",
                            peak: frameSwitchPeakCurrent, avg: frameSwitchAverageCurrent);
                    }

                    if (!isLastAction)
                    {
                        await PauseBetweenLumbarActionsAsync(token).ConfigureAwait(false);
                    }

                    continue;
                }

                var (commandAddress, statusAddresses) = ParseLumbarAddresses(config, context.Channel);

                var actionCurrents = new List<double>();
                var heightSamples = new List<double>();
                bool[] statusTriggered = statusAddresses.Select(_ => false).ToArray();
                bool feedbackReached = statusAddresses.Length == 0;
                DateTime actionStart = DateTime.UtcNow;
                DateTime? feedbackTime = null;
                bool heightTargetReached = false;
                DateTime? heightReachedTime = null;
                TimeSpan timeout = TimeSpan.FromMilliseconds(Math.Max(100, config.TargetTime));
                DateTime deadline = actionStart + timeout;
                const int sampleInterval = 100;
                bool commandActivated = false;
                double lastCurrent = 0;
                double lastHeight = 0;
                CancellationTokenSource? actionSendCts = null;
                Task? actionSendTask = null;

                try
                {
                    if (!string.IsNullOrWhiteSpace(commandAddress))
                    {
                        await WriteConfiguredBitAsync(commandAddress, true).ConfigureAwait(false);
                        commandActivated = true;
                    }

                    if (!await _commService.SendMessageAsync(context.Channel, channelPayload).ConfigureAwait(false))
                    {
                        return StageExecutionResult.Fail($"{actionLabel}鎶ユ枃鍙戦€佸け璐?);
                    }

                    actionSendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    actionSendTask = SendChannelMessageLoopAsync(
                        context.Channel,
                        channelPayload,
                        actionLabel,
                        actionSendCts.Token);

                    var (initialHeight, initialCurrent) = await ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig).ConfigureAwait(false);

                    int sampleCount = 0;
                    while (DateTime.UtcNow <= deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        sampleCount++;

                        var (height, current) = await ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig).ConfigureAwait(false);
                        lastHeight = height;
                        lastCurrent = current;
                        heightSamples.Add(height);
                        actionCurrents.Add(current);
                        stageCurrents.Add(current);
                        context.Record.LumbarCurrents.Add(current);
                        RecordCurrentSample(context, actionLabel, current);

                        string progressMessage = $"{actionLabel} 瀹炴椂楂樺害: {height:F1}mm / 鐩爣: {config.TargetHeight:F1}mm锛岀數娴? {current:F1}mA";
                        RaiseStageChanged(context, TestStage.LumbarTest, StepExecutionState.Running, progressMessage);

                        bool meetsHeightCondition = false;

                        switch (config.Action)
                        {
                            case LumbarActionType.SimultaneousDeflate:
                                meetsHeightCondition = height <= config.TargetHeight;
                                break;
                            case LumbarActionType.FrameHeaderSwitch:
                                meetsHeightCondition = false;
                                break;
                            default:
                                meetsHeightCondition = height >= config.TargetHeight;
                                break;
                        }

                        // 淇锛氫弗鏍肩殑楂樺害杈炬爣妫€鏌?
                        if (!heightTargetReached && meetsHeightCondition)
                        {
                            // 楠岃瘉楂樺害鏁版嵁鍚堢悊鎬?
                            if (config.Action == LumbarActionType.SimultaneousDeflate && height > context.CurrentSleepConfig.HeightRangeMax)
                            {
                                RaiseTestMessage(
                                    $"璀﹀憡锛氬悓鏃舵斁姘斿姩浣滈珮搴﹀紓甯革紒褰撳墠{height:F1}mm 瓒呰繃閰嶇疆楂樺害閲忕▼涓婇檺锛屼絾鍒ゆ柇涓鸿揪鏍囷紝鍙兘鏁版嵁閿欒",
                                    context.Channel);
                                // 涓嶈缃甴eightTargetReached锛岀户缁娴?
                            }
                            else
                            {
                                heightTargetReached = true;
                                heightReachedTime = DateTime.UtcNow;
                                feedbackReached = true;
                                feedbackTime ??= heightReachedTime;

                                string successMessage = config.Action == LumbarActionType.SimultaneousDeflate
                                    ? $"{actionLabel} 鎴愬姛闄嶈嚦鐩爣楂樺害浠ヤ笅 {height:F1}mm"
                                    : $"{actionLabel} 鎴愬姛鍗囪嚦鐩爣楂樺害浠ヤ笂 {height:F1}mm";

                                RaiseStageChanged(context, TestStage.LumbarTest, StepExecutionState.Running, successMessage);
                                break;
                            }
                        }

                        // 鐘舵€佹娴嬮€昏緫
                        if (!feedbackReached && statusAddresses.Length > 0)
                        {
                            for (int i = 0; i < statusAddresses.Length; i++)
                            {
                                if (statusTriggered[i]) continue;
                                bool state = await ReadBitAsync(statusAddresses[i]).ConfigureAwait(false);
                                statusTriggered[i] = state;
                            }

                            if (statusTriggered.All(b => b))
                            {
                                feedbackReached = true;
                                feedbackTime ??= DateTime.UtcNow;
                            }
                        }

                        await Task.Delay(sampleInterval, token).ConfigureAwait(false);
                    }

                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return StageExecutionResult.Fail($"{actionLabel}鎵ц寮傚父: {ex.Message}");
                }
                finally
                {
                    if (actionSendCts != null)
                    {
                        actionSendCts.Cancel();
                    }

                    if (actionSendTask != null)
                    {
                        try
                        {
                            await actionSendTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // expected
                        }
                    }

                    actionSendCts?.Dispose();

                    if (commandActivated)
                    {
                        await WriteConfiguredBitAsync(commandAddress, false).ConfigureAwait(false);
                    }
                }

                // 淇锛氫弗鏍肩殑楂樺害杈炬爣楠岃瘉
                if (!heightTargetReached)
                {
                    string failReason = $"{actionLabel}鏈揪鍒扮洰鏍囬珮搴?(鏈€鍚庨珮搴lastHeight:F1}mm, 鐩爣{config.TargetHeight:F1}mm)";
                    RaiseTestMessage(failReason, context.Channel);
                    return StageExecutionResult.Fail(failReason);
                }

                // 淇锛氭渶缁堥珮搴﹂獙璇?
                double verifiedHeight = lastHeight;
                bool heightVerified = config.Action == LumbarActionType.SimultaneousDeflate
                    ? verifiedHeight <= config.TargetHeight
                    : verifiedHeight >= config.TargetHeight;

                if (!heightVerified)
                {
                    string verificationFail = $"{actionLabel} 鏈€缁堥珮搴﹂獙璇佸け璐?(鏈€缁坽verifiedHeight:F1}mm, 鐩爣{config.TargetHeight:F1}mm)";
                    RaiseTestMessage(verificationFail, context.Channel);
                    return StageExecutionResult.Fail(verificationFail);
                }

                if (actionCurrents.Count == 0)
                {
                    return StageExecutionResult.Fail($"{actionLabel}鏈噰闆嗗埌鐢垫祦鏁版嵁");
                }

                // 璁＄畻瀹為檯鎵ц鏃堕棿
                DateTime measurementCompletion = heightReachedTime ?? feedbackTime ?? DateTime.UtcNow;
                int actualTime = (int)Math.Round((measurementCompletion - actionStart).TotalMilliseconds);
                double evaluationHeight;

                if (heightSamples.Count > 0)
                {
                    evaluationHeight = config.Action == LumbarActionType.SimultaneousDeflate
                        ? heightSamples.Min()
                        : heightSamples.Max();
                }
                else
                {
                    evaluationHeight = verifiedHeight;
                }

                lastMeasuredHeight = evaluationHeight;

                double peakCurrent = actionCurrents.Max();
                double averageCurrent = actionCurrents.Average();

                bool heightOk = heightTargetReached;
                bool timeOk = actualTime <= config.TargetTime;
                bool currentOk = peakCurrent <= context.CurrentSleepConfig.CurrentOverLimit;

                string passMessage = $"楂樺害{evaluationHeight:F1}mm, 鐢ㄦ椂{actualTime}ms";

                var actionResult = new LumbarActionResult
                {
                    Order = actionOrder,
                    Action = config.Action,
                    TargetHeight = config.TargetHeight,
                    ActualHeight = evaluationHeight,
                    TargetTime = config.TargetTime,
                    ActualTime = actualTime,
                    PeakCurrent = peakCurrent,
                    AverageCurrent = averageCurrent,
                    Passed = heightOk && timeOk && currentOk,
                    Message = heightOk && timeOk && currentOk ? passMessage : "娴嬭瘯澶辫触"
                };

                context.Record.LumbarResults.Add(actionResult);

                if (!actionResult.Passed)
                {
                    string failDetail = !heightOk ? "楂樺害鏈揪鏍? : (!timeOk ? "瓒呮椂" : "鐢垫祦瓒呴檺");
                    return StageExecutionResult.Fail($"{actionLabel}{failDetail}",
                        peak: peakCurrent, avg: averageCurrent, height: evaluationHeight);
                }

                if (!isLastAction)
                {
                    await PauseBetweenLumbarActionsAsync(token).ConfigureAwait(false);
                }
            }

            // 闃舵缁熻
            double? stagePeak = stageCurrents.Count > 0 ? stageCurrents.Max() : null;
            double? stageAvg = stageCurrents.Count > 0 ? stageCurrents.Average() : null;
            context.Record.LumbarAverageCurrent = stageAvg;
            context.Record.LumbarMaxCurrent = stagePeak;

            return StageExecutionResult.Pass("鑵版墭娴嬭瘯閫氳繃",
                peak: stagePeak, avg: stageAvg, height: lastMeasuredHeight);
        }

        private async Task<(bool Passed, string Message)> ValidateLumbarInitialHeightAsync(ChannelTestContext context, LumbarTestConfig? deflateConfig, CancellationToken token)
        {
            const double initialHeightThreshold = 10.0;
            TimeSpan sampleInterval = TimeSpan.FromMilliseconds(100);

            var (initialHeight, _) = await ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig).ConfigureAwait(false);

            // 楂樺害宸插湪闃堝€间互涓嬶紝鐩存帴閫氳繃
            if (initialHeight < initialHeightThreshold)
            {
                string passMessage = $"鑵版墭鍒濆楂樺害{initialHeight:F1}mm锛屽皬浜巤initialHeightThreshold:F1}mm锛岀户缁祴璇?;
                RaiseTestMessage($"閫氶亾{context.Channel} {passMessage}", context.Channel);
                return (true, passMessage);
            }

            // 楂樺害楂樹簬闃堝€硷細灏濊瘯閫氳繃鍚屾椂鏀炬皵闄嶄綆楂樺害
            RaiseTestMessage($"閫氶亾{context.Channel} 鑵版墭鍒濆楂樺害{initialHeight:F1}mm锛岄珮浜巤initialHeightThreshold:F1}mm锛屾墽琛屾斁姘旈檷浣?, context.Channel);

            bool hasValidDeflateConfig = deflateConfig != null
                && deflateConfig.SendMessage != null
                && deflateConfig.SendMessage.Length > 0;

            if (!hasValidDeflateConfig)
            {
                string failMessage = $"鑵版墭鍒濆浣嶇疆閿欒锛氶珮搴initialHeight:F1}mm锛屾湭灏忎簬{initialHeightThreshold:F1}mm锛屼笖鏈厤缃斁姘斿姩浣?;
                RaiseTestMessage($"閫氶亾{context.Channel} {failMessage}", context.Channel);
                return (false, failMessage);
            }

            // 鎵ц鍚屾椂鏀炬皵鎸囦护锛坉eflateConfig鍦╤asValidDeflateCheck涓凡纭繚闈炵┖锛?
            var (commandAddress, _) = ParseLumbarAddresses(deflateConfig!, context.Channel);
            var channelPayload = CommService.PrepareChannelMessagePayload(context.Channel, deflateConfig!.SendMessage!);

            bool commandActivated = false;
            CancellationTokenSource? deflateSendCts = null;
            Task? deflateSendTask = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(commandAddress))
                {
                    await WriteConfiguredBitAsync(commandAddress, true).ConfigureAwait(false);
                    commandActivated = true;
                }

                if (!await _commService.SendMessageAsync(context.Channel, channelPayload).ConfigureAwait(false))
                {
                    return (false, "鑵版墭鍒濆鏀炬皵鎸囦护鍙戦€佸け璐?);
                }

                string actionLabel = "鍒濆鏀炬皵";
                deflateSendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                deflateSendTask = SendChannelMessageLoopAsync(
                    context.Channel,
                    channelPayload,
                    actionLabel,
                    deflateSendCts.Token);

                // 绛夊緟鏈€澶?0绉掞紝鐩戞祴楂樺害鏄惁闄嶈嚦闃堝€间互涓?
                TimeSpan waitWindow = TimeSpan.FromSeconds(20);
                DateTime startTime = DateTime.UtcNow;
                DateTime deadline = startTime + waitWindow;
                double? lastHeight = null;

                while (DateTime.UtcNow <= deadline)
                {
                    token.ThrowIfCancellationRequested();

                    var (height, _) = await ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig).ConfigureAwait(false);
                    lastHeight = height;

                    if (height < initialHeightThreshold)
                    {
                        string passMessage = $"鑵版墭鏀炬皵鍚庨珮搴﹂檷鑷硔height:F1}mm锛屽皬浜巤initialHeightThreshold:F1}mm锛岀户缁祴璇?;
                        RaiseTestMessage($"閫氶亾{context.Channel} {passMessage}", context.Channel);
                        return (true, passMessage);
                    }

                    string waitingMessage = $"鑵版墭鏀炬皵涓紝褰撳墠楂樺害{height:F1}mm锛岀瓑寰呴檷鑷硔initialHeightThreshold:F1}mm浠ヤ笅";
                    RaiseStageChanged(context, TestStage.LumbarTest, StepExecutionState.Running, waitingMessage);

                    await Task.Delay(sampleInterval, token).ConfigureAwait(false);
                }

                string failMessage = $"鑵版墭鍒濆浣嶇疆閿欒锛氭斁姘攞waitWindow.TotalSeconds}绉掑悗楂樺害浠嶄负{(lastHeight ?? 0):F1}mm锛屾湭灏忎簬{initialHeightThreshold:F1}mm";
                RaiseTestMessage($"閫氶亾{context.Channel} {failMessage}", context.Channel);
                return (false, failMessage);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, $"鑵版墭鍒濆鏀炬皵寮傚父: {ex.Message}");
            }
            finally
            {
                if (deflateSendCts != null)
                {
                    deflateSendCts.Cancel();
                }

                if (deflateSendTask != null)
                {
                    try
                    {
                        await deflateSendTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected
                    }
                }

                deflateSendCts?.Dispose();

                if (commandActivated)
                {
                    await WriteConfiguredBitAsync(commandAddress, false).ConfigureAwait(false);
                }
            }
        }

        private Task DelayBeforeMassageTestAsync(ChannelTestContext context)
        {
            return Task.Delay(TimeSpan.FromSeconds(1), context.Cancellation.Token);
        }

        private Task PauseBetweenLumbarActionsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private async Task<bool> ActivateManualMassageMessageAsync(ChannelTestContext context)
        {
            var message = context.ChannelConfig?.MessageConfig?.MassageMessage;
            if (message == null || message.Length == 0)
            {
                return false;
            }

            return true;
        }

        private Task DeactivateManualMassageMessageAsync()
        {
            return Task.CompletedTask;
        }

        private async Task ResetModeSwitchAsync(ChannelTestContext context)
        {
            if (context == null)
            {
                return;
            }

            string powerOffAddress = context.ChannelConfig?.ManualControl?.PowerOffAddress ?? string.Empty;
            string driverSwitchAddress = context.ChannelConfig?.ManualControl?.DriverSwitchAddress ?? string.Empty;

            if (context.ModeSwitchPowerOffActive)
            {
                try
                {
                    await WriteConfiguredBitAsync(powerOffAddress, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"閫氶亾{context.Channel}鏂數澶嶄綅澶辫触: {ex.Message}", context.Channel);
                }
                finally
                {
                    context.ModeSwitchPowerOffActive = false;
                }
            }

            if (context.ModeSwitchDriverActive)
            {
                try
                {
                    await WriteConfiguredBitAsync(driverSwitchAddress, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"閫氶亾{context.Channel}妯″紡鍒囨崲澶嶄綅澶辫触: {ex.Message}", context.Channel);
                }
                finally
                {
                    context.ModeSwitchDriverActive = false;
                }
            }
        }

        private async Task ResetClampCylinderAsync(ChannelTestContext context)
        {
            if (context == null)
            {
                return;
            }

            if (!context.ClampCylinderActive)
            {
                return;
            }

            string clampCylinderAddress = context.ChannelConfig?.ManualControl?.ClampCylinderAddress ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clampCylinderAddress))
            {
                context.ClampCylinderActive = false;
                return;
            }

            try
            {
                await WriteConfiguredBitAsync(clampCylinderAddress, false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseTestMessage($"閫氶亾{context.Channel}澶圭揣姘旂几鏂數澶辫触: {ex.Message}", context.Channel);
            }
            finally
            {
                context.ClampCylinderActive = false;
            }
        }

        private async Task<StageExecutionResult> PerformMassageTestAsync(ChannelTestContext context, CancellationToken token)
        {
            bool manualMessageActive = false;

            try
            {
                manualMessageActive = await ActivateManualMassageMessageAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return StageExecutionResult.Fail("鎸夋懇鎶ユ枃鍔犺浇澶辫触: " + ex.Message);
            }

            CancellationTokenSource? massageSendCts = null;
            Task? massageSendTask = null;

            try
            {
                var channelConfig = context.ChannelConfig ?? new ChannelConfig();
                var enabledSteps = (channelConfig.MassageConfigs ?? new List<MassageConfig>())
                    .Where(c => c != null && c.Enabled)
                    .OrderBy(c => c.Point)
                    .ToList();

                if (!enabledSteps.Any())
                {
                    return StageExecutionResult.Pass("鏈厤缃寜鎽╁姩浣?);
                }

                if (!await _commService.SendMassageMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false))
                {
                    return StageExecutionResult.Fail("鎸夋懇鎸囦护鍙戦€佸け璐?);
                }

                massageSendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                massageSendTask = SendMassageMessageLoopAsync(context.Channel, context.ChannelConfig, massageSendCts.Token);

                var settings = channelConfig.MassageTestSettings ?? new MassageTestSettings();

                var executor = new MassageTestExecutor(
                    settings,
                    TimeSpan.FromMilliseconds(100),
                    _ => ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig),
                    (address, _) => ReadBitAsync(address, 1, 0),
                    context.CurrentSleepConfig.CurrentOverLimit,
                    current =>
                    {
                        context.Record.MassageCurrents.Add(current);
                        RecordCurrentSample(context, "鎸夋懇娴嬭瘯", current);
                    },
                    progress => RaiseStageChanged(context, TestStage.MassageTest, StepExecutionState.Running, progress),
                    pointStates => RaiseMassagePointSampled(context.Channel, pointStates));

                var result = await executor.ExecuteAsync(enabledSteps, token).ConfigureAwait(false);

                foreach (var pointResult in result.PointResults)
                {
                    context.Record.MassageResults.Add(pointResult);
                }

                context.Record.MassageAverageCurrent = result.StageAverageCurrent;
                context.Record.MassageMaxCurrent = result.StagePeakCurrent;

                if (!result.Succeeded)
                {
                    return StageExecutionResult.Fail(result.Message, peak: result.StagePeakCurrent, avg: result.StageAverageCurrent);
                }

                return StageExecutionResult.Pass(result.Message, peak: result.StagePeakCurrent, avg: result.StageAverageCurrent);
            }
            finally
            {
                if (massageSendCts != null)
                {
                    massageSendCts.Cancel();
                }

                if (massageSendTask != null)
                {
                    try
                    {
                        await massageSendTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected
                    }
                }

                massageSendCts?.Dispose();

                if (manualMessageActive)
                {
                    await DeactivateManualMassageMessageAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<StageExecutionResult> PerformMasterSlaveDecisionAsync(ChannelTestContext context, CancellationToken token)
        {
            var process = context.Model?.ProcessConfig;
            if (process == null)
            {
                return StageExecutionResult.Fail("缂哄皯妯″紡鍒囨崲閰嶇疆");
            }

            int duration = Math.Max(100, process.ModeSwitchPowerOffDuration);
            string powerOffAddress = context.ChannelConfig?.ManualControl?.PowerOffAddress ?? string.Empty;
            string driverSwitchAddress = context.ChannelConfig?.ManualControl?.DriverSwitchAddress ?? string.Empty;

            bool powerActivated = false;
            bool driverActivated = false;
            bool success = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(powerOffAddress))
                {
                    await WriteConfiguredBitAsync(powerOffAddress, true).ConfigureAwait(false);
                    powerActivated = true;
                    context.ModeSwitchPowerOffActive = true;
                }

                if (!string.IsNullOrWhiteSpace(driverSwitchAddress))
                {
                    await WriteConfiguredBitAsync(driverSwitchAddress, true).ConfigureAwait(false);
                    driverActivated = true;
                    context.ModeSwitchDriverActive = true;
                }

                DateTime endTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(duration);
                while (DateTime.UtcNow < endTime)
                {
                    token.ThrowIfCancellationRequested();
                    int remaining = Math.Max(0, (int)Math.Round((endTime - DateTime.UtcNow).TotalMilliseconds));
                    RaiseStageChanged(context, TestStage.MasterSlaveDecision, StepExecutionState.Running,
                        $"妯″紡鍒囨崲鏂數涓?鍓╀綑 {remaining}ms");
                    await Task.Delay(200, token).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(powerOffAddress))
                {
                    await WriteConfiguredBitAsync(powerOffAddress, false).ConfigureAwait(false);
                }
                context.ModeSwitchPowerOffActive = false;

                success = true;
                return StageExecutionResult.Pass($"妯″紡鍒囨崲瀹屾垚锛屾柇鐢祘duration}ms");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return StageExecutionResult.Fail($"妯″紡鍒囨崲鎺у埗澶辫触: {ex.Message}");
            }
            finally
            {
                if (!success && powerActivated)
                {
                    try
                    {
                        await WriteConfiguredBitAsync(powerOffAddress, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RaiseTestMessage($"閫氶亾{context.Channel}鏂數澶嶄綅澶辫触: {ex.Message}", context.Channel);
                    }
                    finally
                    {
                        context.ModeSwitchPowerOffActive = false;
                    }
                }

                if (!success && driverActivated)
                {
                    try
                    {
                        await WriteConfiguredBitAsync(driverSwitchAddress, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RaiseTestMessage($"閫氶亾{context.Channel}妯″紡鍒囨崲澶嶄綅澶辫触: {ex.Message}", context.Channel);
                    }
                    finally
                    {
                        context.ModeSwitchDriverActive = false;
                    }
                }
            }
        }

        private async Task<StageExecutionResult> PerformMasterModeMassageAsync(ChannelTestContext context, CancellationToken token)
        {
            if (context.ChannelConfig?.MessageConfig?.MassageMessage2 is not { Length: > 0 })
            {
                return StageExecutionResult.Fail("鏈厤缃寜鎽╂姤鏂?");
            }

            if (!await _commService.SendMassageMessage2Async(context.Channel, context.ChannelConfig).ConfigureAwait(false))
            {
                return StageExecutionResult.Fail("鎸夋懇鎶ユ枃2鍙戦€佸け璐?);
            }

            string massageKeyAddress = context.ChannelConfig?.ManualControl?.MassageKeyAddress ?? string.Empty;
            var stageSamples = new List<double>();
            const double currentDropThreshold = 20.0;
            TimeSpan pressureSwitchTimeout = TimeSpan.FromSeconds(5);
            TimeSpan postTriggerDelay = TimeSpan.FromSeconds(1);
            TimeSpan massageKeyPulseDuration = TimeSpan.FromSeconds(1);
            TimeSpan currentMonitorTimeout = TimeSpan.FromSeconds(6);
            TimeSpan requiredStableDuration = TimeSpan.FromSeconds(2);
            const int sampleInterval = 100;
            bool massageKeyActivated = false;

            try
            {
                var pressureSwitchAddresses = context.ChannelConfig?.MassageConfigs?
                    .Where(c => c != null && c.Enabled && !string.IsNullOrWhiteSpace(c.HeightSwitchAddress))
                    .Select(c => c.HeightSwitchAddress.Trim())
                    .Where(address => !string.IsNullOrWhiteSpace(address))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                if (pressureSwitchAddresses.Count == 0)
                {
                    return StageExecutionResult.Fail("鏈厤缃寜鎽╅珮搴﹀紑鍏冲湴鍧€");
                }

                DateTime pressureWaitStart = DateTime.UtcNow;
                string? triggeredSwitch = null;
                DateTime? pressureTriggerTime = null;

                while (DateTime.UtcNow - pressureWaitStart < pressureSwitchTimeout)
                {
                    token.ThrowIfCancellationRequested();

                    foreach (string address in pressureSwitchAddresses)
                    {
                        bool isHigh = await ReadBitAsync(address).ConfigureAwait(false);
                        if (isHigh)
                        {
                            triggeredSwitch = address;
                            pressureTriggerTime = DateTime.UtcNow;
                            break;
                        }
                    }

                    if (triggeredSwitch != null)
                    {
                        break;
                    }

                    string waitingProgress = $"鎸夋懇2娴嬭瘯 绛夊緟楂樺害寮€鍏宠Е鍙?{pressureSwitchTimeout.TotalSeconds:F0}s瓒呮椂)";
                    RaiseStageChanged(context, TestStage.MasterModeMassage, StepExecutionState.Running, waitingProgress);
                    await Task.Delay(sampleInterval, token).ConfigureAwait(false);
                }

                if (triggeredSwitch == null || pressureTriggerTime == null)
                {
                    return StageExecutionResult.Fail($"鎸夋懇楂樺害寮€鍏虫湭鍦▄pressureSwitchTimeout.TotalSeconds:F0}s鍐呰Е鍙?);
                }

                TimeSpan triggerDelay = pressureTriggerTime.Value - pressureWaitStart;
                string triggerMessage = $"楂樺害寮€鍏硔triggeredSwitch}鍦▄triggerDelay.TotalMilliseconds:F0}ms鍐呰Е鍙?;
                RaiseTestMessage($"閫氶亾{context.Channel} {triggerMessage}", context.Channel);
                RaiseStageChanged(context, TestStage.MasterModeMassage, StepExecutionState.Running, triggerMessage);

                try
                {
                    await _commService.ClearChannelSendBufferAsync(context.Channel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return StageExecutionResult.Fail(
                        $"娓呯┖鎸夋懇鎶ユ枃鍙戦€佸尯澶辫触: {ex.Message}",
                        heightSwitchTriggered: true,
                        heightSwitchAddress: triggeredSwitch,
                        heightSwitchDelay: triggerDelay);
                }

                await Task.Delay(postTriggerDelay, token).ConfigureAwait(false);

                try
                {
                    if (!string.IsNullOrWhiteSpace(massageKeyAddress))
                    {
                        await WriteConfiguredBitAsync(massageKeyAddress, true).ConfigureAwait(false);
                        massageKeyActivated = true;
                    }
                }
                catch (Exception ex)
                {
                    return StageExecutionResult.Fail(
                        $"鎸夋懇鎸夐敭缃綅澶辫触: {ex.Message}",
                        heightSwitchTriggered: true,
                        heightSwitchAddress: triggeredSwitch,
                        heightSwitchDelay: triggerDelay);
                }

                await Task.Delay(massageKeyPulseDuration, token).ConfigureAwait(false);

                try
                {
                    if (!string.IsNullOrWhiteSpace(massageKeyAddress))
                    {
                        await WriteConfiguredBitAsync(massageKeyAddress, false).ConfigureAwait(false);
                    }
                    massageKeyActivated = false;
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"閫氶亾{context.Channel}鎸夋懇鎸夐敭澶嶄綅澶辫触: {ex.Message}", context.Channel);
                }

                DateTime monitorStart = DateTime.UtcNow;
                DateTime? belowThresholdStart = null;
                DateTime? dropSatisfiedTime = null;

                while (DateTime.UtcNow - monitorStart < currentMonitorTimeout)
                {
                    token.ThrowIfCancellationRequested();

                    var (_, current) = await ReadHeightAndCurrentAsync(context.Channel, context.CurrentSleepConfig).ConfigureAwait(false);
                    stageSamples.Add(current);
                    context.Record.MassageCurrents.Add(current);
                    RecordCurrentSample(context, "鎸夋懇2娴嬭瘯-鐢垫祦鐩戞帶", current);

                    bool isBelow = current < currentDropThreshold;
                    if (isBelow)
                    {
                        belowThresholdStart ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - belowThresholdStart >= requiredStableDuration)
                        {
                            dropSatisfiedTime = DateTime.UtcNow;
                            break;
                        }
                    }
                    else
                    {
                        belowThresholdStart = null;
                    }

                    string progress = $"鎸夋懇2娴嬭瘯 鐢垫祦: {current:F1} mA (鐩爣 < {currentDropThreshold:F1} mA锛屼繚鎸亄requiredStableDuration.TotalSeconds:F0}s)";
                    RaiseStageChanged(context, TestStage.MasterModeMassage, StepExecutionState.Running, progress);

                    await Task.Delay(sampleInterval, token).ConfigureAwait(false);
                }

                double? stagePeak = stageSamples.Count > 0 ? stageSamples.Max() : (double?)null;
                double? stageAvg = stageSamples.Count > 0 ? stageSamples.Average() : (double?)null;

                if (dropSatisfiedTime == null)
                {
                    TimeSpan dropAttemptDuration = DateTime.UtcNow - monitorStart;
                    return StageExecutionResult.Fail(
                        $"楂樺害寮€鍏硔triggeredSwitch}瑙﹀彂鍚巤currentMonitorTimeout.TotalSeconds:F0}s鍐呯數娴佹湭绋冲畾浣庝簬{currentDropThreshold:F1}mA",
                        peak: stagePeak,
                        avg: stageAvg,
                        heightSwitchTriggered: true,
                        heightSwitchAddress: triggeredSwitch,
                        heightSwitchDelay: triggerDelay,
                        currentDropDuration: dropAttemptDuration);
                }

                TimeSpan dropDuration = dropSatisfiedTime.Value - monitorStart;
                string successMessage =
                    $"楂樺害寮€鍏硔triggeredSwitch}鍦▄triggerDelay.TotalMilliseconds:F0}ms瑙﹀彂锛岀數娴亄dropDuration.TotalMilliseconds:F0}ms闄嶈嚦{currentDropThreshold:F1}mA浠ヤ笅骞朵繚鎸亄requiredStableDuration.TotalSeconds:F0}s";
                RaiseTestMessage($"閫氶亾{context.Channel} {successMessage}", context.Channel);

                return StageExecutionResult.Pass(
                    successMessage,
                    peak: stagePeak,
                    avg: stageAvg,
                    heightSwitchTriggered: true,
                    heightSwitchAddress: triggeredSwitch,
                    heightSwitchDelay: triggerDelay,
                    currentDropDuration: dropDuration);
            }
            finally
            {
                if (massageKeyActivated)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(massageKeyAddress))
                        {
                            await WriteConfiguredBitAsync(massageKeyAddress, false).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        RaiseTestMessage($"閫氶亾{context.Channel}鎸夋懇鎸夐敭澶嶄綅澶辫触: {ex.Message}", context.Channel);
                    }
                }

                try
                {
                    await _commService.SendStopMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"閫氶亾{context.Channel}鍙戦€佸仠姝㈡姤鏂囧け璐? {ex.Message}", context.Channel);
                }

                await ResetModeSwitchAsync(context).ConfigureAwait(false);
            }
        }

        private Task<StageExecutionResult> CompleteStageAsync(ChannelTestContext context, CancellationToken token)
        {
            return Task.FromResult(StageExecutionResult.Pass("鎵€鏈夐」鐩畬鎴?));
        }

        private void RecordCurrentSample(ChannelTestContext context, string stage, double value)
        {
            context.Record.CurrentTimeline.Add(new CurrentMeasurement
            {
                Timestamp = DateTime.Now,
                Stage = stage,
                CurrentValue = value
            });
        }

        private (string CommandAddress, string[] StatusAddresses) ParseLumbarAddresses(LumbarTestConfig config, int channel)
        {
            var configuredAddresses = ModbusAddressHelper.ParseAddressList(config.MRegisterAddress)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalid = new List<string>();

            foreach (var address in configuredAddresses)
            {
                var normalizedToken = ModbusAddressHelper.NormalizeBitAddressToken(address);
                if (!string.IsNullOrWhiteSpace(normalizedToken) &&
                    normalizedToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(normalizedToken))
                    {
                        normalized.Add(normalizedToken);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(address))
                {
                    invalid.Add(address);
                }
            }

            if (invalid.Count > 0)
            {
                string message = $"閫氶亾{channel} 鑵版墭鍔ㄤ綔{config.Order}鍖呭惈鏃犳晥Modbus鍦板潃: {string.Join("銆?, invalid)}";
                if (normalized.Count > 0)
                {
                    message += $"锛屽凡淇濈暀鏈夋晥鍦板潃: {string.Join("銆?, normalized)}";
                }

                RaiseTestMessage(message + "銆?, channel);
            }

            if (normalized.Count > 0)
            {
                config.MRegisterAddress = string.Join(", ", normalized);
            }
            else
            {
                config.MRegisterAddress = string.Empty;
            }

            string commandAddress = normalized.FirstOrDefault() ?? string.Empty;

            var statusAddresses = normalized
                .Where(a => !string.Equals(a, commandAddress, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return (commandAddress, statusAddresses);
        }


        private Task<bool> ReadBitAsync(string address)
        {
            return ReadBitAsync(address, 3, 50);
        }

        private async Task<bool> ReadBitAsync(string address, int maxReadAttempts, int retryDelayMs)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            string normalized = address.Trim();
            if (!IsModbusBitAddress(normalized))
            {
                return GetSystemBit(normalized);
            }

            int attempts = Math.Max(1, maxReadAttempts);
            int delayMs = Math.Max(0, retryDelayMs);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var bits = await _plcService.ReadBitsAsync(normalized, 1).ConfigureAwait(false);
                if (bits != null && bits.Length > 0 && bits[0])
                {
                    return true;
                }

                if (attempt < attempts - 1 && delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }

            return false;
        }

        private async Task<(double Height, double Current)> ReadHeightAndCurrentAsync(int channel, CurrentSleepConfig? currentSleepConfig = null)
        {
            try
            {
                double height = 0;
                double current = 0;
                if (_sensorService.TryGetMeasurement(channel, TimeSpan.FromMilliseconds(1200), out var measurement))
                {
                    if (_sensorService.EnableHeightReading)
                    {
                        height = ConvertHeightRawToMillimeters(measurement.HeightRaw, currentSleepConfig);
                    }
                    current = measurement.CurrentMilliAmps;
                }
                else
                {
                    if (!_sensorService.EnableHeightReading)
                    {
                        var updated = await _sensorService.WaitForMeasurementAsync(
                            channel,
                            TimeSpan.FromMilliseconds(1200),
                            TimeSpan.FromMilliseconds(600),
                            CancellationToken.None).ConfigureAwait(false);
                        if (updated.HasValue)
                        {
                            current = updated.Value.CurrentMilliAmps;
                            return (0, current);
                        }

                        if (_sensorService.TryGetMeasurement(channel, out var lastMeasurement))
                        {
                            var age = DateTime.Now - lastMeasurement.Timestamp;
                            _logService.LogWarning($"閫氶亾{channel}鐢垫祦缂撳瓨杩囨湡({age.TotalMilliseconds:F0}ms)锛屾寜鎽╅樁娈典娇鐢ㄦ棫鍊?);
                            current = lastMeasurement.CurrentMilliAmps;
                            return (0, current);
                        }
                    }

                    if (_sensorService.EnableHeightReading)
                    {
                        int rawHeight = await _sensorService.ReadHeightRawAsync(channel, CancellationToken.None)
                            .ConfigureAwait(false);
                        height = ConvertHeightRawToMillimeters(rawHeight, currentSleepConfig);
                    }

                    current = await _sensorService.ReadCurrentMilliAmpsAsync(channel, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                return (height, current);
            }
            catch (Exception ex)
            {
                RaiseTestMessage($"楂樺害璇诲彇寮傚父: {ex.Message}", channel);
                return (0, 0);
            }
        }



        private static double ConvertHeightRawToMillimeters(int rawHeight, CurrentSleepConfig? currentSleepConfig)
        {
            return currentSleepConfig?.ConvertHeightCodeToMillimeters(rawHeight) ?? rawHeight / 100.0;
        }

        private async Task<double> ReadCurrentMilliAmpsAsync(int channel, CancellationToken token)
        {
            const int maxAgeMs = 1200;
            const int retryDelayMs = 100;
            const int retryCount = 2;

            if (_sensorService.TryGetMeasurement(channel, TimeSpan.FromMilliseconds(maxAgeMs), out var measurement))
            {
                return measurement.CurrentMilliAmps;
            }

            for (int attempt = 0; attempt < retryCount; attempt++)
            {
                await Task.Delay(retryDelayMs, token).ConfigureAwait(false);
                if (_sensorService.TryGetMeasurement(channel, TimeSpan.FromMilliseconds(maxAgeMs), out measurement))
                {
                    return measurement.CurrentMilliAmps;
                }
            }

            var updated = await _sensorService.WaitForMeasurementAsync(
                channel,
                TimeSpan.FromMilliseconds(maxAgeMs),
                TimeSpan.FromMilliseconds(600),
                token).ConfigureAwait(false);
            if (updated.HasValue)
            {
                return updated.Value.CurrentMilliAmps;
            }

            if (_sensorService.TryGetMeasurement(channel, out var lastMeasurement))
            {
                var age = DateTime.Now - lastMeasurement.Timestamp;
                _logService.LogWarning($"閫氶亾{channel}鐢垫祦缂撳瓨杩囨湡({age.TotalMilliseconds:F0}ms)锛屾敼涓虹洿鎺ヨ鍙?);
            }
            else
            {
                _logService.LogWarning($"閫氶亾{channel}鐢垫祦缂撳瓨涓嶅彲鐢紝鏀逛负鐩存帴璇诲彇");
            }

            return await _sensorService.ReadCurrentMilliAmpsAsync(channel, token).ConfigureAwait(false);
        }

        private static ushort GetWordFromPayload(byte[] payload, int wordIndex)
        {
            int offset = wordIndex * 2;
            if (payload == null || offset + 1 >= payload.Length)
            {
                return 0;
            }

            return (ushort)(payload[offset] | (payload[offset + 1] << 8));
        }

        private async Task SendMassageMessageLoopAsync(int channel, ChannelConfig? channelConfig, CancellationToken token)
        {
            if (channelConfig == null)
            {
                return;
            }

            var interval = TimeSpan.FromMilliseconds(200);

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);

                bool sent = await _commService.SendMassageMessageAsync(channel, channelConfig).ConfigureAwait(false);
                if (!sent)
                {
                    RaiseTestMessage($"閫氶亾{channel}鎸夋懇鎶ユ枃鍙戦€佸け璐?, channel);
                }
            }
        }

        private async Task SendChannelMessageLoopAsync(int channel, ushort[] payload, string actionLabel, CancellationToken token)
        {
            var interval = TimeSpan.FromMilliseconds(200);

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);

                bool sent = await _commService.SendMessageAsync(channel, payload).ConfigureAwait(false);
                if (!sent)
                {
                    RaiseTestMessage($"閫氶亾{channel}{actionLabel}鎶ユ枃杩炵画鍙戦€佸け璐?, channel);
                }
            }
        }

        private Task WriteConfiguredBitAsync(string address, bool value)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return Task.CompletedTask;
            }

            if (!IsModbusBitAddress(address))
            {
                SetSystemBit(address, value);
                return Task.CompletedTask;
            }

            return _plcService.WriteBitAsync(address, value);
        }

        private void SetSystemBit(string address, bool value)
        {
            lock (_systemBitLock)
            {
                _systemBits[address] = value;
            }
        }

        private bool GetSystemBit(string address)
        {
            lock (_systemBitLock)
            {
                return _systemBits.TryGetValue(address, out var value) && value;
            }
        }

        private static bool IsModbusBitAddress(string address)
        {
            return address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                   || address.StartsWith("1x", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class MassageTestExecutionResult
        {
            public MassageTestExecutionResult(
                bool succeeded,
                string message,
                IReadOnlyList<MassagePointResult> pointResults,
                IReadOnlyList<double> stageCurrents,
                double? stagePeakCurrent,
                double? stageAverageCurrent)
            {
                Succeeded = succeeded;
                Message = message;
                PointResults = pointResults;
                StageCurrents = stageCurrents;
                StagePeakCurrent = stagePeakCurrent;
                StageAverageCurrent = stageAverageCurrent;
            }

            public bool Succeeded { get; }
            public string Message { get; }
            public IReadOnlyList<MassagePointResult> PointResults { get; }
            public IReadOnlyList<double> StageCurrents { get; }
            public double? StagePeakCurrent { get; }
            public double? StageAverageCurrent { get; }
        }

        private sealed class MassageTestExecutor
        {
            private readonly MassageTestSettings _settings;
            private readonly TimeSpan _sampleInterval;
            private readonly Func<CancellationToken, Task<(double Height, double Current)>> _sampleAsync;
            private readonly Func<string, CancellationToken, Task<bool>> _readSwitchAsync;
            private readonly Action<double>? _onCurrentSample;
            private readonly Action<string>? _onProgress;
            private readonly Action<bool[]>? _onPointSample;
            private readonly Func<DateTime> _utcNowProvider;
            private readonly Func<TimeSpan, CancellationToken, Task> _delayProvider;
            private readonly double? _currentOverLimit;

            public MassageTestExecutor(
                MassageTestSettings settings,
                TimeSpan sampleInterval,
                Func<CancellationToken, Task<(double Height, double Current)>> sampleAsync,
                Func<string, CancellationToken, Task<bool>> readSwitchAsync,
                double? currentOverLimit = null,
                Action<double>? onCurrentSample = null,
                Action<string>? onProgress = null,
                Action<bool[]>? onPointSample = null,
                Func<DateTime>? utcNowProvider = null,
                Func<TimeSpan, CancellationToken, Task>? delayProvider = null)
            {
                _settings = settings ?? throw new ArgumentNullException(nameof(settings));
                if (sampleInterval < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(sampleInterval));
                }

                _sampleInterval = sampleInterval;
                _sampleAsync = sampleAsync ?? throw new ArgumentNullException(nameof(sampleAsync));
                _readSwitchAsync = readSwitchAsync ?? throw new ArgumentNullException(nameof(readSwitchAsync));
                _currentOverLimit = currentOverLimit;
                _onCurrentSample = onCurrentSample;
                _onProgress = onProgress;
                _onPointSample = onPointSample;
                _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
                _delayProvider = delayProvider ?? ((delay, token) => Task.Delay(delay, token));
            }

            public async Task<MassageTestExecutionResult> ExecuteAsync(
                IReadOnlyList<MassageConfig> massageConfigs,
                CancellationToken cancellationToken)
            {
                if (massageConfigs == null)
                {
                    throw new ArgumentNullException(nameof(massageConfigs));
                }

                var enabledConfigs = massageConfigs
                    .Where(c => c != null && c.Enabled && !string.IsNullOrWhiteSpace(c.HeightSwitchAddress))
                    .DistinctBy(c => c.Point)
                    .OrderBy(c => c.Point)
                    .ToList();

                if (!enabledConfigs.Any())
                {
                    return BuildSuccess("鏈厤缃彲鐢ㄧ殑鎸夋懇楂樺害寮€鍏?, Array.Empty<double>(), Array.Empty<MassagePointTracker>());
                }

                if (_settings.TotalDuration <= 0)
                {
                    return BuildFailure("鎸夋懇鍔ㄤ綔鎬婚暱鏈厤缃垨涓洪浂", Array.Empty<double>(), Array.Empty<MassagePointTracker>());
                }

                var trackers = enabledConfigs.ToDictionary(c => c.Point, c => new MassagePointTracker(c));

                var stageCurrents = new List<double>();
                var startTime = _utcNowProvider();
                bool hasAnyHigh = false;

                foreach (var tracker in trackers.Values)
                {
                    bool initialState = await _readSwitchAsync(tracker.Config.HeightSwitchAddress, cancellationToken)
                        .ConfigureAwait(false);
                    if (initialState)
                    {
                        tracker.IsHigh = true;
                        tracker.HighStart = startTime;
                        tracker.TriggerCount++;
                        tracker.HeightSwitchTriggered = true;
                        tracker.RunningCurrentSum = 0;
                        tracker.RunningSampleCount = 0;
                        tracker.RunningPeak = 0;
                        hasAnyHigh = true;
                    }
                }
                EmitPointSample(trackers.Values);
                var noHighTimeout = TimeSpan.FromSeconds(10);
                var endTime = startTime + TimeSpan.FromMilliseconds(_settings.TotalDuration);
                var now = startTime;

                while (now < endTime)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (_, current) = await _sampleAsync(cancellationToken).ConfigureAwait(false);
                    _onCurrentSample?.Invoke(current);
                    stageCurrents.Add(current);

                    now = _utcNowProvider();

                    foreach (var tracker in trackers.Values)
                    {
                        bool isHigh = await _readSwitchAsync(tracker.Config.HeightSwitchAddress, cancellationToken)
                            .ConfigureAwait(false);

                        if (isHigh)
                        {
                            hasAnyHigh = true;
                            if (!tracker.IsHigh)
                            {
                                tracker.IsHigh = true;
                                tracker.HighStart = now;
                                tracker.TriggerCount++;
                                tracker.HeightSwitchTriggered = true;
                                tracker.RunningCurrentSum = current;
                                tracker.RunningSampleCount = 1;
                                tracker.RunningPeak = current;
                            }
                            else
                            {
                                tracker.RunningCurrentSum += current;
                                tracker.RunningSampleCount++;
                                tracker.RunningPeak = Math.Max(tracker.RunningPeak, current);
                            }
                        }
                        else if (tracker.IsHigh)
                        {
                            string failure = FinalizeHighEvent(tracker, now, false);
                            if (!string.IsNullOrWhiteSpace(failure))
                            {
                                return BuildFailure(failure, stageCurrents, trackers.Values, tracker);
                            }
                        }
                    }

                    EmitPointSample(trackers.Values);
                    _onProgress?.Invoke(BuildProgressMessage(current, trackers.Values));

                    int activeCount = trackers.Values.Count(t => t.IsHigh);
                    if (activeCount > _settings.MaxConcurrentPoints)
                    {
                        string concurrentMessage = $"鎸夋懇鐐瑰苟鍙戞暟{activeCount}瓒呰繃涓婇檺{_settings.MaxConcurrentPoints}";
                        return BuildFailure(concurrentMessage, stageCurrents, trackers.Values);
                    }

                    if (!hasAnyHigh && now - startTime >= noHighTimeout)
                    {
                        return BuildFailure("鎸夋懇娴嬭瘯寮€濮?0绉掑唴鏈娴嬪埌楂樼數骞?, stageCurrents, trackers.Values);
                    }

                    await _delayProvider(_sampleInterval, cancellationToken).ConfigureAwait(false);
                    now = _utcNowProvider();
                }

                var finalTime = _utcNowProvider();
                foreach (var tracker in trackers.Values.Where(t => t.IsHigh))
                {
                    string failure = FinalizeHighEvent(tracker, finalTime, true);
                    if (!string.IsNullOrWhiteSpace(failure))
                    {
                        return BuildFailure(failure, stageCurrents, trackers.Values, tracker);
                    }
                }

                foreach (var tracker in trackers.Values.Where(t => !t.HeightSwitchTriggered))
                {
                    bool isHigh = await _readSwitchAsync(tracker.Config.HeightSwitchAddress, cancellationToken)
                        .ConfigureAwait(false);
                    if (isHigh)
                    {
                        tracker.HeightSwitchTriggered = true;
                        tracker.TriggerCount = Math.Max(1, tracker.TriggerCount + 1);
                    }
                }

                var untriggeredPoints = trackers.Values
                    .Where(tracker => !tracker.HeightSwitchTriggered)
                    .Select(tracker => tracker.Config.Point)
                    .OrderBy(point => point)
                    .ToList();

                if (untriggeredPoints.Any())
                {
                    string pointsText = string.Join("/", untriggeredPoints.Take(8));
                    string message = $"鎸夋懇鐐箋pointsText}鍦ㄨ瀹氭椂闂村唴鏈Е鍙?;
                    return BuildFailure(message, stageCurrents, trackers.Values);
                }

                return BuildSuccess("鎸夋懇娴嬭瘯閫氳繃", stageCurrents, trackers.Values);
            }

            private void EmitPointSample(IEnumerable<MassagePointTracker> trackers)
            {
                if (_onPointSample == null)
                {
                    return;
                }

                var states = new bool[32];
                foreach (var tracker in trackers)
                {
                    int index = tracker.Config.Point - 1;
                    if (index >= 0 && index < states.Length)
                    {
                        states[index] = tracker.IsHigh;
                    }
                }

                _onPointSample(states);
            }

            private string BuildProgressMessage(double current, IEnumerable<MassagePointTracker> trackers)
            {
                var activePoints = trackers
                    .Where(t => t.IsHigh)
                    .Select(t => t.Config.Point)
                    .OrderBy(p => p)
                    .ToList();

                string activeText = activePoints.Any()
                    ? string.Join("/", activePoints)
                    : "鏃?;

                return $"鐢垫祦 {current:F1} mA锛岄珮鐢靛钩鐐? {activeText}";
            }

            private MassageTestExecutionResult BuildSuccess(string message,
                IReadOnlyList<double> stageCurrents,
                IEnumerable<MassagePointTracker> trackers)
            {
                var pointResults = trackers
                    .Select(t => t.ToResult(true))
                    .OrderBy(r => r.Point)
                    .ToList();

                double? peak = stageCurrents.Count > 0 ? stageCurrents.Max() : null;
                double? avg = stageCurrents.Count > 0 ? stageCurrents.Average() : null;

                return new MassageTestExecutionResult(true, message, pointResults, stageCurrents, peak, avg);
            }

            private MassageTestExecutionResult BuildFailure(string message,
                IReadOnlyList<double> stageCurrents,
                IEnumerable<MassagePointTracker> trackers,
                MassagePointTracker? failedTracker = null)
            {
                var pointResults = trackers
                    .Select(t =>
                    {
                        bool isFailed = failedTracker != null && ReferenceEquals(t, failedTracker);
                        return t.ToResult(!isFailed, isFailed ? message : null);
                    })
                    .OrderBy(r => r.Point)
                    .ToList();

                foreach (var result in pointResults.Where(r => r.TriggerCount == 0 && string.IsNullOrWhiteSpace(r.Message)))
                {
                    result.Passed = false;
                    result.Message = "鏈Е鍙?;
                }

                double? peak = stageCurrents.Count > 0 ? stageCurrents.Max() : null;
                double? avg = stageCurrents.Count > 0 ? stageCurrents.Average() : null;

                return new MassageTestExecutionResult(false, message, pointResults, stageCurrents, peak, avg);
            }

            private string FinalizeHighEvent(MassagePointTracker tracker, DateTime now, bool treatAsTimeout)
            {
                tracker.IsHigh = false;
                if (!tracker.HighStart.HasValue)
                {
                    return string.Empty;
                }

                double duration = Math.Max(0, (now - tracker.HighStart.Value).TotalMilliseconds);
                tracker.LastHighDuration = duration;
                tracker.Durations.Add(duration);

                double averageCurrent = tracker.RunningSampleCount > 0
                    ? tracker.RunningCurrentSum / tracker.RunningSampleCount
                    : tracker.RunningPeak;
                tracker.LastAverageCurrent = averageCurrent;
                tracker.LastPeakCurrent = tracker.RunningPeak;
                tracker.AverageSamples.Add((averageCurrent, duration));
                tracker.Peaks.Add(tracker.RunningPeak);

                tracker.HighStart = null;
                tracker.RunningCurrentSum = 0;
                tracker.RunningSampleCount = 0;
                tracker.RunningPeak = 0;

                if (duration < _settings.HighLevelDurationMin)
                {
                    return $"鎸夋懇鐐箋tracker.Config.Point}楂樼數骞虫寔缁瓄duration:F0}ms浣庝簬涓嬮檺{_settings.HighLevelDurationMin}ms";
                }

                if (duration > _settings.HighLevelDurationMax)
                {
                    var timeoutMessage = $"鎸夋懇鐐箋tracker.Config.Point}楂樼數骞虫寔缁瓄duration:F0}ms瓒呰繃涓婇檺{_settings.HighLevelDurationMax}ms";
                    if (treatAsTimeout)
                    {
                        return string.Empty;
                    }

                    return timeoutMessage;
                }

                if (tracker.LastPeakCurrent < _settings.PeakCurrentMin || tracker.LastPeakCurrent > _settings.PeakCurrentMax)
                {
                    return $"鎸夋懇鐐箋tracker.Config.Point}宄板€肩數娴亄tracker.LastPeakCurrent:F1}mA瓒呭嚭鑼冨洿[{_settings.PeakCurrentMin},{_settings.PeakCurrentMax}]mA";
                }

                if (_currentOverLimit.HasValue && tracker.LastPeakCurrent > _currentOverLimit.Value)
                {
                    return $"鎸夋懇鐐箋tracker.Config.Point}宄板€肩數娴亄tracker.LastPeakCurrent:F1}mA瓒呰繃閫氶亾涓婇檺{_currentOverLimit.Value:F1}mA";
                }

                if (averageCurrent < _settings.AverageCurrentMin || averageCurrent > _settings.AverageCurrentMax)
                {
                    return $"鎸夋懇鐐箋tracker.Config.Point}骞冲潎鐢垫祦{averageCurrent:F1}mA瓒呭嚭鑼冨洿[{_settings.AverageCurrentMin},{_settings.AverageCurrentMax}]mA";
                }

                return string.Empty;
            }

            private sealed class MassagePointTracker
            {
                public MassagePointTracker(MassageConfig config)
                {
                    Config = config ?? throw new ArgumentNullException(nameof(config));
                }

                public MassageConfig Config { get; }
                public bool IsHigh { get; set; }
                public DateTime? HighStart { get; set; }
                public bool HeightSwitchTriggered { get; set; }
                public int TriggerCount { get; set; }
                public double RunningCurrentSum { get; set; }
                public int RunningSampleCount { get; set; }
                public double RunningPeak { get; set; }
                public double LastHighDuration { get; set; }
                public double LastPeakCurrent { get; set; }
                public double LastAverageCurrent { get; set; }
                public List<double> Durations { get; } = new();
                public List<double> Peaks { get; } = new();
                public List<(double average, double duration)> AverageSamples { get; } = new();

                public MassagePointResult ToResult(bool? passedOverride = null, string? messageOverride = null)
                {
                    double totalDuration = Durations.Sum();
                    double? weightedAverage = null;
                    if (AverageSamples.Count > 0)
                    {
                        double total = AverageSamples.Sum(a => a.duration);
                        weightedAverage = total > 0
                            ? AverageSamples.Sum(a => a.average * a.duration) / total
                            : AverageSamples.Average(a => a.average);
                    }

                    var result = new MassagePointResult
                    {
                        Point = Config.Point,
                        HeightSwitchTriggered = HeightSwitchTriggered,
                        Duration = Durations.Count > 0 ? (int?)Math.Round(Durations.Average()) : null,
                        PeakCurrent = Peaks.Count > 0 ? (double?)Peaks.Max() : null,
                        AverageCurrent = weightedAverage,
                        TriggerCount = TriggerCount,
                        TotalHighDuration = Durations.Count > 0 ? totalDuration : null,
                        MaxHighDuration = Durations.Count > 0 ? Durations.Max() : null,
                        MinHighDuration = Durations.Count > 0 ? Durations.Min() : null,
                        LastHighDuration = Durations.Count > 0 ? LastHighDuration : null,
                        Passed = passedOverride ?? HeightSwitchTriggered,
                        Message = messageOverride ?? (HeightSwitchTriggered ? $"瑙﹀彂{TriggerCount}娆? : "鏈Е鍙?)
                    };

                    return result;
                }
            }
        }

        private class ChannelTestContext
        {
            public int Channel { get; init; }
            public required ProductModel Model { get; init; }
            public required ChannelConfig ChannelConfig { get; init; }
            public required CurrentSleepConfig CurrentSleepConfig { get; init; }
            public required TestStartOptions Options { get; init; }
            public required TestRecord Record { get; init; }
            public required CancellationTokenSource Cancellation { get; init; }
            public bool ModeSwitchPowerOffActive { get; set; }
            public bool ModeSwitchDriverActive { get; set; }
            public bool ClampCylinderActive { get; set; }
        }

        private class StageExecutionResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public double? PeakCurrent { get; init; }
            public double? AverageCurrent { get; init; }
            public double? Height { get; init; }
            public double? SleepCurrent { get; init; }
            public bool HeightSwitchTriggered { get; init; }
            public string? HeightSwitchAddress { get; init; }
            public TimeSpan? HeightSwitchDelay { get; init; }
            public TimeSpan? CurrentDropDuration { get; init; }
            public double? PressureStart { get; init; }
            public double? PressureEnd { get; init; }
            public double? PressureDrop { get; init; }
            public string? PressureUnit { get; init; }

            public static StageExecutionResult Pass(
                string message,
                double? peak = null,
                double? avg = null,
                double? height = null,
                double? sleepCurrent = null,
                bool heightSwitchTriggered = false,
                string? heightSwitchAddress = null,
                TimeSpan? heightSwitchDelay = null,
                TimeSpan? currentDropDuration = null,
                double? pressureStart = null,
                double? pressureEnd = null,
                double? pressureDrop = null,
                string? pressureUnit = null)
            {
                return new StageExecutionResult
                {
                    Success = true,
                    Message = message,
                    PeakCurrent = peak,
                    AverageCurrent = avg,
                    Height = height,
                    SleepCurrent = sleepCurrent,
                    HeightSwitchTriggered = heightSwitchTriggered,
                    HeightSwitchAddress = heightSwitchAddress,
                    HeightSwitchDelay = heightSwitchDelay,
                    CurrentDropDuration = currentDropDuration,
                    PressureStart = pressureStart,
                    PressureEnd = pressureEnd,
                    PressureDrop = pressureDrop,
                    PressureUnit = pressureUnit
                };
            }

            public static StageExecutionResult Fail(
                string message,
                double? peak = null,
                double? avg = null,
                double? height = null,
                double? sleepCurrent = null,
                bool heightSwitchTriggered = false,
                string? heightSwitchAddress = null,
                TimeSpan? heightSwitchDelay = null,
                TimeSpan? currentDropDuration = null,
                double? pressureStart = null,
                double? pressureEnd = null,
                double? pressureDrop = null,
                string? pressureUnit = null)
            {
                return new StageExecutionResult
                {
                    Success = false,
                    Message = message,
                    PeakCurrent = peak,
                    AverageCurrent = avg,
                    Height = height,
                    SleepCurrent = sleepCurrent,
                    HeightSwitchTriggered = heightSwitchTriggered,
                    HeightSwitchAddress = heightSwitchAddress,
                    HeightSwitchDelay = heightSwitchDelay,
                    CurrentDropDuration = currentDropDuration,
                    PressureStart = pressureStart,
                    PressureEnd = pressureEnd,
                    PressureDrop = pressureDrop,
                    PressureUnit = pressureUnit
                };
            }
        }
    }
}


