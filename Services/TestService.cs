// Services/TestService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Services;

namespace AudioActuatorCanTest.Services
{
    public class TestService : IDisposable
    {
        private const string AutoTestFlagAddress = "M79";

        private readonly IPLCService _plcService;
        private readonly CommService _commService;
        private readonly ILogService _logService;
        private readonly Dictionary<int, ChannelTestContext> _activeChannels = new();
        private readonly Dictionary<string, int> _barcodeHistory = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _syncRoot = new();
        private readonly Random _random = new();
        private bool _disposed;

        private const int ManualMessageRegisterCount = 20;
        private static readonly byte[] ManualMessageClearBuffer = new byte[ManualMessageRegisterCount * 2];
        private const int StatusMessageRegisterCount = 20;
        private static readonly byte[] StatusMessageClearBuffer = new byte[StatusMessageRegisterCount * 2];

        public event EventHandler<TestStageChangedEventArgs>? OnTestStageChanged;
        public event EventHandler<TestRecord>? OnTestCompleted;
        public event EventHandler<TestMessageEventArgs>? OnTestMessage;
        public event EventHandler<ChannelTestResultEventArgs>? OnTestResultDisplay;

        public TestService(IPLCService plcService, CommService commService)
            : this(plcService, commService, LogService.Instance)
        {
        }

        public TestService(IPLCService plcService, CommService commService, ILogService? logService)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _commService = commService ?? throw new ArgumentNullException(nameof(commService));
            _logService = logService ?? LogService.Instance;
        }

        private void RaiseTestMessage(string message, int? channel = null)
        {
            OnTestMessage?.Invoke(this, new TestMessageEventArgs(message ?? string.Empty, channel));
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
                RaiseTestMessage("未选择产品型号");
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.WorkOrder))
            {
                RaiseTestMessage("错误：工单号不能为空");
                return false;
            }

            options.Barcode = CodeScanService.SanitizeBarcode(options.Barcode);

            if (options.Model.ProcessConfig.EnableBarcodeCheck && string.IsNullOrWhiteSpace(options.Barcode))
            {
                RaiseTestMessage("错误：需扫描产品二维码");
                return false;
            }

            ChannelConfig channelConfig = options.Channel == 1 ? options.Model.Channel1Config : options.Model.Channel2Config;
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
                TestVoltage = 13.5,
                Result = TestResult.Testing
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

            bool shouldSetAutoTestFlag;
            lock (_syncRoot)
            {
                _activeChannels[options.Channel] = context;
                shouldSetAutoTestFlag = _activeChannels.Count == 1;
            }

            if (shouldSetAutoTestFlag)
            {
                await UpdateAutoTestFlagAsync(true).ConfigureAwait(false);
            }

            RaiseTestMessage($"通道{options.Channel}开始测试，条码:{options.Barcode}", options.Channel);

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

                if (shouldResetAutoTestFlag)
                {
                    await UpdateAutoTestFlagAsync(false).ConfigureAwait(false);
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
                    // 已经释放
                }
            }

            lock (_syncRoot)
            {
                _activeChannels.Clear();
            }

            try
            {
                UpdateAutoTestFlagAsync(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logService.LogError("停止所有测试时复位自动测试标记失败", ex);
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
                await InitializeStatusMessageRegistersAsync(context).ConfigureAwait(false);

                if (!await ExecuteStageAsync(context, TestStage.Standby, EnsureStandbyAsync))
                    return await FailAsync(context, "待机状态检查失败");

                if (!await ExecuteStageAsync(context, TestStage.ScanBarcode, ConfirmBarcodeAsync))
                    return await FailAsync(context, "扫码验证失败");

                if (!await ExecuteStageAsync(context, TestStage.StartTest, StartEquipmentAsync))
                    return await FailAsync(context, "启动测试失败");

                var processConfig = context.Model.ProcessConfig ?? new TestProcessConfig();

                if (processConfig.MeasureSleepCurrent)
                {
                    if (!await ExecuteStageAsync(context, TestStage.SleepTest, PerformSleepTestAsync))
                        return await FailAsync(context, "休眠测试失败");
                }
                else
                {
                    SkipStage(context, TestStage.SleepTest, "配置未启用休眠电流检测");
                }

                if (processConfig.MeasureStaticCurrent)
                {
                    if (!await ExecuteStageAsync(context, TestStage.StaticCurrentTest, PerformStaticCurrentTestAsync))
                        return await FailAsync(context, "静态电流超出范围");
                }
                else
                {
                    SkipStage(context, TestStage.StaticCurrentTest, "配置未启用静态电流检测");
                }

                if (!await ExecuteStageAsync(context, TestStage.StatusMessageCheck, PerformStatusMessageCheckAsync))
                    return await FailAsync(context, "状态报文校验失败");

                bool lumbarTestEnabled = processConfig.EnableLumbarTest;
                bool massageTestEnabled = processConfig.EnableMassageTest;

                if (lumbarTestEnabled)
                {
                    if (!await ExecuteStageAsync(context, TestStage.LumbarTest, PerformLumbarTestAsync))
                        return await FailAsync(context, "腰托动作检测不合格");

                    if (massageTestEnabled)
                    {
                        await DelayBeforeMassageTestAsync(context).ConfigureAwait(false);
                    }
                }
                else
                {
                    SkipStage(context, TestStage.LumbarTest, "配置未启用腰托测试");
                }

                if (massageTestEnabled)
                {
                    if (!await ExecuteStageAsync(context, TestStage.MassageTest, PerformMassageTestAsync))
                        return await FailAsync(context, "按摩功能检测失败");
                }
                else
                {
                    SkipStage(context, TestStage.MassageTest, "配置未启用按摩测试");
                }

                if (processConfig.EnableMassageTest && processConfig.CheckSameModel)
                {
                    if (!await ExecuteStageAsync(context, TestStage.MasterSlaveDecision, PerformMasterSlaveDecisionAsync))
                        return await FailAsync(context, "模式切换失败");

                    if (!await ExecuteStageAsync(context, TestStage.MasterModeMassage, PerformMasterModeMassageAsync))
                        return await FailAsync(context, "按摩2测试失败");
                }
                else if (!processConfig.EnableMassageTest)
                {
                    SkipStage(context, TestStage.MasterSlaveDecision, "配置未启用按摩测试");
                    SkipStage(context, TestStage.MasterModeMassage, "配置未启用按摩测试");
                }
                else
                {
                    SkipStage(context, TestStage.MasterSlaveDecision, "未启用模式切换");
                    SkipStage(context, TestStage.MasterModeMassage, "未启用按摩2测试");
                }

                await ExecuteStageAsync(context, TestStage.Completed, CompleteStageAsync);
                await FinalizeTest(context, true, "测试完成");
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

        private Task UpdateAutoTestFlagAsync(bool isTesting)
        {
            return _plcService.WriteBitAsync(AutoTestFlagAddress, isTesting);
        }

        private async Task<bool> FailAsync(ChannelTestContext context, string reason)
        {
            await FinalizeTest(context, false, reason);
            return false;
        }

        private async Task FinalizeTest(ChannelTestContext context, bool success, string message, bool aborted = false)
        {
            var record = context.Record;
            record.Result = success ? TestResult.Pass : aborted ? TestResult.Aborted : TestResult.Fail;
            record.FailReason = success ? string.Empty : message;
            record.TestDuration = (DateTime.Now - record.TestTime).TotalSeconds;
            record.WasAborted = aborted;

            await SendStopMessageWithDelayAsync(context).ConfigureAwait(false);
            await ResetModeSwitchAsync(context).ConfigureAwait(false);

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

            await ClearChannelStatusMessageSendRegistersAsync(context.Channel).ConfigureAwait(false);

            OnTestResultDisplay?.Invoke(this, new ChannelTestResultEventArgs
            {
                Channel = context.Channel,
                IsOk = success
            });

            OnTestCompleted?.Invoke(this, record);
            RaiseTestMessage($"通道{context.Channel}测试结束: {message}", context.Channel);
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
                RaiseTestMessage($"通道{context.Channel}停止报文发送失败: {ex.Message}", context.Channel);
            }
        }

        private async Task InitializeStatusMessageRegistersAsync(ChannelTestContext context)
        {
            var (sendStart, receiveStart, _) = GetStatusMessageAddresses(context.Channel);
            await ClearStatusMessageRegistersAsync(sendStart).ConfigureAwait(false);
            await ClearStatusMessageRegistersAsync(receiveStart).ConfigureAwait(false);
        }

        private Task ClearStatusMessageRegistersAsync(int startAddress)
        {
            string address = $"D{startAddress}";
            return _plcService.WriteWordsAsync(address, StatusMessageClearBuffer);
        }

        private async Task ClearChannelStatusMessageSendRegistersAsync(int channel)
        {
            try
            {
                var (sendStart, _, _) = GetStatusMessageAddresses(channel);
                await ClearStatusMessageRegistersAsync(sendStart).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseTestMessage($"通道{channel} 清空状态报文寄存器失败: {ex.Message}", channel);
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
                result.PressureSwitchTriggered = execution.PressureSwitchTriggered;
                result.PressureSwitchAddress = execution.PressureSwitchAddress;
                result.PressureSwitchDelaySeconds = execution.PressureSwitchDelay?.TotalSeconds;
                result.CurrentDropDurationSeconds = execution.CurrentDropDuration?.TotalSeconds;
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
                result.Message = "测试取消";
                RaiseStageChanged(context, stage, StepExecutionState.Failed, "测试取消");
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
            return Task.FromResult(StageExecutionResult.Pass("待机状态正常"));
        }

        private Task<StageExecutionResult> ConfirmBarcodeAsync(ChannelTestContext context, CancellationToken token)
        {
            if (!context.Model.ProcessConfig.EnableBarcodeCheck)
            {
                return Task.FromResult(StageExecutionResult.Pass("已跳过扫码检查"));
            }

            if (context.Model.ProcessConfig.EnableBarcodeCheck && string.IsNullOrWhiteSpace(context.Options.Barcode))
            {
                return Task.FromResult(StageExecutionResult.Fail("未扫描条码"));
            }

            if (context.Model.ProcessConfig.EnableBarcodeCheck && context.Options.Barcode!.Length < 10)
            {
                return Task.FromResult(StageExecutionResult.Fail("扫码数据长度不足10个字符"));
            }

            if (context.Record.DuplicateCount > 1 && context.Model.ProcessConfig.PromptOnDuplicateBarcode && !context.Options.ContinueOnDuplicate)
            {
                return Task.FromResult(StageExecutionResult.Fail($"重复条码，第{context.Record.DuplicateCount}次测试已取消"));
            }

            string message = context.Record.DuplicateCount > 1
                ? $"重复测试第{context.Record.DuplicateCount}次"
                : "扫码成功";
            return Task.FromResult(StageExecutionResult.Pass(message));
        }

        private async Task<StageExecutionResult> StartEquipmentAsync(ChannelTestContext context, CancellationToken token)
        {
            await Task.Delay(200, token);
            return StageExecutionResult.Pass("测试设备已启动");
        }

        private async Task<StageExecutionResult> PerformSleepTestAsync(ChannelTestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            bool sent = await _commService.SendSleepMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
            if (!sent)
            {
                return StageExecutionResult.Fail("休眠指令发送失败");
            }

            await Task.Delay(500, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            await _commService.ClearChannelSendBufferAsync(context.Channel).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            ChannelAddressMap addressMap = GetChannelAddressMap(context.Channel);
            string currentAddress = $"D{addressMap.CurrentValueD}";

            int timeout = Math.Max(100, context.ChannelConfig.SleepTestTimeout);
            var endTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout);
            const int sampleInterval = 100;
            double threshold = context.ChannelConfig.SleepCurrentThreshold;
            double? lastValue = null;

            while (DateTime.UtcNow <= endTime)
            {
                token.ThrowIfCancellationRequested();

                short[] data = await _plcService.ReadWordsAsync(currentAddress, 2).ConfigureAwait(false);
                int raw = ParseInt32FromWords(data);
                double currentMilliAmps = raw / 100.0;

                RecordCurrentSample(context, "休眠测试", currentMilliAmps);

                lastValue = currentMilliAmps;

                if (currentMilliAmps <= threshold)
                {
                    string passMessage = $"休眠电流 {currentMilliAmps:F2}mA 低于阈值 {threshold:F2}mA";
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
                return StageExecutionResult.Fail("未读取到休眠电流数据");
            }

            string failMessage = $"休眠电流 {lastValue.Value:F2}mA 超过阈值 {threshold:F2}mA";
            return StageExecutionResult.Fail(failMessage, sleepCurrent: lastValue);
        }

        private async Task<StageExecutionResult> PerformStaticCurrentTestAsync(ChannelTestContext context, CancellationToken token)
        {
            bool messageSent = await _commService.SendReadMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
            if (!messageSent)
            {
                return StageExecutionResult.Fail("静态电流读取报文发送失败");
            }

            var samples = new List<double>();
            ChannelAddressMap addressMap = GetChannelAddressMap(context.Channel);
            string currentAddress = $"D{addressMap.CurrentValueD}";

            var endTime = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            const int sampleInterval = 100;

            while (DateTime.UtcNow <= endTime)
            {
                token.ThrowIfCancellationRequested();

                short[] data = await _plcService.ReadWordsAsync(currentAddress, 2).ConfigureAwait(false);
                int raw = ParseInt32FromWords(data);
                double currentMilliAmps = raw / 100.0;

                samples.Add(currentMilliAmps);
                RecordCurrentSample(context, "静态电流", currentMilliAmps);

                if (DateTime.UtcNow >= endTime)
                {
                    break;
                }

                await Task.Delay(sampleInterval, token).ConfigureAwait(false);
            }

            if (samples.Count == 0)
            {
                return StageExecutionResult.Fail("未读取到静态电流数据");
            }

            double average = samples.Average();
            bool overLimit = average > context.ChannelConfig.CurrentOverLimit;
            if (overLimit)
            {
                string overLimitMessage = $"静态电流 平均 {average:F2}mA 超过上限 {context.ChannelConfig.CurrentOverLimit:F2}mA";
                return StageExecutionResult.Fail(overLimitMessage, avg: average);
            }

            bool inRange = average >= context.ChannelConfig.StaticCurrentMin && average <= context.ChannelConfig.StaticCurrentMax;
            string message = $"静态电流 平均 {average:F2}mA";

            return inRange
                ? StageExecutionResult.Pass(message, avg: average)
                : StageExecutionResult.Fail($"{message} 超出范围 {context.ChannelConfig.StaticCurrentMin:F2}-{context.ChannelConfig.StaticCurrentMax:F2}mA", avg: average);
        }

        private async Task<StageExecutionResult> PerformStatusMessageCheckAsync(ChannelTestContext context, CancellationToken token)
        {
            var messageConfig = context.ChannelConfig?.MessageConfig;
            if (messageConfig?.ReadMessage == null || messageConfig.ReadMessage.Length == 0)
            {
                return StageExecutionResult.Fail("未配置状态读取报文");
            }

            var (sendStart, receiveStart, displayAddress) = GetStatusMessageAddresses(context.Channel);
            string displayLabel = GetStatusDisplayLabel(context.Channel, displayAddress);

            bool messageSent = await _commService.SendReadMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
            if (!messageSent)
            {
                return StageExecutionResult.Fail("状态报文读取命令发送失败");
            }

            RaiseTestMessage($"通道{context.Channel} 已触发状态读取报文发送，写入区D{sendStart}", context.Channel);

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            ushort? lastReportedValue = null;
            int displayIndex = displayAddress - receiveStart;

            while (DateTime.UtcNow <= deadline)
            {
                token.ThrowIfCancellationRequested();

                short[] response = await _plcService.ReadWordsAsync($"D{receiveStart}", StatusMessageRegisterCount).ConfigureAwait(false);

                if (response != null && displayIndex >= 0 && displayIndex < response.Length)
                {
                    ushort value = unchecked((ushort)response[displayIndex]);

                    if (!lastReportedValue.HasValue || lastReportedValue.Value != value)
                    {
                        RaiseTestMessage($"通道{context.Channel} 状态报文 {displayLabel}=0x{value:X4}", context.Channel);
                        lastReportedValue = value;
                    }

                    if (value != 0)
                    {
                        return StageExecutionResult.Pass($"状态报文接收成功 {displayLabel}=0x{value:X4}");
                    }
                }

                await Task.Delay(100, token).ConfigureAwait(false);
            }

            string lastValueText = lastReportedValue.HasValue ? $"0x{lastReportedValue.Value:X4}" : "0x0000";
            RaiseTestMessage($"通道{context.Channel} 状态报文检测失败 {displayLabel}保持{lastValueText}", context.Channel);
            return StageExecutionResult.Fail($"状态报文检测超时 {displayLabel}保持{lastValueText}");
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
                return StageExecutionResult.Pass("未配置腰托动作");
            }

            var addressMap = GetChannelAddressMap(context.Channel);
            string sendAddress = GetChannelSendAddress(context.Channel);
            var stageCurrents = new List<double>();
            double? lastMeasuredHeight = null;

            for (int actionIndex = 0; actionIndex < enabledConfigs.Count; actionIndex++)
            {
                var config = enabledConfigs[actionIndex];
                int actionOrder = config.Order;
                string actionLabel = config.Action.FormatWithOrder(actionOrder);
                bool isLastAction = actionIndex == enabledConfigs.Count - 1;
                token.ThrowIfCancellationRequested();

                // 详细的动作信息输出
                RaiseTestMessage(
                    $"开始{actionLabel}，目标高度{config.TargetHeight}mm，超时{config.TargetTime}ms，M地址:{config.MRegisterAddress}",
                    context.Channel);

                if (config.SendMessage == null || config.SendMessage.Length == 0)
                {
                    return StageExecutionResult.Fail($"{actionLabel}报文未配置");
                }

                bool isFrameHeaderSwitch = config.Action == LumbarActionType.FrameHeaderSwitch;
                var channelPayload = CommService.PrepareChannelMessagePayload(context.Channel, config.SendMessage);
                var messageBytes = PrepareMessageBytes(channelPayload, ManualMessageRegisterCount);

                if (isFrameHeaderSwitch)
                {
                    // 帧头切换处理逻辑保持不变
                    var frameSwitchCurrents = new List<double>();
                    DateTime frameSwitchStart = DateTime.UtcNow;
                    DateTime frameSwitchDeadline = frameSwitchStart + TimeSpan.FromSeconds(1);
                    const int frameSwitchSampleInterval = 100;
                    bool frameSwitchManualSendActivated = false;

                    try
                    {
                        await _plcService.WriteWordsAsync(sendAddress, messageBytes).ConfigureAwait(false);
                        frameSwitchManualSendActivated = true;

                        while (DateTime.UtcNow <= frameSwitchDeadline)
                        {
                            token.ThrowIfCancellationRequested();

                            var (height, current) = await ReadHeightAndCurrentAsync(addressMap, context.Channel).ConfigureAwait(false);
                            frameSwitchCurrents.Add(current);
                            stageCurrents.Add(current);
                            context.Record.LumbarCurrents.Add(current);
                            RecordCurrentSample(context, actionLabel, current);

                            RaiseStageChanged(context, TestStage.LumbarTest, StepExecutionState.Running,
                                $"{actionLabel} 帧头切换电流: {current:F1} mA");

                            await Task.Delay(frameSwitchSampleInterval, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return StageExecutionResult.Fail($"{actionLabel}执行异常: {ex.Message}");
                    }
                    finally
                    {
                        if (frameSwitchManualSendActivated)
                        {
                            await _plcService.WriteWordsAsync(sendAddress, ManualMessageClearBuffer).ConfigureAwait(false);
                        }
                    }

                    if (frameSwitchCurrents.Count == 0)
                    {
                        return StageExecutionResult.Fail($"{actionLabel}未采集到电流数据");
                    }

                    int frameSwitchActualTime = (int)Math.Round((DateTime.UtcNow - frameSwitchStart).TotalMilliseconds);
                    double frameSwitchPeakCurrent = frameSwitchCurrents.Max();
                    double frameSwitchAverageCurrent = frameSwitchCurrents.Average();
                    bool frameSwitchCurrentOk = frameSwitchPeakCurrent <= context.ChannelConfig.CurrentOverLimit;

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
                        Message = frameSwitchCurrentOk ? "帧头切换完成" : "电流超限"
                    };

                    context.Record.LumbarResults.Add(frameSwitchResult);

                    if (!frameSwitchCurrentOk)
                    {
                        return StageExecutionResult.Fail($"{actionLabel}电流超限",
                            peak: frameSwitchPeakCurrent, avg: frameSwitchAverageCurrent);
                    }

                    if (!isLastAction)
                    {
                        await PauseBetweenLumbarActionsAsync(sendAddress, token).ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(config.MRegisterAddress))
                {
                    return StageExecutionResult.Fail($"{actionLabel}缺少M寄存器地址");
                }

                var (commandAddress, statusAddresses) = ParseLumbarAddresses(config, addressMap, context.Channel);

                if (string.IsNullOrWhiteSpace(commandAddress))
                {
                    return StageExecutionResult.Fail($"{actionLabel}缺少启动M寄存器地址");
                }

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

                try
                {
                    await _plcService.WriteBitAsync(commandAddress, true).ConfigureAwait(false);
                    commandActivated = true;

                    await _plcService.WriteWordsAsync(sendAddress, messageBytes).ConfigureAwait(false);

                    var (initialHeight, initialCurrent) = await ReadHeightAndCurrentAsync(addressMap, context.Channel).ConfigureAwait(false);

                    int sampleCount = 0;
                    while (DateTime.UtcNow <= deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        sampleCount++;

                        var (height, current) = await ReadHeightAndCurrentAsync(addressMap, context.Channel).ConfigureAwait(false);
                        lastHeight = height;
                        lastCurrent = current;
                        heightSamples.Add(height);
                        actionCurrents.Add(current);
                        stageCurrents.Add(current);
                        context.Record.LumbarCurrents.Add(current);
                        RecordCurrentSample(context, actionLabel, current);

                        string progressMessage = $"{actionLabel} 实时高度: {height:F1}mm / 目标: {config.TargetHeight:F1}mm，电流: {current:F1}mA";
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

                        // 修复：严格的高度达标检查
                        if (!heightTargetReached && meetsHeightCondition)
                        {
                            // 验证高度数据合理性
                            if (config.Action == LumbarActionType.SimultaneousDeflate && height > 50)
                            {
                                RaiseTestMessage(
                                    $"警告：同时放气动作高度异常！当前{height:F1}mm > 50mm，但判断为达标，可能数据错误",
                                    context.Channel);
                                // 不设置heightTargetReached，继续检测
                            }
                            else
                            {
                                heightTargetReached = true;
                                heightReachedTime = DateTime.UtcNow;
                                feedbackReached = true;
                                feedbackTime ??= heightReachedTime;

                                string successMessage = config.Action == LumbarActionType.SimultaneousDeflate
                                    ? $"{actionLabel} 成功降至目标高度以下 {height:F1}mm"
                                    : $"{actionLabel} 成功升至目标高度以上 {height:F1}mm";

                                RaiseStageChanged(context, TestStage.LumbarTest, StepExecutionState.Running, successMessage);
                                break;
                            }
                        }

                        // 状态检测逻辑
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
                    return StageExecutionResult.Fail($"{actionLabel}执行异常: {ex.Message}");
                }
                finally
                {
                    if (commandActivated)
                    {
                        await _plcService.WriteBitAsync(commandAddress, false).ConfigureAwait(false);
                    }
                }

                // 修复：严格的高度达标验证
                if (!heightTargetReached)
                {
                    string failReason = $"{actionLabel}未达到目标高度 (最后高度{lastHeight:F1}mm, 目标{config.TargetHeight:F1}mm)";
                    RaiseTestMessage(failReason, context.Channel);
                    return StageExecutionResult.Fail(failReason);
                }

                // 修复：最终高度验证
                double verifiedHeight = lastHeight;
                bool heightVerified = config.Action == LumbarActionType.SimultaneousDeflate
                    ? verifiedHeight <= config.TargetHeight
                    : verifiedHeight >= config.TargetHeight;

                if (!heightVerified)
                {
                    string verificationFail = $"{actionLabel} 最终高度验证失败 (最终{verifiedHeight:F1}mm, 目标{config.TargetHeight:F1}mm)";
                    RaiseTestMessage(verificationFail, context.Channel);
                    return StageExecutionResult.Fail(verificationFail);
                }

                if (actionCurrents.Count == 0)
                {
                    return StageExecutionResult.Fail($"{actionLabel}未采集到电流数据");
                }

                // 计算实际执行时间
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
                bool currentOk = peakCurrent <= context.ChannelConfig.CurrentOverLimit;

                string passMessage = $"高度{evaluationHeight:F1}mm, 用时{actualTime}ms";

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
                    Message = heightOk && timeOk && currentOk ? passMessage : "测试失败"
                };

                context.Record.LumbarResults.Add(actionResult);

                if (!actionResult.Passed)
                {
                    string failDetail = !heightOk ? "高度未达标" : (!timeOk ? "超时" : "电流超限");
                    return StageExecutionResult.Fail($"{actionLabel}{failDetail}",
                        peak: peakCurrent, avg: averageCurrent, height: evaluationHeight);
                }

                if (!isLastAction)
                {
                    await PauseBetweenLumbarActionsAsync(sendAddress, token).ConfigureAwait(false);
                }
            }

            // 阶段统计
            double? stagePeak = stageCurrents.Count > 0 ? stageCurrents.Max() : null;
            double? stageAvg = stageCurrents.Count > 0 ? stageCurrents.Average() : null;
            context.Record.LumbarAverageCurrent = stageAvg;
            context.Record.LumbarMaxCurrent = stagePeak;

            return StageExecutionResult.Pass("腰托测试通过",
                peak: stagePeak, avg: stageAvg, height: lastMeasuredHeight);
        }

        private Task DelayBeforeMassageTestAsync(ChannelTestContext context)
        {
            return Task.Delay(TimeSpan.FromSeconds(3), context.Cancellation.Token);
        }

        private async Task PauseBetweenLumbarActionsAsync(string sendAddress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            await _plcService.WriteWordsAsync(sendAddress, ManualMessageClearBuffer).ConfigureAwait(false);
        }

        private async Task<bool> ActivateManualMassageMessageAsync(ChannelTestContext context)
        {
            var message = context.ChannelConfig?.MessageConfig?.MassageMessage;
            string sendAddress = GetChannelSendAddress(context.Channel);
            if (message == null || message.Length == 0)
            {
                await DeactivateManualMassageMessageAsync(sendAddress).ConfigureAwait(false);
                return false;
            }

            var buffer = ConvertToWordBytes(message, ManualMessageRegisterCount);
            await _plcService.WriteWordsAsync(sendAddress, buffer).ConfigureAwait(false);
            return true;
        }

        private async Task DeactivateManualMassageMessageAsync(string sendAddress)
        {
            await _plcService.WriteWordsAsync(sendAddress, ManualMessageClearBuffer).ConfigureAwait(false);
        }

        private async Task ResetModeSwitchAsync(ChannelTestContext context)
        {
            if (context == null)
            {
                return;
            }

            var addressMap = GetChannelAddressMap(context.Channel);
            string powerOffAddress = $"M{addressMap.PowerOff}";
            string driverSwitchAddress = $"M{addressMap.DriverSwitch}";

            if (context.ModeSwitchPowerOffActive)
            {
                try
                {
                    await _plcService.WriteBitAsync(powerOffAddress, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"通道{context.Channel}断电复位失败: {ex.Message}", context.Channel);
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
                    await _plcService.WriteBitAsync(driverSwitchAddress, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"通道{context.Channel}模式切换复位失败: {ex.Message}", context.Channel);
                }
                finally
                {
                    context.ModeSwitchDriverActive = false;
                }
            }
        }

        private static byte[] ConvertToWordBytes(ushort[] source, int registerCount)
        {
            var buffer = new byte[registerCount * 2];
            if (source == null)
            {
                return buffer;
            }

            int length = Math.Min(source.Length, registerCount);
            for (int i = 0; i < length; i++)
            {
                buffer[i * 2] = (byte)(source[i] & 0xFF);
                buffer[i * 2 + 1] = (byte)((source[i] >> 8) & 0xFF);
            }

            return buffer;
        }

        private async Task<StageExecutionResult> PerformMassageTestAsync(ChannelTestContext context, CancellationToken token)
        {
            bool manualMessageActive = false;
            string sendAddress = GetChannelSendAddress(context.Channel);

            try
            {
                manualMessageActive = await ActivateManualMassageMessageAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return StageExecutionResult.Fail("按摩报文加载失败: " + ex.Message);
            }

            try
            {
                var channelConfig = context.ChannelConfig ?? new ChannelConfig();
                var enabledSteps = (channelConfig.MassageConfigs ?? new List<MassageConfig>())
                    .Where(c => c != null && c.Enabled)
                    .OrderBy(c => c.Point)
                    .ToList();

                if (!enabledSteps.Any())
                {
                    return StageExecutionResult.Pass("未配置按摩动作");
                }

                if (!await _commService.SendMassageMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false))
                {
                    return StageExecutionResult.Fail("按摩指令发送失败");
                }

                var addressMap = GetChannelAddressMap(context.Channel);
                var settings = channelConfig.MassageTestSettings ?? new MassageTestSettings();

                var executor = new MassageTestExecutor(
                    settings,
                    TimeSpan.FromMilliseconds(100),
                    _ => ReadHeightAndCurrentAsync(addressMap, context.Channel),
                    (address, _) => ReadBitAsync(address),
                    channelConfig.CurrentOverLimit,
                    current =>
                    {
                        context.Record.MassageCurrents.Add(current);
                        RecordCurrentSample(context, "按摩测试", current);
                    },
                    progress => RaiseStageChanged(context, TestStage.MassageTest, StepExecutionState.Running, progress));

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
                if (manualMessageActive)
                {
                    await DeactivateManualMassageMessageAsync(sendAddress).ConfigureAwait(false);
                }
            }
        }

        private async Task<StageExecutionResult> PerformMasterSlaveDecisionAsync(ChannelTestContext context, CancellationToken token)
        {
            var process = context.Model?.ProcessConfig;
            if (process == null)
            {
                return StageExecutionResult.Fail("缺少模式切换配置");
            }

            int duration = Math.Max(100, process.ModeSwitchPowerOffDuration);
            var addressMap = GetChannelAddressMap(context.Channel);
            string powerOffAddress = $"M{addressMap.PowerOff}";
            string driverSwitchAddress = $"M{addressMap.DriverSwitch}";

            bool powerActivated = false;
            bool driverActivated = false;
            bool success = false;

            try
            {
                await _plcService.WriteBitAsync(powerOffAddress, true).ConfigureAwait(false);
                powerActivated = true;
                context.ModeSwitchPowerOffActive = true;

                await _plcService.WriteBitAsync(driverSwitchAddress, true).ConfigureAwait(false);
                driverActivated = true;
                context.ModeSwitchDriverActive = true;

                DateTime endTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(duration);
                while (DateTime.UtcNow < endTime)
                {
                    token.ThrowIfCancellationRequested();
                    int remaining = Math.Max(0, (int)Math.Round((endTime - DateTime.UtcNow).TotalMilliseconds));
                    RaiseStageChanged(context, TestStage.MasterSlaveDecision, StepExecutionState.Running,
                        $"模式切换断电中 剩余 {remaining}ms");
                    await Task.Delay(200, token).ConfigureAwait(false);
                }

                await _plcService.WriteBitAsync(powerOffAddress, false).ConfigureAwait(false);
                context.ModeSwitchPowerOffActive = false;

                success = true;
                return StageExecutionResult.Pass($"模式切换完成，断电{duration}ms");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return StageExecutionResult.Fail($"模式切换控制失败: {ex.Message}");
            }
            finally
            {
                if (!success && powerActivated)
                {
                    try
                    {
                        await _plcService.WriteBitAsync(powerOffAddress, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RaiseTestMessage($"通道{context.Channel}断电复位失败: {ex.Message}", context.Channel);
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
                        await _plcService.WriteBitAsync(driverSwitchAddress, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RaiseTestMessage($"通道{context.Channel}模式切换复位失败: {ex.Message}", context.Channel);
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
                return StageExecutionResult.Fail("未配置按摩报文2");
            }

            if (!await _commService.SendMassageMessage2Async(context.Channel, context.ChannelConfig).ConfigureAwait(false))
            {
                return StageExecutionResult.Fail("按摩报文2发送失败");
            }

            var addressMap = GetChannelAddressMap(context.Channel);
            string massageKeyAddress = $"M{addressMap.MassageKey}";
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
                    .Where(c => c != null && c.Enabled && !string.IsNullOrWhiteSpace(c.PressureSwitchAddress))
                    .Select(c => c.PressureSwitchAddress.Trim())
                    .Where(address => !string.IsNullOrWhiteSpace(address))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                if (pressureSwitchAddresses.Count == 0)
                {
                    return StageExecutionResult.Fail("未配置按摩压力开关地址");
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

                    string waitingProgress = $"按摩2测试 等待压力开关触发({pressureSwitchTimeout.TotalSeconds:F0}s超时)";
                    RaiseStageChanged(context, TestStage.MasterModeMassage, StepExecutionState.Running, waitingProgress);
                    await Task.Delay(sampleInterval, token).ConfigureAwait(false);
                }

                if (triggeredSwitch == null || pressureTriggerTime == null)
                {
                    return StageExecutionResult.Fail($"按摩压力开关未在{pressureSwitchTimeout.TotalSeconds:F0}s内触发");
                }

                TimeSpan triggerDelay = pressureTriggerTime.Value - pressureWaitStart;
                string triggerMessage = $"压力开关{triggeredSwitch}在{triggerDelay.TotalMilliseconds:F0}ms内触发";
                RaiseTestMessage($"通道{context.Channel} {triggerMessage}", context.Channel);
                RaiseStageChanged(context, TestStage.MasterModeMassage, StepExecutionState.Running, triggerMessage);

                try
                {
                    await _commService.ClearChannelSendBufferAsync(context.Channel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return StageExecutionResult.Fail(
                        $"清空按摩报文发送区失败: {ex.Message}",
                        pressureSwitchTriggered: true,
                        pressureSwitchAddress: triggeredSwitch,
                        pressureSwitchDelay: triggerDelay);
                }

                await Task.Delay(postTriggerDelay, token).ConfigureAwait(false);

                try
                {
                    await _plcService.WriteBitAsync(massageKeyAddress, true).ConfigureAwait(false);
                    massageKeyActivated = true;
                }
                catch (Exception ex)
                {
                    return StageExecutionResult.Fail(
                        $"按摩按键置位失败: {ex.Message}",
                        pressureSwitchTriggered: true,
                        pressureSwitchAddress: triggeredSwitch,
                        pressureSwitchDelay: triggerDelay);
                }

                await Task.Delay(massageKeyPulseDuration, token).ConfigureAwait(false);

                try
                {
                    await _plcService.WriteBitAsync(massageKeyAddress, false).ConfigureAwait(false);
                    massageKeyActivated = false;
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"通道{context.Channel}按摩按键复位失败: {ex.Message}", context.Channel);
                }

                DateTime monitorStart = DateTime.UtcNow;
                DateTime? belowThresholdStart = null;
                DateTime? dropSatisfiedTime = null;

                while (DateTime.UtcNow - monitorStart < currentMonitorTimeout)
                {
                    token.ThrowIfCancellationRequested();

                    var (_, current) = await ReadHeightAndCurrentAsync(addressMap, context.Channel).ConfigureAwait(false);
                    stageSamples.Add(current);
                    context.Record.MassageCurrents.Add(current);
                    RecordCurrentSample(context, "按摩2测试-电流监控", current);

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

                    string progress = $"按摩2测试 电流: {current:F1} mA (目标 < {currentDropThreshold:F1} mA，保持{requiredStableDuration.TotalSeconds:F0}s)";
                    RaiseStageChanged(context, TestStage.MasterModeMassage, StepExecutionState.Running, progress);

                    await Task.Delay(sampleInterval, token).ConfigureAwait(false);
                }

                double? stagePeak = stageSamples.Count > 0 ? stageSamples.Max() : (double?)null;
                double? stageAvg = stageSamples.Count > 0 ? stageSamples.Average() : (double?)null;

                if (dropSatisfiedTime == null)
                {
                    TimeSpan dropAttemptDuration = DateTime.UtcNow - monitorStart;
                    return StageExecutionResult.Fail(
                        $"压力开关{triggeredSwitch}触发后{currentMonitorTimeout.TotalSeconds:F0}s内电流未稳定低于{currentDropThreshold:F1}mA",
                        peak: stagePeak,
                        avg: stageAvg,
                        pressureSwitchTriggered: true,
                        pressureSwitchAddress: triggeredSwitch,
                        pressureSwitchDelay: triggerDelay,
                        currentDropDuration: dropAttemptDuration);
                }

                TimeSpan dropDuration = dropSatisfiedTime.Value - monitorStart;
                string successMessage =
                    $"压力开关{triggeredSwitch}在{triggerDelay.TotalMilliseconds:F0}ms触发，电流{dropDuration.TotalMilliseconds:F0}ms降至{currentDropThreshold:F1}mA以下并保持{requiredStableDuration.TotalSeconds:F0}s";
                RaiseTestMessage($"通道{context.Channel} {successMessage}", context.Channel);

                return StageExecutionResult.Pass(
                    successMessage,
                    peak: stagePeak,
                    avg: stageAvg,
                    pressureSwitchTriggered: true,
                    pressureSwitchAddress: triggeredSwitch,
                    pressureSwitchDelay: triggerDelay,
                    currentDropDuration: dropDuration);
            }
            finally
            {
                if (massageKeyActivated)
                {
                    try
                    {
                        await _plcService.WriteBitAsync(massageKeyAddress, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RaiseTestMessage($"通道{context.Channel}按摩按键复位失败: {ex.Message}", context.Channel);
                    }
                }

                try
                {
                    await _commService.SendStopMessageAsync(context.Channel, context.ChannelConfig).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseTestMessage($"通道{context.Channel}发送停止报文失败: {ex.Message}", context.Channel);
                }

                await ResetModeSwitchAsync(context).ConfigureAwait(false);
            }
        }

        private Task<StageExecutionResult> CompleteStageAsync(ChannelTestContext context, CancellationToken token)
        {
            return Task.FromResult(StageExecutionResult.Pass("所有项目完成"));
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

        private (string CommandAddress, string[] StatusAddresses) ParseLumbarAddresses(LumbarTestConfig config, ChannelAddressMap addressMap, int channel)
        {
            var validAddresses = new HashSet<string>(
                addressMap.GetAllBitAddresses().Select(addr => $"M{addr}"),
                StringComparer.OrdinalIgnoreCase);

            var configuredAddresses = ParseAddressList(config.MRegisterAddress)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalid = new List<string>();

            foreach (var address in configuredAddresses)
            {
                var normalizedToken = NormalizeMAddressToken(address);
                if (normalizedToken != null && validAddresses.Contains(normalizedToken))
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
                string message = $"通道{channel} 腰托动作{config.Order}包含无效M寄存器: {string.Join("、", invalid)}";
                if (normalized.Count > 0)
                {
                    message += $"，已保留有效寄存器: {string.Join("、", normalized)}";
                }

                RaiseTestMessage(message + "。", channel);
            }

            if (normalized.Count == 0)
            {
                var defaults = GetDefaultLumbarAddresses(addressMap, config.Action).ToList();
                if (defaults.Count > 0)
                {
                    normalized.AddRange(defaults);
                    RaiseTestMessage($"通道{channel} 腰托动作{config.Order}已恢复默认寄存器: {string.Join("、", defaults)}。", channel);
                }
                else
                {
                    RaiseTestMessage($"通道{channel} 腰托动作{config.Order}缺少可用的M寄存器配置。", channel);
                }
            }

            if (normalized.Count > 0)
            {
                config.MRegisterAddress = string.Join(", ", normalized);
            }
            else
            {
                config.MRegisterAddress = string.Empty;
            }

            string? defaultCommand = GetDefaultLumbarCommandAddress(addressMap, config.Action);
            string? commandAddress = null;

            if (!string.IsNullOrWhiteSpace(defaultCommand))
            {
                commandAddress = normalized
                    .FirstOrDefault(a => string.Equals(a, defaultCommand, StringComparison.OrdinalIgnoreCase));
            }

            commandAddress ??= normalized.FirstOrDefault();
            commandAddress ??= defaultCommand ?? string.Empty;

            var statusAddresses = normalized
                .Where(a => !string.Equals(a, commandAddress, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!statusAddresses.Any())
            {
                statusAddresses.AddRange(GetDefaultLumbarStatusAddresses(addressMap, config.Action));
            }

            statusAddresses = statusAddresses
                .Where(a => !string.Equals(a, commandAddress, StringComparison.OrdinalIgnoreCase))
                .Where(validAddresses.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (commandAddress, statusAddresses.ToArray());
        }

        private static string? GetDefaultLumbarCommandAddress(ChannelAddressMap map, LumbarActionType action)
        {
            return action switch
            {
                LumbarActionType.UpInflateDownDeflate => $"M{map.UpInflateDownDeflate}",
                LumbarActionType.DownInflateUpDeflate => $"M{map.DownInflateUpDeflate}",
                LumbarActionType.SimultaneousInflate => $"M{map.BothInflate}",
                LumbarActionType.SimultaneousDeflate => $"M{map.BothDeflate}",
                _ => null
            };
        }

        private static IEnumerable<string> GetDefaultLumbarStatusAddresses(ChannelAddressMap map, LumbarActionType action)
        {
            return action switch
            {
                LumbarActionType.UpInflateDownDeflate => new[] { $"M{map.UpInflateDownDeflate}" },
                LumbarActionType.DownInflateUpDeflate => new[] { $"M{map.DownInflateUpDeflate}" },
                LumbarActionType.SimultaneousInflate => new[] { $"M{map.BothInflate}" },
                LumbarActionType.SimultaneousDeflate => new[] { $"M{map.BothDeflate}" },
                _ => Array.Empty<string>()
            };
        }

        private static IEnumerable<string> GetDefaultLumbarAddresses(ChannelAddressMap map, LumbarActionType action)
        {
            var defaults = new List<string>();
            var command = GetDefaultLumbarCommandAddress(map, action);
            if (!string.IsNullOrWhiteSpace(command))
            {
                defaults.Add(command);
            }

            defaults.AddRange(GetDefaultLumbarStatusAddresses(map, action));

            return defaults
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ParseAddressList(string? addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses))
                yield break;

            var separators = new[] { ',', ';', '|', ' ' };
            foreach (var part in addresses.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    yield return trimmed;
            }
        }

        private static string? NormalizeMAddressToken(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            string trimmed = address.Trim().ToUpperInvariant();
            if (!trimmed.StartsWith("M", StringComparison.Ordinal))
            {
                return null;
            }

            return int.TryParse(trimmed.Substring(1), out int numeric) ? $"M{numeric}" : null;
        }

        private async Task<bool> ReadBitAsync(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            var bits = await _plcService.ReadBitsAsync(address, 1).ConfigureAwait(false);
            return bits != null && bits.Length > 0 && bits[0];
        }

        private async Task<(double Height, double Current)> ReadHeightAndCurrentAsync(ChannelAddressMap map, int channel)
        {
            try
            {
                int currentStart = map.CurrentValueD;
                int currentEnd = map.CurrentValueD + 1;
                int start = Math.Min(map.HeightValueD, currentStart);
                int end = Math.Max(map.HeightValueD, currentEnd);
                ushort count = (ushort)(end - start + 1);

                string address = $"D{start}";
                short[] words = await _plcService.ReadWordsAsync(address, count).ConfigureAwait(false);

                if (words == null || words.Length < count)
                {
                    RaiseTestMessage($"高度读取失败: 地址{address}, 期望{count}个字, 实际{words?.Length ?? 0}", channel);
                    return (0, 0);
                }

                int heightIndex = map.HeightValueD - start;
                int currentLowIndex = map.CurrentValueD - start;
                int currentHighIndex = currentLowIndex + 1;

                if (currentHighIndex >= words.Length)
                {
                    RaiseTestMessage($"高度读取失败: 地址{address}, 期望{count}个字, 实际{words.Length}", channel);
                    return (0, 0);
                }

                short heightRaw = words[heightIndex];
                short currentLow = words[currentLowIndex];
                short currentHigh = words[currentHighIndex];

                double height = NormalizeHeight(heightRaw);
                int currentRaw = CombineToInt32(currentLow, currentHigh);
                double current = NormalizeCurrent(currentRaw);

                // 调试信息
                //OnTestMessage?.Invoke(this,
                //    $"高度读取: 原始值{heightRaw} → {height:F1}mm, 电流{currentRaw} → {current:F2}mA");

                return (height, current);
            }
            catch (Exception ex)
            {
                RaiseTestMessage($"高度读取异常: {ex.Message}", channel);
                return (0, 0);
            }
        }

        private static int ParseInt32FromWords(short[] words, int startIndex = 0)
        {
            if (words == null || startIndex < 0 || startIndex + 1 >= words.Length)
            {
                return 0;
            }

            short low = words[startIndex];
            short high = words[startIndex + 1];
            return CombineToInt32(low, high);
        }

        private static int CombineToInt32(short lowWord, short highWord)
        {
            uint low = unchecked((ushort)lowWord);
            uint high = unchecked((ushort)highWord);
            uint combined = (high << 16) | low;
            return unchecked((int)combined);
        }

        private static byte[] PrepareMessageBytes(ushort[] payload, int registerCount)
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

        private static double NormalizeHeight(short raw)
        {
            return raw < 0 ? 0 : raw;
        }

        private static double NormalizeCurrent(int raw) => raw / 100.0;

        private static (int sendStart, int receiveStart, int displayAddress) GetStatusMessageAddresses(int channel)
        {
            return channel switch
            {
                1 => (160, 260, 264),
                2 => (180, 280, 284),
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "仅支持通道1或通道2")
            };
        }

        private static string GetStatusDisplayLabel(int channel, int displayAddress)
        {
            return channel switch
            {
                1 => "帧ID",
                2 => "帧ID",
                _ => $"D{displayAddress}"
            };
        }

        private static ChannelAddressMap GetChannelAddressMap(int channel)
        {
            return channel switch
            {
                1 => PLCAddressMapper.Channel1Addresses,
                2 => PLCAddressMapper.Channel2Addresses,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "仅支持通道1或通道2")
            };
        }

        private static string GetChannelSendAddress(int channel)
        {
            var map = GetChannelAddressMap(channel);
            return $"D{map.CommSendStart}";
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
                    .Where(c => c != null && c.Enabled && !string.IsNullOrWhiteSpace(c.PressureSwitchAddress))
                    .DistinctBy(c => c.Point)
                    .OrderBy(c => c.Point)
                    .ToList();

                if (!enabledConfigs.Any())
                {
                    return BuildSuccess("未配置可用的按摩压力开关", Array.Empty<double>(), Array.Empty<MassagePointTracker>());
                }

                if (_settings.TotalDuration <= 0)
                {
                    return BuildFailure("按摩动作总长未配置或为零", Array.Empty<double>(), Array.Empty<MassagePointTracker>());
                }

                var trackers = enabledConfigs.ToDictionary(c => c.Point, c => new MassagePointTracker(c));

                foreach (var tracker in trackers.Values)
                {
                    bool initialState = await _readSwitchAsync(tracker.Config.PressureSwitchAddress, cancellationToken)
                        .ConfigureAwait(false);
                    if (initialState)
                    {
                        string message = $"按摩点{tracker.Config.Point}压力开关初始为高电平";
                        return BuildFailure(message, Array.Empty<double>(), trackers.Values, tracker);
                    }
                }

                var stageCurrents = new List<double>();
                var startTime = _utcNowProvider();
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
                        bool isHigh = await _readSwitchAsync(tracker.Config.PressureSwitchAddress, cancellationToken)
                            .ConfigureAwait(false);

                        if (isHigh)
                        {
                            if (!tracker.IsHigh)
                            {
                                var otherActive = trackers.Values
                                    .Where(t => t.IsHigh && !ReferenceEquals(t, tracker))
                                    .OrderBy(t => t.HighStart)
                                    .FirstOrDefault();

                                if (otherActive != null)
                                {
                                    string concurrentMessage =
                                        $"按摩点{otherActive.Config.Point}与点{tracker.Config.Point}同时触发";
                                    return BuildFailure(concurrentMessage, stageCurrents, trackers.Values, otherActive);
                                }

                                tracker.IsHigh = true;
                                tracker.HighStart = now;
                                tracker.TriggerCount++;
                                tracker.PressureSwitchTriggered = true;
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

                    _onProgress?.Invoke(BuildProgressMessage(current, trackers.Values));

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

                foreach (var tracker in trackers.Values)
                {
                    if (!tracker.PressureSwitchTriggered)
                    {
                        string message = $"按摩点{tracker.Config.Point}在规定总时长内未触发";
                        return BuildFailure(message, stageCurrents, trackers.Values, tracker);
                    }
                }

                return BuildSuccess("按摩测试通过", stageCurrents, trackers.Values);
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
                    : "无";

                return $"电流 {current:F1} mA，高电平点: {activeText}";
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
                    result.Message = "未触发";
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
                    return $"按摩点{tracker.Config.Point}高电平持续{duration:F0}ms低于下限{_settings.HighLevelDurationMin}ms";
                }

                if (duration > _settings.HighLevelDurationMax)
                {
                    var timeoutMessage = $"按摩点{tracker.Config.Point}高电平持续{duration:F0}ms超过上限{_settings.HighLevelDurationMax}ms";
                    return treatAsTimeout ? timeoutMessage + " (超时)" : timeoutMessage;
                }

                if (tracker.LastPeakCurrent < _settings.PeakCurrentMin || tracker.LastPeakCurrent > _settings.PeakCurrentMax)
                {
                    return $"按摩点{tracker.Config.Point}峰值电流{tracker.LastPeakCurrent:F1}mA超出范围[{_settings.PeakCurrentMin},{_settings.PeakCurrentMax}]mA";
                }

                if (_currentOverLimit.HasValue && tracker.LastPeakCurrent > _currentOverLimit.Value)
                {
                    return $"按摩点{tracker.Config.Point}峰值电流{tracker.LastPeakCurrent:F1}mA超过通道上限{_currentOverLimit.Value:F1}mA";
                }

                if (averageCurrent < _settings.AverageCurrentMin || averageCurrent > _settings.AverageCurrentMax)
                {
                    return $"按摩点{tracker.Config.Point}平均电流{averageCurrent:F1}mA超出范围[{_settings.AverageCurrentMin},{_settings.AverageCurrentMax}]mA";
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
                public bool PressureSwitchTriggered { get; set; }
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
                        PressureSwitchTriggered = PressureSwitchTriggered,
                        Duration = Durations.Count > 0 ? (int?)Math.Round(Durations.Average()) : null,
                        PeakCurrent = Peaks.Count > 0 ? (double?)Peaks.Max() : null,
                        AverageCurrent = weightedAverage,
                        TriggerCount = TriggerCount,
                        TotalHighDuration = Durations.Count > 0 ? totalDuration : null,
                        MaxHighDuration = Durations.Count > 0 ? Durations.Max() : null,
                        MinHighDuration = Durations.Count > 0 ? Durations.Min() : null,
                        LastHighDuration = Durations.Count > 0 ? LastHighDuration : null,
                        Passed = passedOverride ?? PressureSwitchTriggered,
                        Message = messageOverride ?? (PressureSwitchTriggered ? $"触发{TriggerCount}次" : "未触发")
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
            public required TestStartOptions Options { get; init; }
            public required TestRecord Record { get; init; }
            public required CancellationTokenSource Cancellation { get; init; }
            public bool ModeSwitchPowerOffActive { get; set; }
            public bool ModeSwitchDriverActive { get; set; }
        }

        private class StageExecutionResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public double? PeakCurrent { get; init; }
            public double? AverageCurrent { get; init; }
            public double? Height { get; init; }
            public double? SleepCurrent { get; init; }
            public bool PressureSwitchTriggered { get; init; }
            public string? PressureSwitchAddress { get; init; }
            public TimeSpan? PressureSwitchDelay { get; init; }
            public TimeSpan? CurrentDropDuration { get; init; }

            public static StageExecutionResult Pass(
                string message,
                double? peak = null,
                double? avg = null,
                double? height = null,
                double? sleepCurrent = null,
                bool pressureSwitchTriggered = false,
                string? pressureSwitchAddress = null,
                TimeSpan? pressureSwitchDelay = null,
                TimeSpan? currentDropDuration = null)
            {
                return new StageExecutionResult
                {
                    Success = true,
                    Message = message,
                    PeakCurrent = peak,
                    AverageCurrent = avg,
                    Height = height,
                    SleepCurrent = sleepCurrent,
                    PressureSwitchTriggered = pressureSwitchTriggered,
                    PressureSwitchAddress = pressureSwitchAddress,
                    PressureSwitchDelay = pressureSwitchDelay,
                    CurrentDropDuration = currentDropDuration
                };
            }

            public static StageExecutionResult Fail(
                string message,
                double? peak = null,
                double? avg = null,
                double? height = null,
                double? sleepCurrent = null,
                bool pressureSwitchTriggered = false,
                string? pressureSwitchAddress = null,
                TimeSpan? pressureSwitchDelay = null,
                TimeSpan? currentDropDuration = null)
            {
                return new StageExecutionResult
                {
                    Success = false,
                    Message = message,
                    PeakCurrent = peak,
                    AverageCurrent = avg,
                    Height = height,
                    SleepCurrent = sleepCurrent,
                    PressureSwitchTriggered = pressureSwitchTriggered,
                    PressureSwitchAddress = pressureSwitchAddress,
                    PressureSwitchDelay = pressureSwitchDelay,
                    CurrentDropDuration = currentDropDuration
                };
            }
        }
    }
}
