# RubbishCleaner

> 轻量、便捷、易操作的 Windows 流氓软件检测与残留清理工具
> 当前版本：**v0.5.0**（自启动检测 + 流氓评分 + 残留启动项清理 + 软件列表与协助卸载 + 残留目录扫描与可还原清理 + 卸载后自动扫残留 + 注册表孤儿条目清理 + **系统一致性报告（v0.5，只读）** + WPF 界面 + UAC 提权）

---

## 这是什么

一款面向 Windows 普通用户的绿色工具，解决六件事：

1. **检测** —— 找出开机自启动、后台常驻、无界面、无图标的可疑软件
2. **协助卸载** —— 调用软件自带卸载程序，先备份、再确认
3. **清理启动项残留** —— 识别并清理失效启动项（v0.1.3+）
4. **清理文件残留** —— 全盘秒级索引，找出已卸载软件留下的目录，**移入回收站**而非删除（v0.3）
5. **卸载后自动扫残留** —— 卸载前自动记录快照，卸载完直接给出确定性残留清单（v0.3.1）
6. **清理注册表残留** —— 只清 MSI 孤儿 Uninstall 条目（「应用和功能」里点了报 1639 的纯噪音），先备份 `.reg` 再删（v0.4.0 引入，v0.4.1 加固）

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

# 注册表清理的真机验证（三种模式，默认全跑）
#   e2e    造孤儿 → 扫描 → 备份 → 删键 → reg import 还原 → 现场清理（不需要管理员）
#   views  32 位视图 / 三上下文实测（只读）
#   e2e32  HKLM 32 位视图真实往返（会自己请求提权；拿不到权限就跳过，不算失败）
.\scripts\e2e-registry.ps1              # 等价于 -Mode all
.\scripts\e2e-registry.ps1 -Mode views  # 只跑只读实测

# 运行主程序
#   注意：app.manifest 是 requireAdministrator，所以**不能**用 dotnet run 启动
#   （dotnet run 拉起的进程拿不到提权，会报"请求的操作需要提升"）。
#   直接启动 exe，或先提权再运行 dll：
#     .\src\RubbishCleaner.App\bin\Debug\net8.0-windows\RubbishCleaner.exe
#     dotnet .\src\RubbishCleaner.App\bin\Debug\net8.0-windows\RubbishCleaner.dll   # 需要已提权的终端

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
| 残留启动项 | 识别指向已删除文件的僵尸启动项，单独分组，一键筛出 |
| 安全清理 | 先备份为可双击还原的 .reg，二次确认后**只删值不删键**，执行前重新校验目标是否仍失效 |
| 手动指引 | 服务/计划任务类残留不代劳，给出可复制的 sc delete / schtasks 命令，并提醒服务名与文件名不一致的情况 |
| 软件列表 | 从 Uninstall 注册表聚合已安装软件（实测 520 条原始项过滤到 114 个真软件），支持按名称/发行商搜索 |
| 协助卸载 | 调用软件**自带**卸载程序，卸载前三道检查（命令存在/系统工具拦截/程序存在），不做任何强制删除 |
| 脏数据清洗 | 第三方安装器写坏的注册表字符串自动截断（实测「明日之后」条目整片乱码） |

> 表里最后三行（软件列表 / 协助卸载 / 脏数据清洗）实际是 **v0.2** 的工作，
> 因为卸载引擎与软件列表是同一批数据的两面，放在一起读更连贯；v0.2 的完整设计见技术设计文档 §12.1。

**v0.1 明确不做**：任何自动删除。只展示与解释，处置权完全交给用户（合规要求）。

---

## v0.3 新增：残留目录扫描

前两个版本回答"什么在偷跑""我装了什么"。v0.3 回答第三个问题：**我卸载过的东西还剩下什么？**

| 能力 | 说明 |
|------|------|
| 全盘秒级索引 | 管理员下用 `FSCTL_ENUM_USN_DATA` 直读 NTFS 卷：本机 C: 82.6 万条记录 / **1.5 秒**，D: 53.1 万条 / 2.2 秒 |
| 标准用户可退化运行 | 拿不到卷句柄时自动改用目录遍历（5 个根目录约 3–20 秒），结果一致，并在界面上如实显示当前用的是哪种索引 |
| 判定只信硬痕迹 | ① 目录里还留着卸载程序（`unins000.exe` / `uninst.exe` / `uninstall.exe`…）且**没有任何已安装软件认领**；② 失效启动项指向的文件没了、但**目录还在**且无人认领 |
| 不用名字猜 | 实测本机 306 个顶层目录里只有 21 个名字能对上已装软件，按名字判残留误报率约 93%（NVIDIA Corporation、Code、Tencent 这类公共/厂商目录全会被误判） |
| 七道安全闸门 | 盘根 / 层级过浅 / 系统保护路径 / 系统保留名 / 与本程序重叠 / 符号链接 / 非绝对路径，逐条拒绝并给出原因 |
| **只移入回收站** | 代码里**不存在**永久删除分支；另外提前拒绝非固定磁盘的卷，因为 `FOF_ALLOWUNDO` 在没有回收站的卷上会静默降级成永久删除 |
| 证据链可见 | 每一条残留都直接列出"凭什么这么判"（保留了哪个卸载程序、几个文件、多大、最后修改时间、哪个启动项指向它） |
| 执行前重校验 | 扫描到点确认之间目录可能已被删除或被换成别的东西，此时跳过而不是硬删 |

### 卸载后自动扫残留（v0.3.1）

通用扫描是启发式的——它不知道你卸过什么。而**你刚刚用本工具卸载了 X** 是已知事实：
软件名、装在哪、卸载前有哪些文件全都知道，于是能给出确定性的清单。

| 能力 | 说明 |
|------|------|
| 卸载前自动快照 | 记录安装目录的文件清单（只记路径/大小，不记内容哈希） |
| 卸载后定向扫描 | 三步：安装目录剩余文件 / 安装目录本身 / `%AppData%` 等常见位置同名目录 |
| 自动区分呈现 | 索引方式标为"定向扫描（卸载后对比）"，不和通用扫描的结果混在一起 |
| 不阻塞卸载 | 快照失败、扫描失败都不影响"已经卸完了"这个事实，只记日志 |

> **为什么定向扫描可以用名字匹配，通用扫描不行**：通用扫描里"不知道卸过什么"，
> 306 个顶层目录里 285 个对不上任何已装软件，按名字判残留误报率 93%。
> 而定向扫描里"刚刚卸了微信"是事实，"`%AppData%` 下还有 WeChat 目录"这条推理成立。
> 短名字（< 4 字符）仍然只允许全等，避免"微信"匹配到"微信小程序"。

---

> **为什么不用名字匹配判残留**（通用扫描）：名字匹配只能用来**证明"这个目录有主"**（避免误报），
> 不能用来证明"它是残留"。方向反了，误报率立刻上到 90% 以上。

---

## v0.4.0 新增：注册表清理（v0.4.1 加固）

文件删错能从回收站还原，注册表键删错则可能是"软件报错 / 系统组件加载失败"。
所以这一版**先把范围收窄到一种确定无害的形态**，再谈清理。

### 什么叫「孤儿 Uninstall 条目」

Windows Installer 已经不认这个产品了，但「设置 → 应用和功能」列表里还留着它的名字：

| 现象 | 实际情况 |
|------|---------|
| 列表里看得到这个软件 | Uninstall 注册表条目还在 |
| 点「卸载」 | 报错 **1639**（无效命令行参数）——MSI 认为这个产品不存在 |
| 结果 | 用户删不掉、看着碍眼，纯噪音 |

删掉这条注册表条目只是清除噪音：**不动软件文件、不动 MSI 内部结构、不影响任何还在正常工作的软件**。

### 判据：只信 Windows Installer 自己的回答

两条**同时成立**才算孤儿：

| # | 检查 | API |
|---|------|-----|
| 1 | 产品状态为「未安装」 | `MsiQueryProductStateW(guid)` 返回 ABSENT（1/2/3）或 `ERROR_UNKNOWN_PRODUCT`（从未安装过） |
| 2 | 已安装产品列表里没有它 | `MsiEnumProductsExW` 遍历的集合中**不包含**该 GUID |

任一条说"还在"就跳过。两个 API 打架时也跳过——**不确定就不报**。

产品列表要枚举三个安装上下文（`MACHINE` / `USERMANAGED` / `USERUNMANAGED`）取并集。
只用旧 `MsiEnumProductsW` 会看不到每用户安装的产品，**漏枚举的后果就是把在装的产品判成孤儿、然后误删**。
本机只读实测：旧 API 344 个，三上下文并集也是 344 个（MACHINE 338 / USERMANAGED 0 / USERUNMANAGED 6），双向差集 0。

> **刻意不用「`Classes\Installer\Products\{GUID}` 键是否存在」判孤儿。**
> 2026-10-25 真机反例（CC Switch）：`MsiQueryProductState` 返回 **5 = INSTALLED**、
> `LocalPackage` 指向的 msi 文件确实存在、`MsiEnumProducts` 也包含它，
> 但 `Classes\Installer\Products\{GUID}`、`UserData\<SID>\Products\{GUID}` **全都不存在**，
> `reg query /s /f {GUID}` 也是 0 命中。
> 按"键不存在 = 僵尸"这条草案判据上线，会把一个好好装着的软件删掉卸载入口。

同理也不看 `InstallLocation` 目录在不在（用户手动删目录很常见，但产品其实有效）、
不看 `LocalPackage` 在不在（`C:\Windows\Installer` 缓存可能被清过）。

### 七道闸门 R1–R7

| # | 闸门 | 拦的是什么 |
|---|------|-----------|
| R1 | 键名必须严格匹配 `{8-4-4-4-12}` GUID | 防止误删 `Uninstall\某软件` 这类非 GUID 键 |
| R2 | 拒绝 `Uninstall` 根键本身（**最先判**） | 删它等于清空整个已安装列表；放最前面是因为它拦的是最坏情况 |
| R3 | 必须是 `...\Uninstall\{GUID}`，只差一层 | 防止路径拼接错误删到上层 |
| R4 | **执行前重新查一次** MSI 状态，必须仍是 ABSENT | 扫描到点确认之间产品可能被重装 |
| R5 | 再确认一次不在 `MsiEnumProducts` 列表 | 两个 API 打架时不删 |
| R6 | **备份必须写入成功且文件非空**，否则不删 | 备份是底线 |
| R7 | 删除后复核键确实不存在 | 与 v0.1.3 的 `RegistryWriter` 同一约定 |

### 备份与还原

| 项目 | 说明 |
|------|------|
| 格式 | 标准 `.reg`（`Windows Registry Editor Version 5.00`），**UTF-16LE + BOM** |
| 还原方式 | **双击该 .reg 文件**即可；不装本工具也能还原 |
| 备份目录 | `%LocalAppData%\RubbishCleaner\registry-backups` |
| 文件名 | `orphan-{GUID}-{yyyyMMddHHmmss}.reg` |
| 内容完整性 | 递归导出**子键**，不只是值——删键走的是 `DeleteSubKeyTree` 整棵子树，只备份值就等于丢了子键 |

两个细节都是**为了让"还原"真的成立**，不是随意选择：

- **编码必须是 UTF-16LE + BOM**：v0.1.3 实测过，UTF-8 的 `.reg` 里中文经 `reg import` 会变成问号。
- **32 位视图必须写成物理路径 `...\WOW6432Node\...`**：`reg import` 和双击导入都**没有视图开关**。
  若把 32 位视图里打开的路径原样写成 `HKEY_LOCAL_MACHINE\SOFTWARE\...`，
  导入时只会落到 64 位视图，备份等于白做。
  （`HKCU` 相反：实测它不做 WOW64 重定向，两个视图是同一份数据，
  而 `HKCU\SOFTWARE\WOW6432Node\...` 这个路径根本不存在，硬写反而会还原到错误位置。）

> **这一版允许删键**，与 v0.1.3「只删值不删键」并不矛盾：
> 一个 `Run` 键下挂着多个软件的值，删键会误伤（ADR-019）；
> 而 `Uninstall\{GUID}` 是**一个产品一个键**，键下所有值都属于这个产品，删键是安全的。

### 界面上怎么用

残留扫描页里单独一块「注册表」，与文件残留**分开扫描、分开列出**
（两者判据完全不同，混在一起用户分不清扫的是磁盘还是注册表）。

| 状态 | 界面说什么 |
|------|-----------|
| 还没扫过 | 提示去点「扫描注册表残留」 |
| 扫过、干净 | 明确告诉你「注册表干净：已检查 N 个 MSI 条目，没有发现孤儿」 |
| 有孤儿 | 标题写「注册表残留 N 条、可清理 M 条（其余 K 条被安全闸门拦下）」 |
| 筛选后无匹配 | 单独提示「当前筛选无匹配」，而不是留一片白板 |

- 列表支持和文件残留**同一套筛选**（关键词 + 只看可清理），关键词能搜显示名、**产品码**、发行商、**完整键路径**——
  因为注册表条目的名字经常是脏数据或空的，你手上真正能拿来搜的往往就是 GUID 或键路径。
- **筛选只影响显示，不会缩小实际清理范围**：清理永远针对全部孤儿条目。
- 选中任意一行，能看到**完整键路径**、所在分支，以及被安全闸门拦下的具体原因。
- 清理按钮文案分三态：`清理 N 个注册表残留` / `无可清理项`（有条目但全被拦下）/ `无注册表残留`。

### 明确不做

| 不做 | 理由 |
|------|------|
| `HKCR` 下的任何键 | COM 组件引用，删错会让软件加载失败 |
| `HKLM\SYSTEM` | 驱动与服务，蓝屏风险 |
| `Software\{Vendor}` 厂商根目录 | 里面可能挂着多个软件，无法判断归属 |
| 孤儿 CLSID / 文件关联 | 需要解析 COM 引用计数，误报率不可接受 → **推到 v0.5** |
| 非 MSI 的 Uninstall 条目 | 没有可靠的"产品是否还存在"判据 |
| MSI 内部结构（`Installer\UserData`、`Installer\Folders`） | 该由 Windows Installer 自己维护，不该手改 |

---

## v0.5 新增：系统一致性报告（只读）

v0.5 回答第四个问题：**我的系统里有没有"半死不活"的东西？**

v0.5 是纯只读版本——**没有也不可能有任何清理按钮**。这个决定来自两份调研
（[孤儿 CLSID](docs/technical/v0.5-孤儿CLSID调研.md) /
[孤儿文件关联](docs/technical/v0.5-孤儿文件关联调研.md)）：
直觉判据（文件不存在 = 孤儿）在这两块误报率不可接受，Windows 按需注册、
活软件的升级残留、MSIX 的按需解析都会被误判。只做能判定的两块：

| 报告项 | 判据 | 真机实测 |
|---|---|---|
| 服务 ImagePath 孤儿 | 服务的可执行文件路径解析成绝对 Win32 路径、不在系统目录、文件确实不存在 | 836 个服务，跳过 792，报 1 条（Winstep Xtreme Service，运行中，路径已消失） |
| 文件与注册表状态不一致 | MSI 确认"已安装"，但 InstallLocation 目录已消失 | 113 个软件，报 5 条（CC Switch + 4 条 NVIDIA 安装器临时目录） |

为什么这两块可判定而 CLSID/文件关联不可：服务表由 SCM 维护、没有权限陷阱、
没有"按需注册"；状态不一致有 MSI API 背书（IsPresent）。
而 CLSID 没有等价于 MSI 数据库的引用计数，文件关联被 UserChoice 防篡改与 AppX 按需解析保护。

**安全设计**：

| 设计 | 说明 |
|---|---|
| `CanAutoClean` / `CanAutoFix` 在契约层写死 `false` | 不是可配置项——让危险的事在类型上不可表达 |
| 服务孤儿只给 `sc delete` 手动命令 | 复制后自己去管理员终端执行，工具不代劳（ADR-019 服务类惯例） |
| 跳过规则写进代码注释 | 空 ImagePath（内核驱动）/ svchost 宿主 / 内核形态路径 / 系统目录（Windows Update 可能回装文件）/ 相对路径 |
| App 层有反射测试锁死铁律 | ViewModel 里出现 Clean / Fix / Delete / Repair 命名即测试失败 |

与 v0.4 的边界：MSI 状态 ABSENT 的条目归 v0.4 的孤儿清理；v0.5 只报告 MSI 确认"已安装"但文件没了的。

---

## 项目结构

```
Rubbish-cleaner/
├── src/
│   ├── RubbishCleaner.App/        # WPF 主程序（UI，三个页签）
│   ├── RubbishCleaner.Core/       # 核心引擎（检测 / 评分 / 确认表 / 清理）
│   │   ├── RegistryCleanup/       # 注册表孤儿 Uninstall 条目（v0.4，详见文末目录说明）
│   │   └── Integrity/             # 系统一致性报告（v0.5，只读：服务孤儿 + 状态不一致）
│   └── RubbishCleaner.Common/     # Win32 / 注册表 / 签名 / 日志 / 文件索引
├── tests/
│   ├── RubbishCleaner.Core.Tests/ # Core 逻辑与安全闸门（337 项）
│   └── RubbishCleaner.App.Tests/  # 排序 / 导出 / ViewModel / 注册表交互（130 项）
├── tools/
│   ├── RubbishCleaner.Smoke/      # 无界面冒烟验证工具
│   └── RubbishCleaner.RegE2E/     # 注册表清理真机探针（e2e 全链路 / views 视图实测 / e2e32 32 位往返）
├── docs/
│   ├── planning/                  # 产品文档
│   ├── technical/                 # 技术设计文档（含 ADR）
│   ├── environment/               # 环境清单
│   └── progress/                  # 进度文档与界面实拍
├── scripts/                       # build / test / publish / probe-env / e2e-registry
└── work-logs/                     # 按日期的开发日志
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

## 目录说明（v0.3 新增部分）

```
src/RubbishCleaner.Common/Mft/VolumeFileIndex.cs      # 索引抽象 + 主/备自动切换
src/RubbishCleaner.Common/Mft/UsnFileIndex.cs         # USN 直读卷（管理员，秒级全盘）
src/RubbishCleaner.Common/Mft/FallbackVolumeFileIndex.cs # 目录遍历回退（标准用户）
src/RubbishCleaner.Common/Win32/RecycleBin.cs         # 移入回收站（唯一的"删除"方式）
src/RubbishCleaner.Core/Leftover/LeftoverScanner.cs   # 残留判定
src/RubbishCleaner.Core/Leftover/LeftoverSafetyGuard.cs # 七道安全闸门
src/RubbishCleaner.Core/Leftover/LeftoverCleaner.cs   # 计划 + 执行（无永久删除分支）
src/RubbishCleaner.Core/Residue/                  # 卸载与残留联动（v0.3.1）
│   ├── UninstallSnapshotStore.cs                 # 卸载前快照采集 + JSON 持久化
│   └── UninstallResidueScanner.cs                # 三步定向扫描
src/RubbishCleaner.App/ViewModels/UninstallResidueCoordinator.cs # 联动编排
```

## 目录说明（v0.4 新增部分）

```
src/RubbishCleaner.Core/RegistryCleanup/
├── MsiInterop.cs                  # msi.dll 最小封装：产品状态 / 三上下文产品枚举 / 属性读取
├── OrphanUninstallModels.cs       # 孤儿条目模型 + 扫描契约（IOrphanUninstallScanner）
├── OrphanUninstallScanner.cs      # 只读扫描：MSI 双确认判孤儿，绝不动注册表
├── OrphanCleaner.cs               # 清理计划 + 执行：复核 → 备份 → 删键 → 再复核
└── RegistryKeyBackupWriter.cs     # 导出键及其子键为 .reg（UTF-16LE+BOM，32 位视图写 WOW6432Node）
```

---

## 技术栈

- C# / .NET 8 / WPF
- 零第三方运行时依赖（仅测试用 xUnit）
- P/Invoke：`user32`、`kernel32`、`wintrust`、`msi.dll`、Shell COM

---

## 重要提示

- 本工具**不会静默删除任何文件或注册表项**。
- 清理类操作全部**可还原**：注册表改动先备份为可双击还原的 `.reg`，文件改动一律**移入回收站**。
- 检测结果是**建议性**的，请结合证据自行判断。
- 评分与残留判定都基于启发式规则，可能存在误报；命中白名单的程序会被压制为"正常"。
- 残留扫描默认不扫系统目录，也不扫本工具自己的数据目录（那里是清理备份）。
- 注册表清理**只删 Uninstall 注册表条目**，不动任何软件文件，也不动 `HKCR` / `HKLM\SYSTEM` / 厂商根目录；
  删之前一律先导出 `.reg`，还原只需双击该文件。