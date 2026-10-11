## 变更说明 / Summary

<!-- 这个 PR 做了什么、为什么 / What does this PR change, and why? -->

关联 issue / Related issue: #

## 变更类型 / Type of change

- [ ] 缺陷修复 / Bug fix
- [ ] 新功能 / New feature
- [ ] 重构(行为不变)/ Refactor (no behavior change)
- [ ] 文档 / Documentation
- [ ] 构建、CI 或依赖 / Build, CI or dependencies

## 自检 / Checklist

- [ ] `dotnet build` 0 警告 0 错误 / builds with 0 warnings, 0 errors
- [ ] `dotnet test` 全部通过,新逻辑有对应测试 / all tests pass, and new logic has tests
- [ ] 遵守分层规则(见 docs/ARCHITECTURE.md,`LayeringTests` 通过)/ follows the layering rules (see docs/ARCHITECTURE.en.md; `LayeringTests` pass)
- [ ] 涉及用户可见行为时,已同步更新 `README.md` 与 `README.en.md` / user-facing changes are reflected in both `README.md` and `README.en.md`
- [ ] 涉及数据库结构时,已新增迁移且不修改已发布的迁移 / schema changes add a new migration instead of editing a released one
- [ ] 已在真实 PLC 或 `tools/plc-sim` 上验证(如涉及 PLC 通信)/ verified against a real PLC or `tools/plc-sim` (if PLC communication is involved)
