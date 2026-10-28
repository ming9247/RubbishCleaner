## RubbishCleaner v0.9

轻量、免安装的 Windows 流氓软件检测与残留清理工具。单文件绿色版，**无需安装 .NET**，`requireAdministrator`。

### 本次新增

**1. 特征库离线编辑（设置页可编辑）**
- 设置页新增「特征库」卡片：左侧列出**三层合并后**的全部规则并标注来源层（内置 / 程序目录 / 用户）
- 右侧表单支持新增、修改、删除**用户层**规则；下拉可选 `signer` / `path` / `name` / `namePattern` / `sha256`
- 内置层与程序目录层**只读**：列表里置灰显示「只读」，删除被拒并说明原因（防止删掉内置白名单后大面积误报）
- 与任意层已有规则重复（`type` + `value`，忽略大小写）一律**拒绝并点名冲突层**，不做静默去重
- 每次写入都把用户层 `version` 提到「三层合并版本 + 1」，保证手工编辑不会被下一次在线更新冲掉
- `.tmp` → 校验 → `File.Move(overwrite:true)` 两阶段原子写，失败不留半个特征库
- 支持「导入 JSON / 导出用户层 / 导出合并表」；合并表再导入只补用户层缺的规则，不会污染层结构

**2. 代码签名链路（自签名，流程已打通）**
- `scripts/make-signing-cert.ps1` 生成自签名代码签名证书（RSA 3072 / SHA256 / EKU 代码签名）
- `scripts/sign.ps1` 用 `Set-AuthenticodeSignature` + digicert RFC3161 时间戳签名（不依赖 Windows SDK 的 `signtool`）
- `scripts/publish.ps1 -Sign` 发布时自动签名，并在产物旁写 `signature-info.json` 便于发布者回读核对
- 私钥（`certs/`、`*.pfx`、`*.cer`、`*.p12`）全部 gitignore

> 说明：自签名证书的根不受信，`Get-AuthenticodeSignature` 返回 `UnknownError` 属**预期**，SmartScreen 仍会提示。
> 本次签名解决的是「产物完整性可校验」，发布者身份可信需真实 OV/EV 或 Azure Trusted Signing（见技术设计文档 §11.3）。

**3. 19 条已确认流氓软件黑名单（特征库 version 3）**
- 特征库 `version 2 → 3`：白名单 19 条不变，黑名单 7 → **26** 条，新增 19 条 `signer` 规则
- 判定口径：**多源交叉确认 ≥ 2 个独立来源** + 明确厂商/产品 + 具体行为 + 可落地成规则
- 只收签名者规则，一条名字匹配都没收（本机实测名字匹配误报率约 93%，且文件名零成本可改；签名主体改不了）
- 11 个候选被主动剔除：单来源（苏州星辰、Riyue、奥格威、嗨格式、百胜通、格式工厂）、与主流产品共用签名主体（搜狗/腾讯/百度/金山/爱奇艺/迅雷/优酷/酷狗/酷我/瑞星/虎牙）、仅「证书已过期」（乐视、PPTV）
- 4 条保留但标注误报风险（暴风集团、快压、风行、软媒），后续可按需降级为更精确的 `name` 规则
- 调研全过程见 `work-logs/rogue-research.md`，候选清单见 `work-logs/blacklist-candidates.json`

### 测试

`670 passed / 0 failed`（Core 486 + App 184；v0.8 为 632，新增 38 项集中在 `TrustTableEditorTests`），`dotnet build` 0 警告 0 错误。

### 产物

| 文件 | 说明 |
|---|---|
| `RubbishCleaner.exe` | 自包含单文件，约 67.7 MB，已带时间戳签名（自签名） |
| `signature-info.json` | 本次签名的回读信息（Subject / 指纹 / 有效期 / 时间戳） |

首次运行会弹 UAC（需要管理员权限读取全盘启动项与注册表），**不做任何静默删除**：
文件残留只移入回收站，注册表改动先备份成可双击的 `.reg`。

### 完整变更

`docs/progress/进度文档.md` 的 v0.9 小节；技术决策见 `docs/technical/技术设计文档.md` §5.2.5 与 ADR-062～065。