# 安全策略 / Security Policy

[中文](#中文) | [English](#english)

---

## 中文

### 支持的版本

只有最新发布版本会获得安全修复。

| 版本 | 是否支持 |
|---|---|
| 1.1.x | ✅ |
| < 1.1 | ❌ |

### 报告漏洞

**请不要通过公开 issue、讨论区或 PR 报告安全漏洞。**

请使用 GitHub 的私密漏洞报告功能:进入仓库的 **Security** 标签页 → **Report a vulnerability**,或直接打开
<https://github.com/asckye/Production-Dashboard/security/advisories/new>。

报告中请尽量包含:

- 受影响的版本或 commit
- 漏洞类型与影响(例如绕过退出密码、篡改计数数据、读取部署路径)
- 复现步骤或概念验证
- 已知的缓解措施

维护者会在 7 天内确认收到,并在修复发布前与你保持沟通。修复发布后,如你愿意,会在安全公告中致谢。

### 部署安全提示

本项目通常部署在车间内网的工控机上,以下措施由部署方负责:

- **修改默认口令**:`Display:ExitPassword` 与 `Display:LeaderPassword` 默认值仅供开发,生产环境请用环境变量 `Display__ExitPassword`、`Display__LeaderPassword` 设置强口令,不要写进配置文件提交到版本库。
- **隔离 PLC 网络**:S7 协议本身没有认证和加密,请把 PLC 与上位机放在隔离的控制网段,不要暴露到办公网或互联网。
- **保护数据目录**:`Data/` 下的 SQLite 数据库、备份和 Excel 导出包含生产数据,请限制工控机的文件访问权限。

---

## English

### Supported versions

Only the latest release receives security fixes.

| Version | Supported |
|---|---|
| 1.1.x | ✅ |
| < 1.1 | ❌ |

### Reporting a vulnerability

**Please do not report security vulnerabilities through public issues, discussions or pull requests.**

Use GitHub's private vulnerability reporting instead: open the repository's **Security** tab → **Report a vulnerability**, or go directly to
<https://github.com/asckye/Production-Dashboard/security/advisories/new>.

Please include as much of the following as you can:

- Affected version or commit
- Type of issue and its impact (e.g. bypassing the exit password, tampering with production counts, exposing deployment paths)
- Steps to reproduce or a proof of concept
- Any known mitigations

The maintainer will acknowledge your report within 7 days and keep you updated until a fix is released. With your permission, you will be credited in the security advisory.

### Deployment hardening

This project usually runs on an industrial PC inside a plant network. The deploying party is responsible for:

- **Changing the default passwords**: the defaults for `Display:ExitPassword` and `Display:LeaderPassword` are for development only. In production, set strong passwords through the `Display__ExitPassword` and `Display__LeaderPassword` environment variables rather than committing them to a config file.
- **Isolating the PLC network**: the S7 protocol has no authentication or encryption. Keep the PLC and the dashboard PC on an isolated control network, never exposed to the office network or the internet.
- **Protecting the data folder**: the SQLite database, backups and Excel exports under `Data/` contain production data, so restrict file access on the industrial PC.
