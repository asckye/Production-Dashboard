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
