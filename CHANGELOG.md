# 更新日志 / Changelog

本文件记录项目的重要变更,格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/),版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

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

[1.1.0]: https://github.com/asckye/Production-Dashboard/releases/tag/v1.1.0
