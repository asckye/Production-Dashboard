# 更新日志 / Changelog

本文件记录项目的重要变更,格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/),版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### 修复 / Fixed

- 「班次实时对比」中的领先标签(本周 / 本月 / 本年差值)和「本月领先」徽章固定显示 A 班颜色;现在随领先方变化:A 班橙色、B 班蓝色、持平为中性色 / Leader labels in the shift comparison (week / month / year deltas and the monthly leader badge) always used team A's color; they now follow the leading team (A orange, B blue, neutral when tied)
- 输入正确的退出密码后窗口关闭但进程不退出(后台服务停止时与 UI 线程互相等待),残留进程还会继续连接 PLC;现在几秒内完整退出,所有后台服务正常停止 / After the correct exit password the window closed but the process kept running (background services and the UI thread waited on each other) and kept polling the PLC; exiting now completes within seconds and stops every background service cleanly
- Alt+F4 或任务栏关闭可以绕过退出密码直接关掉大屏;现在同样要输入密码 / Alt+F4 or closing from the taskbar bypassed the exit password; it now asks for the password too
- 看门狗结束不了卡死的主程序(结束进程树时包含看门狗自己而报错),结束失败时还会再启动一个实例;现在只结束主程序,确认退出后才重启 / The watchdog could not kill a hung main program (killing the process tree included the watchdog itself and threw), and it started another instance even when the kill failed; it now kills only the main program and restarts only after it has exited
- 看门狗用系统时间判断心跳是否过期,校时或改系统时间会误判;现在按心跳最后一次更新后经过的时间判断,一直读不到心跳文件也会在超时后重启 / The watchdog judged heartbeat staleness by wall-clock time, so clock changes caused false or missed restarts; it now measures the time since the heartbeat last changed, and a heartbeat file that never appears also triggers a restart after the timeout
- 看门狗读心跳文件的同时,主程序覆盖心跳偶尔会"拒绝访问"并记录告警 / Overwriting the heartbeat occasionally failed with "access denied" while the watchdog was reading it
- 退出 / 登录密码框按一次回车会校验两次,输错一次被记为两次失败,更快触发锁定 / Pressing Enter once in the exit or login password box validated twice, so one wrong entry counted as two failures and triggered the lockout sooner
- 密码框打开时鼠标仍会因空闲被隐藏 / The mouse cursor could be hidden by the idle timer while a password dialog was open
- 窗口小于 1920×1080 时界面底部和顶栏右侧被截断;现在整个界面按 1920×1080 设计、随窗口等比缩放,2K / 4K 屏(系统缩放 100%)也会相应放大,不再字小、留白 / Below 1920×1080 the bottom of the dashboard and the right end of the top bar were cut off; the UI is now designed at 1920×1080 and scales proportionally with the window, and it scales up on 2K / 4K screens at 100% system scaling instead of leaving small text and empty space
- 顶栏「当前班次」徽章和班次对比卡的高亮框绑定路径错误,夜班生效时颜色和高亮不切换 / The top-bar current-shift badge and the shift comparison card highlight had broken binding paths, so their color and highlight never switched when the night shift was active
- 班组配置页滚动条为系统默认的浅灰色外观;现在随深色 / 浅色主题显示 / Scrollbars on the shift configuration page used the default light-gray Windows look; they now follow the dark / light theme
- 同一年再次归档时会覆盖之前的归档 Excel 和清库前备份;如果上次清库中途被中断,第二次归档只含剩余数据,完整的那份就被冲掉了。现在每次归档都另存新文件 / Archiving the same year again overwrote the earlier archive Excel and pre-purge backup; if a purge had been interrupted, the second archive held only the leftover data and replaced the complete one. Every archive now gets its own file
- 握手模式断线重连后会把序号重新对齐,断线期间 PLC 记下的件数全部丢失;现在重连后按序号差补上。PLC 重启导致序号归零时识别为计数器复位并重新对齐,不会一次计入几十亿件 / In Handshake mode a reconnect re-aligned the sequence number, losing every part the PLC counted while disconnected; the difference is now recovered after reconnecting. A counter reset caused by a PLC restart is detected and re-aligned instead of counting billions of parts at once
- 分钟聚合只处理"上一分钟",定时器晚到、聚合失败、程序重启或事件晚落库都会漏掉分钟,本周 / 本月 / 本年累计偏少;现在每次重算最近几分钟并从上次的位置补起,升级时用原始事件补全历史数据(数据库迁移 v3) / Minute aggregation only processed the previous minute, so a late timer, a failed run, a restart or late-persisted events left gaps and undercounted the week / month / year totals; it now recomputes recent minutes and catches up from where it left off, and upgrading backfills history from the raw events (database migration v3)
- 班次交接后最多 30 秒内的产量仍记在上一个班次 / 班组名下;现在按信号自己的时间戳归属,班次切换检测间隔也从 30 秒缩短到 1 秒 / For up to 30 seconds after a shift handover, output was still credited to the previous shift and team; it is now attributed by each signal's own timestamp, and the shift-change check runs every second instead of every 30
- 删除的班次当天仍然生效,保存后又出现在列表里 / A deleted shift stayed in effect for the rest of the day and reappeared in the list after saving
- 持续性故障(数据库写入失败、通道积压、聚合失败)的界面告警因限流计算溢出而从不弹出 / Alerts for persistent failures (database write failures, channel backlog, aggregation failures) never appeared because the throttle calculation overflowed
- PLC 能连上但读不了时(DB 号写错、访问保护等)会立即反复重连,每秒几十次;现在读取失败后同样按 1、2、4…秒退避 / When the PLC accepted connections but reads failed (wrong DB number, access protection), the dashboard reconnected in a tight loop, dozens of times per second; read failures now back off 1, 2, 4… seconds too
- 内置「三班倒」「四班三运转」模板里的 24:00 被判为格式错误,无法保存;现在时间可以填 24:00 / The built-in three-shift templates use 24:00, which was rejected as invalid, so they could not be saved; 24:00 is now accepted
- 班次覆盖检测只比较时长总和(00:00–12:00 加 06:00–18:00 也显示"覆盖完整"),保存时也不检查;现在逐分钟计算,重叠时提示并阻止保存,空档只提示 / Coverage checking only compared total duration (00:00–12:00 plus 06:00–18:00 showed as complete) and saving never checked it; coverage is now computed minute by minute, overlaps block saving and gaps show a warning
- 班次保存成功、班组保存失败后再点保存,会把新增的班次再插入一遍;现在两部分都校验通过才保存,班次修改在一个事务里完成并立即刷新编辑器 / If shifts saved but teams failed, saving again inserted the new shifts a second time; both parts are now validated before anything is written, shift changes are saved in one transaction, and the editor reloads right away

## [1.1.0] - 2026-10-11

首次发布 / Initial release.

### 功能 / Features

- PLC 完成信号实时计数,支持 Edge(上升沿 + 去抖)与 Handshake(零漏读)两种采样模式 / Real-time counting of PLC completion signals in Edge (rising edge + debounce) or Handshake (zero missed counts) mode
- 本小时 / 今日 / 本周 / 本月 / 本年 5 维度看板,含目标完成度与趋势对比 / Five dashboard dimensions (hour / day / week / month / year) with target completion and trend comparison
- 班次与班组完全可配置,倒班后产量跟班组走 / Fully configurable shifts and teams; output stays with the team after a shift swap
- 班次结束自动导出 Excel、跨年归档、断电续传、看门狗自恢复、审计日志 / Automatic Excel export per shift, yearly archiving, resume after power loss, watchdog self-recovery, audit log
- 深色 / 浅色主题 / Dark and light themes

### 工程 / Engineering

- Core / Infrastructure / App 三层结构,分层规则由项目引用与 `LayeringTests` 保证 / Core / Infrastructure / App layers, enforced by project references and `LayeringTests`
- 架构与开发规范、贡献指南、行为准则、安全策略、Issue 与 PR 模板 / Architecture guidelines, contributing guide, code of conduct, security policy, issue and PR templates
- GitHub Actions CI:Windows 上构建并运行测试 / GitHub Actions CI that builds and tests on Windows
- `tools/plc-sim`:无需硬件的软件 S7 PLC,用于本地开发与演示 / `tools/plc-sim`: a software S7 PLC for local development and demos, no hardware needed

[Unreleased]: https://github.com/asckye/Production-Dashboard/compare/v1.1.0...main
[1.1.0]: https://github.com/asckye/Production-Dashboard/releases/tag/v1.1.0
