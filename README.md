# RubbishCleaner

> 轻量、便捷、易操作的 Windows 流氓软件检测与残留清理工具
> 当前版本：**v0.1.1**（自启动检测 + 流氓评分 + 绝对确认表 + WPF 界面 + UAC 提权 + 图标/排序/报告导出）

---

## 这是什么

一款面向 Windows 普通用户的绿色工具，解决三件事：

1. **检测** —— 找出开机自启动、后台常驻、无界面、无图标的可疑软件
2. **协助卸载** —— （v0.2）
3. **清理残留** —— （v0.3+）

设计原则：**越轻量、越便捷、越易操作越好**。用完即走，不常驻后台，不弹广告。

---

## 快速开始

```powershell
# 构建
.\scripts\build.ps1

# 单元测试
.\scripts\test.ps1

# 无界面冒烟扫描（不需要管理员，只读）
dotnet run --project tools/RubbishCleaner.Smoke

# 运行主程序（会请求管理员权限）
dotnet run --project src/RubbishCleaner.App

# 发布单文件绿色版
.\scripts\publish.ps1
```

---

## v0.1 已实现

| 能力 | 说明 |
|------|------|
| 7 个自启动数据源 | HKCU/HKLM/WOW64 Run + 用户/公共启动文件夹 + Windows 服务 + 计划任务 |
| 流氓评分模型 | 7 条加权规则，0–100 分，三级呈现（正常/疑似/高风险） |
| 绝对确认表 | 白名单 + 黑名单，内置兜底 + 可编辑 JSON |
| 进程可见性判定 | 一次 `EnumWindows` 全量枚举，判定"有窗口 / 有任务栏按钮" |
| 签名校验 | 内嵌签名 + **目录签名（catalog）**，避免误判系统组件 |
| 证据链 | 每条结论都能展开看到"为什么这样判" |
| 降级运行 | 权限不足时标记降级并提示，不崩溃、不假装完整 |
| 绿色单文件 | 自包含发布，约 67 MB，免装 .NET |
| 图标列 | 从 exe 提取真实图标，一眼识别程序 |
| 表头排序 | 等级 / 名称 / 启动方式 / 发行商 / 分数 / 特征库，6 列可点击排序 |
| 报告导出 | CSV（Excel 可筛，带 BOM 不乱码）+ HTML（可直接查看分享） |

**v0.1 明确不做**：任何自动删除。只展示与解释，处置权完全交给用户（合规要求）。

---

## 项目结构

```
Rubbish-cleaner/
├── src/
│   ├── RubbishCleaner.App/        # WPF 主程序（UI）
│   ├── RubbishCleaner.Core/       # 核心引擎（检测 / 评分 / 确认表）
│   └── RubbishCleaner.Common/     # Win32 / 注册表 / 签名 / 日志
├── tests/RubbishCleaner.Core.Tests/
├── tools/RubbishCleaner.Smoke/    # 无界面冒烟验证工具
├── docs/
│   ├── planning/                  # 产品文档
│   ├── technical/                 # 技术设计文档
│   ├── environment/               # 环境清单
│   └── progress/                  # 进度与开发日志
├── scripts/                       # build / test / publish / probe-env
└── work-logs/
```

---

## 文档

| 文档 | 内容 |
|------|------|
| [产品文档](docs/planning/Rubbish-cleaner-项目文档.md) | 背景、目标、功能设计、路线图 |
| [技术设计文档](docs/technical/技术设计文档.md) | 架构、类设计、算法、决策记录（ADR） |
| [环境清单](docs/environment/环境清单.md) | 本机实测环境与验证结果 |
| [进度文档](docs/progress/进度文档.md) | 当前进度、已完成、待办、已知问题 |

---

## 技术栈

- C# / .NET 8 / WPF
- 零第三方运行时依赖（仅测试用 xUnit）
- P/Invoke：`user32`、`kernel32`、`wintrust`、Shell COM

---

## 重要提示

- 本工具**只做检测与说明**，不会静默删除任何文件或注册表项。
- 检测结果是**建议性**的，请结合证据自行判断。
- 评分基于启发式规则，可能存在误报；命中白名单的程序会被压制为"正常"。
