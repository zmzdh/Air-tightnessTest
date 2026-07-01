# 自动化按摩测试动作流程与判定逻辑

本文档基于 `Services/TestService.cs` 中的实现，对当前系统在自动测试阶段执行“按摩测试”时的动作步骤与判定逻辑进行梳理说明，帮助理解测试过程中 PLC 指令、采样判断与结果记录的关系。

## 测试入口条件

- 自动测试流程会在 `TestService` 中串行执行各阶段。当产品工艺配置 (`ProcessConfig`) 的 `EnableMassageTest` 为 `true` 时，系统才会进入按摩测试阶段；否则直接调用 `SkipStage` 并跳过该阶段。【F:Services/TestService.cs†L292-L318】【F:ModelS/TestData.cs†L701-L738】

## 报文加载与触发

1. **手动报文激活**：阶段开始时调用 `ActivateManualMassageMessageAsync`，仅把型号配置中的按摩报文写入 PLC 的发送缓冲区；若报文缺失则立即清空缓冲区并返回失败。【F:Services/TestService.cs†L1104-L1116】
2. **自动报文发送**：确认存在启用的按摩点后，通过 `_commService.SendMassageMessageAsync` 将自动测试指令写入控制板（同样只需写缓冲区即可）。发送失败会直接返回阶段失败。【F:Services/TestService.cs†L1202-L1216】
3. **资源回收**：阶段结束（无论成功或失败）都会调用 `DeactivateManualMassageMessageAsync` 清空对应发送缓冲区，确保手动报文不会残留在 PLC 中。【F:Services/TestService.cs†L1251-L1256】

## 总时长采样与共享参数

新版逻辑不再逐条执行单独的动作时长，而是按照 `ChannelConfig.MassageTestSettings` 中配置的“总时长”与共享阈值执行统一的采样循环：【F:Services/TestService.cs†L1820-L1935】【F:ModelS/TestData.cs†L719-L778】

1. **初始状态校验**：进入循环前会逐个读取所有启用点的压力开关，若发现任意一个在开始时即为高电平，阶段直接返回失败，避免错误的起始条件。【F:Services/TestService.cs†L1838-L1845】
2. **实时采样**：在总时长内（默认 30s）以 200ms 周期读取通道电流和每个压力开关状态。采样过程会记录：
   - 阶段电流曲线并追加到 `TestRecord.MassageCurrents`；
   - 当前高电平点列表，通过 `RaiseStageChanged` 推送到界面显示；
   - 针对每个高电平周期累积持续时间、平均电流与峰值电流，用于事后判定。【F:Services/TestService.cs†L1211-L1233】【F:Services/TestService.cs†L1854-L1913】
3. **并发触发监控**：阶段内会统计当前处于高电平的点位数量，若超过机型配方中 `MassageTestSettings.MaxConcurrentPoints`（范围 2-8）则判定失败。允许在该上限内发生并发触发。【F:Services/TestService.cs†L1853-L1860】【F:ModelS/TestData.cs†L1220-L1255】

## 判定规则

共享阈值全部来源于 `MassageTestSettings`，适用于所有启用的压力开关：【F:ModelS/TestData.cs†L719-L778】

1. **单次高电平判定**：每次从高变低时都会调用 `FinalizeHighEvent`，按以下规则校验：
   - 高电平持续时间必须位于 `HighLevelDurationMin` ~ `HighLevelDurationMax` 区间内；若在总时长结束仍未恢复低电平，也按超时处理。
   - 峰值电流需落在 `PeakCurrentMin` ~ `PeakCurrentMax` 范围，且不得超过通道配置的 `CurrentOverLimit`。
   - 平均电流需位于 `AverageCurrentMin` ~ `AverageCurrentMax` 区间。
   任一条件不满足即返回失败，并在对应点位的 `MassagePointResult` 中标记原因。【F:Services/TestService.cs†L1938-L2045】
2. **触发次数校验**：总时长结束后仍未触发（高电平次数为 0）的点会单独返回失败，提示“在规定总时长内未触发”。【F:Services/TestService.cs†L1926-L1932】
3. **并发点数上限**：若高电平并发点数超过 `MaxConcurrentPoints`，立即判定失败并终止该阶段。【F:Services/TestService.cs†L1853-L1860】

## 结果封装与落库

- `MassagePointResult` 记录了每个点的触发次数、高电平累计时长、最后一次持续时间及电流判定结果，供报表与 MES 上传使用。【F:Services/TestService.cs†L2048-L2099】【F:Services/MesService.cs†L232-L260】
- 阶段结束时会统计总采样曲线的峰值与平均值，分别写入 `TestRecord.MassageMaxCurrent` 与 `TestRecord.MassageAverageCurrent`，并在 `MassageResults` 中保留每个点的详细判定信息。【F:Services/TestService.cs†L1215-L1239】【F:ModelS/TestData.cs†L742-L781】

通过上述流程，系统能够在统一的总时长内监测所有压力开关的触发行为和电流表现，既能捕获未触发、触发过短/过长、双点并发等故障，也能为每个点提供统一的统计结果与判定结论。
