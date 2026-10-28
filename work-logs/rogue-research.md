# 流氓软件黑名单候选调研（阶段3）

- 调研人：rogue-research（子代理）
- 日期：2026-10-27
- 任务：task-2「GitHub 检索已确定流氓软件，产出黑名单候选」
- 产出：本文件 + `work-logs/blacklist-candidates.json`
- 约束：只读 `src/`、`tests/`、`scripts/`、`docs/`；不修改 `src/RubbishCleaner.App/Resources/trust-table.json`；不运行 `dotnet build/test`（另一成员正在构建）

---

## 1. 调研目标与判定口径

RubbishCleaner 的信任表分白名单/黑名单两类，规则类型支持 `signer`、`path`、`name`、`namePattern`、`sha256`
（`src/RubbishCleaner.Core/Trust/TrustMatcher.cs`，匹配语义见该文件 108–124 行：
`Signer` 走 `SignerSubject.Contains(value, OrdinalIgnoreCase)`；`Path` 走前缀匹配且值必须以分隔符结尾；
`Name` 对 `Path.GetFileNameWithoutExtension` 精确匹配；`NamePattern` 把通配符编译成正则并对
**进程名**与**完整文件名**各测一次；黑名单先于白名单判定，见 ADR-001）。

本次调研只收「**已经完全确定**」的流氓软件（PUP），判定必须同时满足：

1. **多源交叉确认**：至少 2 个彼此独立的开源来源指向同一法定实体或同一产品；
2. **明确的厂商 + 产品名**：不是"名字像"，而是能落到具体公司或具体产品；
3. **具体行为描述**：捆绑安装、静默推广、改主页、伪装系统进程、卸载残留等之一；
4. **可落地成规则**：优先 `signer`（法定实体名），因为签名主体是软件作者无法随机改写的强标识。

### 1.1 明确排除的判定方式

项目自身已有实测结论，**按名字猜残留不可用**：

- `README.md:93`：「不用名字猜 | 实测本机 306 个顶层目录里只有 21 个名字能对上已装软件，按名字判残留误报率约 93%（NVIDIA Corporation、Code、Tencent 这类公共/厂商目录全会被误判）」
- `README.md:118–119`：「名字匹配只能用来证明"这个目录有主"（避免误报），不能用来证明"它是残留"。方向反了，误报率立刻上到 90% 以上。」
- `docs/technical/技术设计文档.md:1345`（ADR-030）记录了同一结论。

因此本文件里**任何仅凭目录名/文件名相似**得出的条目一律不纳入黑名单；SoftCnKiller 的
`folder.txt`（1130 个目录名）只作为**旁证**，不作为判定依据。

同时，ADR-008（追加合并、绝不覆盖已有条目）要求候选是**可追加**的增量，
所以下面全部是新增规则，不与现有 7 条通配符冲突。

---

## 2. 来源清单

| # | 来源 | 关键文件 / URL | 原文证据摘要 | 为什么可信 |
|---|------|----------------|--------------|-----------|
| S1 | **the1812/Malware-Patch**（软件屏蔽器） | `https://github.com/the1812/Malware-Patch`；`src/MalwarePatch/Certificates/*.cer`（238 个）、`Assets/certificate-map.json`（30 个厂商键）、`Assets/display-names.json` | README：「阻止指定软件的管理员授权，无需后台运行」，按**数字签名**拉黑（`--disallow=file`）。每张 `.cer` 是该产品真实 Authenticode 代码签名证书，可解析出 Subject CN 法定实体名，且同一产品有多期证书（如 `driveTheLife` 有 2019.6 / 2020.06 / 2023.08 / 2024.10） | 仓库把"被屏蔽厂商"与"真实签名证书"一一绑定，等于维护者已完成**厂商级**人工判定；证书里的 CN 是可程序化比对的硬证据，不是名字猜测 |
| S2 | **sharoue/chinawareblock**（国产流氓、娱乐软件屏蔽工具） | `https://github.com/sharoue/chinawareblock`；`一键屏蔽软件/不受欢迎/`、`一键屏蔽软件/全家桶及浏览器/`、`一键屏蔽软件/不好用/`、`过期证书/` 等分类目录，共 164 个路径 | 文件名即 `<厂商>-<产品>-sha{1\|256}-<yymmdd>.cer`，例如 `上海广乐-快压-sha256-191002.cer`、`上海展盟-全系列-sha256-200205.cer`、`重橙-flash中国特供-sha256-210319.cer`、`软媒-魔方-sha256-201007.cer` | 目录分类本身就是维护者的人工判定（`不受欢迎/` vs `不好用/` vs `工作无关软件/`），文件名给出**厂商+产品+日期**三元组，满足"明确厂商产品 + 具体行为"口径 |
| S3 | **SiHaiYiYeQiu/SoftCnKiller**（流氓软件黑名单） | `https://github.com/SiHaiYiYeQiu/SoftCnKiller`，镜像 `https://github.com/qundao/mirror-softcnkiller`（上游 master 树已 404，取镜像） | README：「流氓软件目录、数字签名黑名单」。`sign.txt` 396 行公司名黑名单；`folder.txt` 1130 个 `\FolderName\`；`Descrip/关于folder说明.txt` 27,306 字给出目录→中文产品名映射（如 `\MasterPDF\` 迅读PDF大师、`\connectr0\` 迅读广告组件、`\6789GameBox\` 6789游戏盒子、`\7654Browser\` 7654浏览器） | 独立于 S1/S2 的第三方维护的**数字签名黑名单**，恰好可与 S1 的证书 CN 做实体级对齐；README 说明因内容敏感迁移至 CSDN、此镜像同步更新，属可接受的独立来源 |
| S4 | 媒体/社区佐证（行为证据） | [快压传播病毒强行装全家桶](https://g.pconline.com.cn/x/967/11463545.html)、[二三四五的黑色吸金术](https://finance.ce.cn/home/jrzq/dc/201908/13/t20190813_32899340.shtml)、[删不掉的流氓软件2345](https://c.m.163.com/news/a/JB3O59JB0550W16F.html)、[2345 怎么卸载干净](https://m.tech.china.com/lieqi/20261014/10141965220.html)、[流氓/垃圾软件避坑指南](https://docs.youliangji.com/docs/statement)、[SoftCnKiller 两年亲测](https://m.blog.csdn.net/linan777/article/details/149595858) | 给出具体行为：快压安装包携带病毒并强装全家桶；2345 系捆绑安装、改主页、卸载残留、推广放贷 | 只用于**行为描述**佐证，不单独作为拉黑依据；所有被引用的产品在 S1/S2/S3 中另有实体级证据 |

> 注：搜索结果里出现过 `github.es`、`github.locker`、`github.me`、`www.togithub.com` 等镜像域名，
> 一律不作为证据引用，只用上面的 github.com 规范地址。

### 2.1 一手证据获取方式（可复现）

大文件走 `raw.githubusercontent.com` 会 30s 超时（`sign.txt` 16,450 B、`folder.txt` 32 KB、
`Descrip/关于folder说明.txt` 32 KB 均失败），改用 GitHub Blob API：

```
GET https://api.github.com/repos/<owner>/<repo>/git/blobs/<sha>
→ { content: "<base64>" }
```

SoftCnKiller 的数据文件是 **UTF-16LE + BOM**，必须按 `utf16le` 解码再剥 BOM，否则得到
`\x00` 交错的乱码：

```js
Buffer.from(j.content, "base64").toString("utf16le").replace(/^\uFEFF/, "")
```

证书 `.cer` 为 DER，Subject CN 直接用 `06 03 55 04 03` 的 OID 定位后按 UTF-8 / BMPString 解码即可。
本次从 S1、S2 共解析出 30+ 个签名主体 CN（见下表"证书 Subject CN"列）。

---

## 3. 交叉确认矩阵（三源对齐）

S1 证书 CN ∩ S3 `sign.txt` ∩ S2 目录分类 的实体级交集（★ 为最终采纳）：

| 证书 Subject CN（S1 解析） | S2 标注 | S3 sign.txt | 结论 |
|---|---|---|---|
| 上海广乐网络科技有限公司 | 全家桶及浏览器/上海广乐-快压 | 上海广乐网络科技 | ★ 三源 |
| 上海展盟网络科技有限公司 | 全家桶及浏览器/上海展盟-全系列 | 上海展盟网络科技 | ★ 三源 |
| Shanghai Oriental Webcasting Co. Ltd. | 不受欢迎/东方输入法 | Shanghai Oriental Webcasting | ★ 三源 |
| Chongqing Zhongcheng Network Technology Co. Ltd. | 全家桶及浏览器/重橙-flash中国特供 | 深圳市重诚远顺科技（关联实体，见 §4.12） | ★ 双源 + 产品级 |
| Shanghai 2345 Mobile Technology Co., Ltd. | 全家桶及浏览器/2345-浏览器、2345-看图 | —（S3 未列） | ★ S1+S2+媒体 |
| Shenzhen Drive The Life Technology Co.,Ltd. | 全家桶及浏览器/160-驱动人生 | Beijing Yundongshidai Network Technology（同产品族另一主体） | ★ 双源 + 产品级 |
| Qingdao Ruanmei Network Technology Co.,Ltd. | 不受欢迎/软媒-魔方 | — | ★ 双源 |
| Chengdu Aishang Office Technology Co., Ltd. | — | Chengdu Aishang Office Technology；folder.txt `\MasterPDF\` 迅读PDF大师 | ★ 双源 + 目录映射 |
| Anhui Shabake Network Technology Co., Ltd. | — | Anhui Shabake Network Technology | ★ 双源 |
| Guangdong Fengqi Technology Co., Ltd. | — | Guangdong Fengqi Technology | ★ 双源 |
| Tuling Times Information Technology (Beijing) Co., Ltd. | — | Tuling Times Information Technology | ★ 双源 |
| 合肥听风雨网络科技有限公司 | — | 合肥听风雨网络科技 | ★ 双源 |
| Hefei Yunbiao Information Technology Co., Ltd. | — | Hefei Yunbiao / 合肥云表信息科技 | ★ 双源 |
| Shenzhen Chongcheng Yuanshun Technology Co. Ltd | — | 深圳市重诚远顺科技 | ★ 双源 |
| Shanghai Shaji Network Technology Co., Ltd | — | 上海永楚网络科技 | ★ 双源 |
| 风尚云起文化传媒（北京）有限公司 | — | Fashion cloud culture media / 风尚云起文化传媒；folder.txt `\6789GameBox\` | ★ 双源 |
| Beijing Funshion Online Technologies Ltd. | — | Beijing Funshion Online；folder.txt `\FunAcce\`/`\Avatder\` 风行视频加速器 | ★ 双源 |
| 暴风集团股份有限公司 | 过期证书/暴风影音 | 暴风集团；folder.txt `\StormPlayer\` 暴风影音 | ★ 三源 |
| Guangxi Yunao Network Technology Co., Ltd. | — | Guangxi Yunao Network Technology | ★ 双源 |
| Suzhou Xingchen Network Technology Co., Ltd. | — | — | 单源，未纳入 |
| Riyue Tongxing Information Technology (Beijing) Co., Ltd. | — | — | 单源，未纳入 |
| 北京奥格威网络科技有限公司 | — | — | 单源，未纳入 |
| 苏州开心盒子软件有限公司（嗨格式） | — | — | 单源，未纳入 |
| Xiamen Baishengtong Software Technology Co.,Ltd. | — | — | 单源，未纳入 |
| Geshi Inc.（格式工厂） | — | — | 单源 + 主流工具，未纳入 |
| LE SHI INTERNET INFORMATION & TECHNOLOGY | 过期证书/乐视 | — | 仅"过期证书"分类，未纳入 |
| 上海聚力传媒技术有限公司（PPTV） | 过期证书/PPTV | — | 仅"过期证书"分类，未纳入 |
| Beijing Sogou / 腾讯 / 百度 / 金山 / 爱奇艺 | 全家桶及浏览器 等 | 部分有 | **主动排除**：签名主体与主流正当产品（搜狗输入法、微信/QQ、百度网盘、WPS、爱奇艺客户端）共用，`signer` 规则会误伤，违反"宁可少而准" |

---

## 4. 逐条候选与误报风险分析

共 **19 条**，全部为 `signer` 类型。逐条评估"该签名主体是否可能签过正常软件"。

### 4.1 上海广乐网络科技有限公司（快压 KuaiZip）
- 证据：S1 `kuaizip.cer` / `kuaizip 256.cer`；S2 `全家桶及浏览器/上海广乐-快压-sha1-191002.cer`、`sha256-191002.cer`；S3 `上海广乐网络科技`；S4 快压安装包携带病毒、强装全家桶。
- 行为：捆绑安装、静默推装全家桶、安装包分发恶意组件。
- **误报风险：极低**。该公司只为快压系列签名，快压本身即争议产品；无正常系统组件使用此签名。

### 4.2 上海展盟网络科技有限公司（7654浏览器 / 小黑记事本 / 7654压缩）
- 证据：S1 `7654 note.cer`（2019.10 / 2021.10 两期）解析出该 CN；S2 `全家桶及浏览器/上海展盟-全系列-*`（190128 / 200205 四张）；S3 `上海展盟网络科技`；S3 folder.txt `\7654Browser\` 7654浏览器、`\Heinote\` 小黑记事本。
- 行为：捆绑安装、浏览器主页劫持、弹窗推广。
- **误报风险：极低**。产品线全是导航/浏览器/记事本类推广件。

### 4.3 Shanghai Oriental Webcasting Co. Ltd.（东方输入法 / 头条视频）
- 证据：S1 `donfang toutiao.cer`（2020.1）解析出该 CN，display-names 中文名「头条视频」；S2 `不受欢迎/东方输入法-sha1-191113.cer`、`sha256-191113.cer`；S3 `Shanghai Oriental Webcasting`。
- 行为：捆绑安装、弹窗推广、伪装成输入法类工具。
- **误报风险：低**。三源一致且被 S2 归入「不受欢迎」而非「工作无关」。

### 4.4 Anhui Shabake Network Technology Co., Ltd.（沙巴克系）
- 证据：S1 `shabake 256.2021.10.cer`；S3 `Anhui Shabake Network Technology`。
- 行为：P2P 下载器/广告推广组件静默安装（同族 `riyue (at)` 在 S1 display-names 的英文名直接写作 "P2P Adware"）。
- **误报风险：极低**。该主体无任何正常软件产品线。

### 4.5 Guangdong Fengqi Technology Co., Ltd.（风启）
- 证据：S1 `fengqi (at) 2022.01.cer`；S3 `Guangdong Fengqi Technology`。
- 行为：与 4.4 同族的推广组件，捆绑安装。
- **误报风险：极低**。

### 4.6 Tuling Times Information Technology (Beijing) Co., Ltd.（图灵时代）
- 证据：S1 `tuling times 256.2022.04.cer`；S3 `Tuling Times Information Technology`。
- 行为：静默推广组件安装。
- **误报风险：极低**。

### 4.7 合肥听风雨网络科技有限公司
- 证据：S1 `tingfengyu (at) 256.2022.04.cer`；S3 `合肥听风雨网络科技`。
- 行为：风行系推广组件，捆绑安装。
- **误报风险：极低**。

### 4.8 Hefei Yunbiao Information Technology Co., Ltd.（云表）
- 证据：S1 `yunbiao.cer`、`yunbiao 256.2022.04.cer`；S3 `Hefei Yunbiao` 与 `合肥云表信息科技` 两条。
- 行为：捆绑安装与推广弹窗。
- **误报风险：低**。产品为低代码/表格类工具，但公司仅此产品线且被两家黑名单收录。

### 4.9 Chengdu Aishang Office Technology Co., Ltd.（迅读 PDF 大师）
- 证据：S1 `xundu 256.2022.04.cer`、`xundu 256.2026.05.cer`（两期，最新 2026.05 仍在续签）；S3 `Chengdu Aishang Office Technology`；S3 folder.txt `\MasterPDF\` 迅读PDF大师、`\MasterPDFData\` 迅读PDF大师广告目录、`\connectr0\` 迅读广告组件。
- 行为：捆绑安装、在用户目录写入独立的「广告组件」目录并常驻。
- **误报风险：低**。S3 直接把其目录标注为"广告组件/广告目录"。

### 4.10 Chongqing Zhongcheng Network Technology Co. Ltd.（重橙「国内版Flash」）
- 证据：S1 `zhongcheng (flash cn) 256.2022.01.cer`，display-names 中文「国内版Flash」/英文 "FlashCN"；S2 `全家桶及浏览器/重橙-flash中国特供-sha1/sha256-210319.cer`。
- 行为：冒充 Adobe Flash 静默安装、弹广告、常驻更新服务。
- **误报风险：极低**。该公司唯一业务即"中国特供 Flash"，Adobe 正版签名为 Adobe Inc.，不会命中。

### 4.11 Shenzhen Chongcheng Yuanshun Technology Co. Ltd（起点 Qidian）
- 证据：S1 `qidian 256.2023.02.cer` 解析出该 CN，display-names 中文「起点」；S3 `深圳市重诚远顺科技`。
- 行为：小说站推广客户端，捆绑安装与弹窗。
- **误报风险：低**。与 4.10 为不同法人（重橙/重诚远顺），规则值用完整法人名，两者互不误伤。

### 4.12 Shanghai 2345 Mobile Technology Co., Ltd.（2345 浏览器 / 2345 看图王）
- 证据：S1 `2345 browser.cer` 及 2020.06/2020.10/2023.08 多期；S2 `全家桶及浏览器/2345-浏览器-sha1/sha256-{170903,190623,200508}.cer`、`2345-看图-*`；S4 多篇调查报道（捆绑安装、改主页、卸载残留、推广放贷）。
- 行为：捆绑安装、主页劫持、卸载残留。
- **误报风险：低**。2345 系产品均为推广导向；注意该公司另有 `2345` 通用证书，规则值用完整法人名可避免命中无关的"2345"字样。
- 备注：S3 `sign.txt` 未收录 2345，属"两源 + 媒体"而非三源，见 §6 复核项。

### 4.13 Shenzhen Drive The Life Technology Co.,Ltd.（驱动人生）
- 证据：S1 `driveTheLife.cer` 及 2019.6/2020.06/2023.08/2024.10 四期；S2 `全家桶及浏览器/160-驱动人生-sha1/sha256-{180208,190327,200425}.cer`；S3 收录同产品族的 `Beijing Yundongshidai Network Technology`（驱动人生另一签名主体）。
- 行为：捆绑安装、驱动推广弹窗、静默装全家桶。
- **误报风险：中低**。驱动类工具存在被正常使用的场景，但签名主体专用于该产品，且被两家屏蔽库独立收录，属"产品被拉黑"而非"厂商被误判"。

### 4.14 风尚云起文化传媒（北京）有限公司（6789 游戏盒子 / 6789 压缩）
- 证据：S1 `6789.cer`、`6789 256.2019.4.cer`（WoTrus 签发）；S3 `Fashion cloud culture media`、`风尚云起文化传媒`；S3 folder.txt `\6789GameBox\` 6789游戏盒子、`\6789Zip\` 6789压缩。
- 行为：游戏盒子捆绑安装、推广弹窗。
- **误报风险：极低**。文化传媒主体签游戏盒子，无正常软件线。

### 4.15 Beijing Funshion Online Technologies Ltd.（风行视频加速器）
- 证据：S1 `funshion.cer`（Thawte）；S3 `Beijing Funshion Online`；S3 folder.txt `\FunAcce\`/`\Avatder\` 风行视频加速器。
- 行为：视频加速器捆绑安装、常驻推广。
- **误报风险：中低**。风行网有正常视频业务，但 S3 明确把其加速器目录列为流氓目录，且 S1 单独为其建证书条目。

### 4.16 暴风集团股份有限公司（暴风影音）
- 证据：S1 `baofeng.cer`（thawte）及 2020.10/2022.01/2023.02 多期；S2 `过期证书/暴风影音`；S3 `暴风集团`；S3 folder.txt `\StormPlayer\` 暴风影音。
- 行为：捆绑安装、弹窗广告、公司破产后仍残留升级服务。
- **误报风险：中**。暴风影音历史上是"正常播放器"，但其签名主体专属于该产品，且公司已破产、证书已过期，被 S3 数字签名黑名单收录。**列为需复核项**（见 §6）。

### 4.17 Guangxi Yunao Network Technology Co., Ltd.（WindSoul）
- 证据：S1 `windsoul 256.2024.06.cer`；S3 `Guangxi Yunao Network Technology`。
- 行为：静默推广组件安装（2024 年仍在续签证书）。
- **误报风险：极低**。

### 4.18 Shanghai Shaji Network Technology Co., Ltd（永楚 Shaji）
- 证据：S1 `shaji 2022.04.cer`、`shaji 256.2022.04.cer`（DigiCert EV）；S3 `上海永楚网络科技`。
- 行为：捆绑推广组件。
- **误报风险：极低**。

### 4.19 Qingdao Ruanmei Network Technology Co.,Ltd.（软媒魔方）
- 证据：S1 `ruanmei.cer`、`ruanmei 256.2019.1.cer`；S2 `不受欢迎/软媒-魔方-sha1-170827.cer`、`sha256-{170827,180801,201007}.cer`（4 期）。
- 行为：捆绑安装、弹窗推广、改主页。
- **误报风险：中低**。软媒魔方是"优化工具"，会被部分用户主动安装；但 S2 将其归入「不受欢迎」而非「不好用」，判定依据明确。

---

## 5. 为什么最终只用 `signer`，不用 `name`/`namePattern`

1. **避开 93% 误报陷阱**：`name`/`namePattern` 只能证明"这个程序叫什么"，不能证明"它是流氓软件"（README.md:118–119）。名字可被任意改，签名主体不能。
2. **避开现有 7 条通配符的缺陷**：现有黑名单是 `*hao123*`、`*2345*`、`*sogou*push*`、`*.流氓*`、`svch0st*`、`scvhost*`、`lsasss*`，其中 `*2345*` 会命中任何含 2345 的正常文件名。本次全部用完整法人名做 `Contains`，精度显著提升。
3. **`Path` 规则不适合**：安装路径随版本/渠道变化（`%ProgramFiles%\KuaiZip` vs `%LOCALAPPDATA%\...`），且规则值必须以分隔符结尾，维护成本高。
4. **`sha256` 不适合**：升级即失效，19 个产品需要持续跟踪上百个哈希，不可维护。

> 副作用说明：`signer` 规则命中意味着"该厂商签名的所有程序都被判黑"。这 19 个主体的产品线均为单一推广产品（或已被两家独立屏蔽库整族拉黑），故可接受；`Drive The Life`、`暴风`、`软媒`、`风行` 四个"有正常使用场景"的产品已在 §6 标为复核项。

---

## 6. 需人工复核的条目（已纳入，但建议复核后保留/剔除）

| 候选 | 风险点 | 建议 |
|---|---|---|
| 暴风集团股份有限公司 | 暴风影音历史上是正常播放器，仅被 S3 数字签名黑名单 + S2「过期证书」收录 | 保留；若担心误伤，可改为 `name` 规则 `StormPlayer` 等精确进程名 |
| Shenzhen Drive The Life Technology Co.,Ltd. | 驱动工具存在正常使用场景 | 保留；建议在 UI 上标注"驱动人生"便于用户理解 |
| Qingdao Ruanmei Network Technology Co.,Ltd. | 软媒魔方会被主动安装 | 保留；S2 分类为「不受欢迎」而非「不好用」 |
| Beijing Funshion Online Technologies Ltd. | 风行网有正常视频业务 | 保留；仅加速器产品被拉黑，签名主体专属 |

### 6.1 有意**未纳入**的候选（单源或主流共用签名）

| 候选 | 未纳入原因 |
|---|---|
| Riyue Tongxing Information Technology (Beijing)（日月同行） | 仅 S1 单源（虽 display-names 英文直书 "P2P Adware"） |
| Suzhou Xingchen Network Technology（星辰） | 仅 S1 单源 |
| 北京奥格威网络科技有限公司（Aogewei） | 仅 S1 单源 |
| 苏州开心盒子软件有限公司（嗨格式 Higeshi） | 仅 S1 单源 |
| Xiamen Baishengtong Software Technology（百胜通） | 仅 S1 单源 |
| Geshi Inc.（格式工厂） | 仅 S1 单源，且格式工厂是被广泛使用的正常工具 |
| LE SHI（乐视）、上海聚力传媒（PPTV） | 仅 S1 + S2「过期证书」分类，S2 未归入「不受欢迎」 |
| 搜狗 / 腾讯 / 百度 / 金山 / 爱奇艺 / 迅雷 / 优酷 / 酷狗 / 酷我 / 瑞星 / 虎牙 / 淘宝 / 网易 | **主动排除**：签名主体与主流正当产品共用，`signer` 规则必然误伤正常软件；其中百度 `baidusp`/`baidu download`、360 `wallpaper`/`ludashi` 等子产品虽偏推广，但无法用签名主体与同公司正常产品区分 |

---

## 7. 交付物与合并建议

- `work-logs/blacklist-candidates.json`：`version` 建议 **3**（当前 `src/RubbishCleaner.App/Resources/trust-table.json` 为 `version: 2`，必须单调递增才能让客户端拉到更新）；`updated` = `2026-10-27`；仅含 `blacklist` 数组，19 条 `signer` 规则，**不含 `whitelist`**，不覆盖原 `trust-table.json`。
- 合并方式（交给 task-4）：按 ADR-008 **追加**到现有 `blacklist` 尾部，保留原 7 条通配符不动，然后整体替换 `version`/`updated`。
- 原子写入：沿用 `TrustTableUpdater` 的 `.tmp` + `File.Move(overwrite: true)` 方式，避免半写坏文件。
- 预期效果：19 条 `signer` 规则覆盖 19 个独立推广厂商，可与现有 7 条通配符叠加而不冲突（`SignerSubject` 与 `FileName` 是不同维度）。

---

## 8. 结论

- 最终推荐 **19 条**高置信度 `signer` 黑名单条目（区间 12–25 内，取"少而准"）。
- 全部满足"多源交叉 + 明确厂商/产品 + 具体行为"三项；仅凭名字相似的条目 **0 条**。
- 4 条存在"产品有正常使用场景"的边界情况，已标为复核项；11 个候选因单源或主流签名共用而主动放弃。
- 现有黑名单的 7 条通配符保持不变，本次为纯增量。
---

## 9. 交付物自检

- `work-logs/blacklist-candidates.json` 已校验：顶层键恰为 `version` / `updated` / `blacklist`（**无 `whitelist`**），`blacklist` 19 条，`type` 全为 `signer`，`version: 3`、`updated: "2026-10-27"`。
- 每条 `note` 均为中文并含"来源：…"，满足可追溯要求。
- 与现有 `trust-table.json` 白名单的 12 个 `signer` 值（Microsoft Corporation / Microsoft Windows / Microsoft® Windows® Operating System / Autodesk / Tencent Technology / ByteDance / NVIDIA Corporation / Intel Corporation / Realtek Semiconductor / Adobe Inc. / Mozilla Corporation / Oracle America）**无子串包含关系**，不会出现"同一签名同时命中白/黑"的冲突；且 `TrustMatcher` 黑名单优先（ADR-001），即使将来新增也以黑名单为准。
- 与现有黑名单 7 条 `namePattern` 不冲突：维度不同（`SignerSubject` vs 文件名），可叠加。
- 未修改 `src/RubbishCleaner.App/Resources/trust-table.json`，未触碰 `src/`、`tests/`、`scripts/`、`docs/`，未运行 `dotnet build/test`。
