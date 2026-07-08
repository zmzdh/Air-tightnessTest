# 系统设置零点校正与手动调试模式设计

日期: 2026-08-06

## 背景

两个独立的小改动，均围绕"配置点数/按钮数量应跟随通道数或运行模式而变化"。

## 功能 1: 压力变送器零点校正按通道数显示

### 现状

- `SystemSettingsControl.xaml` 中"压力变送器零点校正" GroupBox 的 `UniformGrid` 固定显示 4 个通道面板（通道1-4），每个面板含 `TextBox` + `一键获取`按钮 + 状态 `TextBlock`。
- "全局配置"中通道数量下拉框 `CmbChannelCount` 支持 2/3/4，但零点校正始终显示 4 通道。
- `TryBuildConfigFromInputs` 无条件校验全部 4 个通道的零点原始值，隐藏通道（如果有）也会被校验。

### 需求

零点校正的配置点数跟随通道数量变化：通道数量为 2 时只显示通道 1-2，为 3 时显示 1-3，为 4 时显示 1-4。

### 行为约定（已与用户确认）

1. **保存配置后生效**：切换通道数量下拉框时界面不变；点击"保存配置"成功后才按新通道数量显示零点校正区域。
2. **隐藏通道值保留**：被隐藏通道的零点原始值仍保存在配置中，仅界面不显示；切回更多通道时数值仍在。

### 实现

改动文件：`Views/SystemSettingsControl.xaml`、`Views/SystemSettingsControl.xaml.cs`。

1. 给 4 个通道面板 `StackPanel` 添加 `x:Name`：`ZeroPanel1`、`ZeroPanel2`、`ZeroPanel3`、`ZeroPanel4`。
2. 新增方法：

```csharp
private void UpdateZeroCalibrationVisibility(int channelCount)
{
    ZeroPanel1.Visibility = channelCount >= 1 ? Visibility.Visible : Visibility.Collapsed;
    ZeroPanel2.Visibility = channelCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
    ZeroPanel3.Visibility = channelCount >= 3 ? Visibility.Visible : Visibility.Collapsed;
    ZeroPanel4.Visibility = channelCount >= 4 ? Visibility.Visible : Visibility.Collapsed;
}
```

3. 调用时机（两处）：
   - `LoadConfigurationAsync` 加载成功后，按 `_currentConfig.ChannelCount`（或默认值）调用。
   - `BtnSaveConfig_Click` 保存成功后，按 `config.ChannelCount` 调用。
4. 校验调整：`TryBuildConfigFromInputs` 仅校验 `1..channelCount` 范围内的通道；超出部分（隐藏通道）直接沿用 `_currentConfig` 中的配置值，不校验文本框内容，也不从文本框取值。

### 测试

- 通道数量 2/3/4 时保存，零点校正分别显示 2/3/4 个通道面板。
- 4 通道下设置通道 3/4 零点值后切到 2 通道并保存，再切回 4 通道，通道 3/4 数值仍在。
- 2 通道下隐藏通道文本框内容任意（含非法值），保存不报错。

## 功能 2: 手动调试模式仅状态显示，不实际启动/停止测试

### 现状

- `MainWindow.ReadPLCContinuously` 每 100ms 轮询 PLC 位，检测气密启动（`AirLeakStartButton`/`FullTestStart`）与停止（`StopButton`）信号上升沿。
- `HandlePlcSignals` 在上升沿时通过 `Dispatcher.InvokeAsync` 调度 `OnChannelStartSignalAsync`/`OnChannelStopSignalAsync`。
- `OnChannelStartSignalAsync` 会调用 `ShowTestControl()` 跳转到测试界面并实际启动测试；`OnChannelStopSignalAsync` 会实际停止测试。
- `HandleFullTestHoldAbort` 在运行中长按启动按钮 2 秒会中止测试。
- 手动调试界面（`ManualControl`）本身已有"气密启动按钮"、"停止按钮"指示灯（`ChXFullTestButtonIndicator`/`ChXStopButtonIndicator`），通过 `UpdateWithPLCData` 每 100ms 更新为绿/灰。

### 需求

在手动调试模式下检测到气密测试按钮（或停止按钮）时，只在本界面做状态显示（指示灯亮/灭），不实际启动或停止测试。

### 行为约定（已与用户确认）

- 停止按钮与启动按钮同样处理：手动调试模式下不停止实际测试。
- 手动调试模式定义为 `MainContentControl.Content is ManualControl`（当前显示手动调试视图）。
- 测试界面（非手动调试）下行为完全不变。

### 实现

改动文件：`Views/MainWindow.xaml.cs`。

1. 新增辅助方法（UI 线程安全检查）：

```csharp
private bool IsManualDebugModeActive()
{
    if (Dispatcher.CheckAccess())
    {
        return MainContentControl.Content is ManualControl;
    }
    return Dispatcher.Invoke(() => MainContentControl.Content is ManualControl);
}
```

2. 在以下方法开头加守卫 `if (IsManualDebugModeActive()) return;`：
   - `OnChannelStartSignalAsync`
   - `OnChannelStopSignalAsync`
   - `HandleFullTestHoldAbort`

3. 上升沿状态 `_prevCh*Signal` 照常更新（不随守卫改变），确保切回测试界面后不会误触发。

### 测试

- 手动调试界面按下气密启动按钮，指示灯变绿，不跳转测试界面、不启动测试。
- 手动调试界面按下停止按钮，指示灯变绿，不停止任何测试。
- 手动调试界面长按启动按钮 2 秒以上，不触发测试中止。
- 切回测试界面后，启动/停止按钮仍正常触发测试启停。

## 范围

- 不涉及数据库/配置结构变更（`SystemConfig` 字段不变）。
- 不涉及安装打包变更。
