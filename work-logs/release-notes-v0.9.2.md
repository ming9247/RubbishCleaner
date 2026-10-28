## RubbishCleaner v0.9.2

纯 UI 修复。两个**只有真机截图才暴露**的布局缺陷（ViewModel 单测全绿也发现不了）。

### 修了什么

| 现象 | 根因 | 修法 |
|---|---|---|
| 「已安装软件」一片空白，被一块白面板挡住，右侧一个很高的粉色「卸载选中的软件」框（115 个软件一条都看不到） | 该页签的 Grid **只声明了 2 行**，底部操作条却写 `Grid.Row="2"`。WPF 会把越界行**夹到最后一行的位置**（已用 `Measure/Arrange` 实测：子元素落在第 2 行顶部并占满剩余高度），那条白色 Card 于是盖住了整个列表 | 补第三行 `<RowDefinition Height="Auto" />`（工具条 / 列表 / 底部操作条） |
| 设置页滚不到底，「关于软件」整块看不见 | 设置页叠了 4 张 Auto 高度卡片，**没有滚动容器**；窗口按工作区自适应后高度不够就被裁掉 | 设置页内容套 `ScrollViewer`（`VerticalScrollBarVisibility="Auto"`） |

第一个缺陷从 **v0.2.0** 就在（`git show` 确认当时也只有 2 行定义），因为项目一直没有 UI 级测试，谁都没发现。

### 顺带补上「UI 不能被单测覆盖」的漏洞

1. **`tests/RubbishCleaner.App.Tests/XamlLayoutTests.cs`（7 项，静态结构断言）**：解析 `MainWindow.xaml`——
   任意 Grid 子元素的 `Grid.Row/Column`（含 Span）不得越界；已安装页必须有第三行；
   设置页必须在 ScrollViewer 里；四个列表类页签**不得**被 ScrollViewer 包裹（否则内层 ListBox 失去内部滚动与虚拟化）。
2. **`tools/RubbishCleaner.UiProbe`（真实 WPF 布局探针，已入 sln）**：加载真实 App 资源与 MainWindow，
   用 WPF 自己的 `Measure/Arrange` 量坐标。实测结果：

| 场景 | 结果 |
|---|---|
| 已安装页 700px | 列表 524px，底边 y=596；底部条从 y=608 起 → 不重叠 |
| 已安装页 520px / 420px | 列表 344px / 244px，底边 416 / 316，条顶 428 / 328 → 不重叠 |
| 设置页 460px | `ScrollableHeight=605px`；滚到底「关于软件」y=234..444（视口 460）完整可见 |
| 设置页 1200px | `ScrollableHeight=0`，无多余滚动条 |

自己跑：`dotnet run --project tools/RubbishCleaner.UiProbe -c Debug`（exit 0 = 通过，不需要管理员）。

### 其它

- 版本 `0.9.1 → 0.9.2`；特征库与上一版一致（`version 3`，白 19 / 黑 26）
- 测试：**683 passed / 0 failed**（Core 486 + App 197，新增 7 项）

### 产物

| 文件 | 说明 |
|---|---|
| `RubbishCleaner.exe` | 自包含单文件，**71,014,200 字节（67.7 MB）**，`ProductVersion 0.9.2+76e9e21`，已带时间戳签名 |
| `signature-info.json` | 本次签名回读信息 |

`SHA256 = E9155D6658372217DBE16207E101249E6BA7BB416844C2DB40741873353B99B5`

> 仍用自签名开发证书（指纹 `A50D82FF8CDA43F860D584BE555CACBBFDB2D5C6`），根证书不受信，SmartScreen 仍会提示「未知发布者」。