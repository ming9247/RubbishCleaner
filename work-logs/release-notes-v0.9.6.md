## RubbishCleaner v0.9.6

修一个真机反馈：**卸载残留留在 `C:\Users\<你>\AppData` 下，工具没扫到。**

### 四个原因（叠加）

| 原因 | 说明 |
|---|---|
| 没有 `InstallLocation` 就整条跳过 | 很多按用户安装的软件（Electron/Squirrel 等）不写 `InstallLocation`，卸载前连快照都存不下来，卸载后自然什么都不扫 |
| 扫描器自己也早退 | 同一条件下连"按软件名找目录"都不做 |
| 只看第一层 | `%LocalAppData%\Programs\<App>`、`%AppData%\<厂商>\<产品>`、`%ProgramData%\<厂商>\<产品>` 这些两层形态整片漏掉 |
| 说明与实现不符 | 界面上写"没有任何条目认领该目录"，但代码其实没查 |

### 修好之后

1. 没有安装目录也存一份**只有名字的快照**，卸载后照样按软件名去 `%AppData%` / `%LocalAppData%` / `%LocalAppData%\LocalLow` / `%ProgramData%` 找同名目录；
2. 上述几个"厂商 + 产品"两层的根**多看一层**；其余根（Program Files、用户主目录）仍只看一层——免得把 Documents/Desktop 翻个底朝天；
3. 每个根最多看 4000 个子目录、不进目录联接、深挖时跳过 `Temp`/`Packages`/`Microsoft`/`Windows`，**不会把扫描拖住**；
4. 候选目录**真的**和已安装软件的 `InstallLocation` 比一遍，被认领的不报（软件又装回去了就不该提示）；
5. 命中外层目录后不再把它内部的同名子目录重复报一遍。

### 顺带修了一个测试隐患（值得说一下）

跑测试时有 4 条 `OrphanCleaner` 的 `Execute_*` 测试突然红了。排查发现它们注入的只是"键是否存在"，
**备份却走真实注册表**——也就是说这 4 条测试一直在偷偷依赖"本机恰好装着某个产品"。
你今天用本工具批量卸载了软件，那个产品的 Uninstall 键没了，测试就红了。
现在备份器也改成可注入，测试完全不碰真实注册表。

### 测试

新增 8 项：无安装位置仍能找到数据目录、两层厂商目录、`MaxDepth=1` 不深挖、外层命中不重复报内层、
被认领目录不报、协调者无安装位置也留快照等。总计 **711 passed / 0 failed**（Core 498 + App 213）。

### 产物

| 文件 | 说明 |
|---|---|
| `RubbishCleaner.exe` | 自包含单文件，**71,030,840 字节（67.7 MB）**，`ProductVersion 0.9.6+3dd965b`，已带时间戳签名 |
| `signature-info.json` | 本次签名回读信息 |

`SHA256 = AAE823158B249D77328396EA78C3B0A7AF355BFA11BD4C1491C2F5B33E1DB0ED`

> 仍用自签名开发证书（指纹 `A50D82FF8CDA43F860D584BE555CACBBFDB2D5C6`），SmartScreen 会提示「未知发布者」。