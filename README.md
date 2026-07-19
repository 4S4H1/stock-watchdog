# StockWatchdog

StockWatchdog 是一款面向 Windows 11 的个人本地 A 股与场内 ETF 盯盘工具。

## 主要功能

- 置顶小浮窗：批量显示自选标的、现价、涨跌幅、信号、行情时间和迷你走势图；
  迷你走势与详情共用最近 120 根已完成的 1 分钟 K 线。
- 两种浮窗模式：信息丰富模式，以及只保留行情表格的精简模式。
- 自选配置：支持添加、删除、排序、自定义标的名称，以及逐列显示或隐藏。
- 双击任意标的展开技术详情；默认显示 1 分钟 K 线，可切换 5/15/60 分钟和日线。
- 技术分析：VWAP、EMA、布林带、支撑阻力、趋势、量价和可解释形态，可单独隐藏指标。
- 做 T 双评分：结合 5 秒行情快照的短时量速、已完成分钟 K 线、多周期趋势和量价形态，
  独立给出买入条件分与卖出条件分；达到阈值并连续确认后触发候选提醒。
- 行情颜色统一采用 A 股习惯：红涨绿跌。
- 快速复盘标记：在 K 线图上按 `Ctrl+左键` 标记买点，按 `Ctrl+右键` 或
  `Ctrl+Shift+左键` 标记卖点；系统从标记位置绘制到最新已完成 K 线的区间框。
  上升区间显示红色盈利框，下降区间显示绿色失败/额外损失框。
  双击任意区间框的填充区域可单独删除该框及其标记。
- 固定价格与技术形态提醒，包含数据质量门、去重和冷却机制。
- 全局老板键 `Ctrl+Alt+H`、托盘、锁屏自动隐藏，以及浅色、深色、电子表格三套皮肤。
- 自选表格支持再次点击当前行或点击空白区域取消选择；深色主题覆盖图表、弹窗、菜单、
  下拉框、表格和原生标题栏。
- 设备迁移：可把界面设置、自选列表、提醒规则和自定义主题导出为一段以 `SWCFG1`
  开头的便携文本，在另一台设备校验并导入。
- 本地 SQLite 存储，无云同步、无遥测、无券商接入、无自动交易。

> 行情来自没有 SLA 的公开网页接口，仅适合个人原型验证。数据延迟、异常或主备源
> 冲突时，应用会暂停技术提醒。图表标记是用户复盘记录，不代表订单或成交，也不构成
> 投资建议。买卖条件分表示规则匹配强度，不代表成功概率、收益率或交易建议。

## 首次使用

1. 启动后输入 6 位沪深代码并添加；双击表格行展开技术详情。
2. 在“设置”中选择刷新周期、浮窗模式、可见列、皮肤和老板键。
3. 需要别名时，在设置中的自选标的列表直接修改显示名称。
4. 在详情页选择需要显示的指标；普通点击不会写入图表标记。
5. 迁移设备时，在“设置 → 设备迁移”选择“导出全部配置”，复制完整文本；在目标设备
   选择“导入配置文本”并确认。行情缓存、图表标记和历史提醒不会被覆盖。

本地数据位于 `%LocalAppData%\StockWatchdog`。分钟/日线缓存、自选、设置、提醒和
图表标记均保存在该目录中。

## 开发

项目使用仓库内 `.dotnet` 目录中的 .NET 10 SDK：

```powershell
.\.dotnet\dotnet.exe restore StockWatchdog.slnx
.\.dotnet\dotnet.exe test StockWatchdog.slnx
.\.dotnet\dotnet.exe run --project src\StockWatchdog.App
```

只验证公开行情适配器：

```powershell
.\.dotnet\dotnet.exe run --project tools\StockWatchdog.ProviderProbe -- 600519 000001 510300
```

离屏渲染丰富浮窗、精简浮窗和技术详情，用于 UI 冒烟检查：

```powershell
.\.dotnet\dotnet.exe run --project tools\StockWatchdog.UiSmoke -c Release -- artifacts\ui-smoke
```

发布便携包：

```powershell
.\.dotnet\dotnet.exe publish src\StockWatchdog.App -c Release -r win-x64 --self-contained -o artifacts\publish
```

更多资料：

- [架构与安全门](docs/architecture.md)
- [自定义主题样例](docs/theme.example.json)
