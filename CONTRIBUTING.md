# 贡献指南 / Contributing

[中文](#中文) | [English](#english)

---

## 中文

感谢你愿意为 ProductionDashboard 出力!缺陷报告、功能建议、文档改进和代码提交都欢迎。参与即表示你同意遵守 [行为准则](CODE_OF_CONDUCT.md)。

### 提 issue 之前

- 先搜索已有 issue,避免重复。
- 缺陷请用「问题报告」模板,附上版本号、PLC 型号、采样模式和相关日志(先去除 IP、路径等敏感信息)。
- **安全漏洞不要公开提交**,请按 [SECURITY.md](SECURITY.md) 私下报告。
- 较大的改动(新功能、数据库结构、PLC 协议)请先开 issue 讨论方案,再动手写代码。

### 开发环境

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022(17.14 及以上)、Rider,或 VS Code + C# Dev Kit
- 可选:Python 3.10+,用于运行 [`tools/plc-sim`](tools/plc-sim) 软件 PLC

### 构建与测试

```powershell
dotnet build
dotnet test
```

- 构建开启了 `TreatWarningsAsErrors`,**任何警告都会让构建失败**。
- 性能基准测试带 `Category=Performance`,CI 中跳过,本地单独运行:`dotnet test --filter "Category=Performance"`。

### 没有 PLC 时本地运行

用软件 PLC 模拟完成信号(默认 Edge 模式,DB100,监听 127.0.0.1:102):

```powershell
pip install -r tools/plc-sim/requirements.txt
python tools/plc-sim/plc_sim.py
```

另开一个终端,以窗口模式启动大屏并指向本机:

```powershell
dotnet run --project src/ProductionDashboard -- --Plc:IpAddress=127.0.0.1 --Display:Fullscreen=false --Watchdog:Enabled=false
```

`dotnet run` 以构建输出目录(如 `src/ProductionDashboard/bin/Debug/net10.0-windows/`)为工作目录:`appsettings.json` 从这里读取,运行数据(`Data/` 数据库与 Excel 导出、`logs/` 日志)也写在这里。验证 Handshake 模式时,模拟器加 `--mode handshake`,大屏加 `--Plc:SampleMode=Handshake`。

### 代码约定

完整规范见 **[架构与开发规范](docs/ARCHITECTURE.md)**,要点:

- **三层结构,依赖只能从上往下**:App → Infrastructure → Core。业务规则写在 Core,数据库 / PLC / Excel 的实现写在 Infrastructure,界面写在 App。`LayeringTests` 会检查这条规则。
- **ViewModel 不直接访问数据仓库**,调用 Core 服务(如 `IProductionStatisticsService`、`IShiftConfigurationService`);弹窗通过 `IDialogService`。
- **时间统一用注入的 `TimeProvider`**,不要直接用 `DateTime.Now`。
- **文件夹 = 命名空间**,测试目录与 `src/` 一一对应。
- 格式以 [`.editorconfig`](.editorconfig) 为准(Allman 大括号、4 空格缩进);公共类型和成员写中文 XML 文档注释。
- 新逻辑需配套单元测试;数据库结构变更只能**新增**迁移,不要修改已发布的迁移。
- 用户可见的行为或配置变更,需同时更新 `README.md` 和 `README.en.md`。

### 提交与 Pull Request

1. Fork 仓库,从 `main` 切出功能分支(如 `fix/handshake-wrap`)。
2. 提交信息遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/):`feat:`、`fix:`、`refactor:`、`docs:`、`test:`、`chore:` 等,描述可用中文或英文。
3. 每个 PR 聚焦一件事;提交前确认 `dotnet build` 无警告、`dotnet test` 全部通过。
4. 按 PR 模板填写变更说明和自检清单,等待 CI 通过和维护者评审。

提交即表示你同意以本项目的 [MIT 许可证](LICENSE) 授权你的贡献。

---

## English

Thanks for your interest in improving ProductionDashboard! Bug reports, feature ideas, documentation fixes and code are all welcome. By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

### Before opening an issue

- Search existing issues to avoid duplicates.
- For bugs, use the "Bug report" template and include the version, PLC model, sample mode and relevant logs (remove IPs, paths and other sensitive data first).
- **Do not report security vulnerabilities publicly**; follow [SECURITY.md](SECURITY.md) instead.
- For larger changes (new features, database schema, PLC protocol), open an issue to discuss the approach before writing code.

### Development environment

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 (17.14 or later), Rider, or VS Code with C# Dev Kit
- Optional: Python 3.10+ to run the [`tools/plc-sim`](tools/plc-sim) software PLC

### Build and test

```powershell
dotnet build
dotnet test
```

- The build uses `TreatWarningsAsErrors`, so **any warning fails the build**.
- Performance benchmarks are tagged `Category=Performance`. CI skips them; run them locally with `dotnet test --filter "Category=Performance"`.

### Running locally without a PLC

Start the software PLC, which simulates completion signals (Edge mode by default, DB100, listening on 127.0.0.1:102):

```powershell
pip install -r tools/plc-sim/requirements.txt
python tools/plc-sim/plc_sim.py
```

In a second terminal, start the dashboard in windowed mode, pointed at the local machine:

```powershell
dotnet run --project src/ProductionDashboard -- --Plc:IpAddress=127.0.0.1 --Display:Fullscreen=false --Watchdog:Enabled=false
```

`dotnet run` uses the build output folder (e.g. `src/ProductionDashboard/bin/Debug/net10.0-windows/`) as its working directory: `appsettings.json` is read from there, and runtime data (`Data/` database and Excel exports, `logs/`) is written there too. To test Handshake mode, add `--mode handshake` to the simulator and `--Plc:SampleMode=Handshake` to the dashboard.

### Code conventions

See the **[Architecture and Development Guidelines](docs/ARCHITECTURE.en.md)** for the full rules. In short:

- **Three layers, dependencies point downward only**: App → Infrastructure → Core. Business rules go in Core, database / PLC / Excel implementations in Infrastructure, UI in App. `LayeringTests` enforces this.
- **ViewModels never call repositories directly**; they use Core services (such as `IProductionStatisticsService`, `IShiftConfigurationService`) and open dialogs through `IDialogService`.
- **Read time from the injected `TimeProvider`**, never `DateTime.Now` directly.
- **Folder = namespace**, and the test folders mirror `src/`.
- Formatting follows [`.editorconfig`](.editorconfig) (Allman braces, 4-space indent); public types and members carry Chinese XML doc comments.
- New logic needs unit tests; schema changes must **add** a new migration, never edit a released one.
- User-facing behavior or configuration changes must update both `README.md` and `README.en.md`.

### Commits and pull requests

1. Fork the repository and create a feature branch from `main` (e.g. `fix/handshake-wrap`).
2. Follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:` and so on. Descriptions may be in Chinese or English.
3. Keep each PR focused on one thing; before submitting, make sure `dotnet build` has no warnings and `dotnet test` passes.
4. Fill in the PR template's summary and checklist, then wait for CI and maintainer review.

By submitting a contribution, you agree to license it under the project's [MIT License](LICENSE).
