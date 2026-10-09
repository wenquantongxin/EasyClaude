# Easy Claude

个人使用的 Windows 程序：WPF 外壳、独立 mihomo 代理和独立 OpenCode 服务，只启用 Anthropic Claude。默认以日常使用和响应速度为先；TUN、出口地域及出口变化显示提示，严格启动检查和防火墙加固默认关闭。

## 启动与目录

发布程序从 app\EasyClaude.exe 启动。app 自带 .NET 10 运行时，目标为 Windows x64；无需安装 .NET。WebView2 使用系统的 Evergreen 运行时，缺失时自动获取微软完整安装程序、验证微软签名后安装。首次补齐运行组件需要网络，不属于完整离线安装包。

| 路径 | 内容 |
|---|---|
| easyclaude.root | 定位程序根目录的标记，必须保留 |
| app | 主程序和自带 .NET 10 运行时 |
| bin | 私有 mihomo、OpenCode、Geo 数据及来源/完整性清单 |
| data\profiles | 当前 Windows 用户绑定的 DPAPI 加密节点配置副本 |
| data\opencode | 独立 OpenCode 配置、凭据与会话数据库 |
| data\logs、data\webview2、data\tmp | 日志、WebView2 缓存及临时文件 |
| src、tests、tools、examples | 源码、验证工具和无凭据示例 |

bin 和 data 的目录访问权限限定为当前用户、SYSTEM 与管理员。导入配置采用“暂存 → 结构与 mihomo 校验 → 原子替换”；失败时保留当前配置，成功后重新加载服务。原 YAML 移动或删除不影响已导入配置。配置仅支持内联 proxies，不支持 proxy-providers / rule-providers 订阅。

## 日常操作

- **Claude API Key**：空白输入时“测试已保存的 Key”；输入新值后“保存新 Key 并测试”。先通过 Anthropic 免费接口验证，再保存并发送简短的“你好”模型测试（产生正常 API 用量）。保存成功和模型测试成功分别显示，测试可以取消。
- **模型测试**：优先选择可用 Haiku，保存凭据后有限等待模型初始化。确认模型不可用后最多改用默认模型一次；认证失败、限流及网络错误直接显示原因。结果包括实际模型、耗时和回退原因。
- **聊天模型列表**：仅显示 OpenCode 当前目录中已有的 Anthropic Fable、Opus 和 Sonnet 系列，保留原模型版本及推理选项。Haiku 从聊天列表隐藏；Key 测试仍可使用可用 Haiku，不额外提高测试费用。隐藏仅作用于内嵌网页目录，不修改后台模型能力或会话历史。
- **重启后的 Key 状态**：测试完成后在 data/api-key-test.json 保存结果摘要、时间、模型和耗时，绑定当前凭据，不保存 Key、回复或原始错误正文。重启显示“上次测试成功 / 未成功”，悬停可查看时间；不会自动发送付费测试请求。更换 Key 后重新测试，取消操作不覆盖旧记录。0.2.1 及更早版本没有保存历史，需要在升级后手动测试一次。
- **重启代理**：只重新启动代理服务，OpenCode 及聊天窗口继续运行。
- **重启程序**：关闭旧服务、释放实例锁，再启动新的主进程。Key、节点配置及会话历史保留。
- **节点延迟**：启动或手动重新检测时测一次，之后每 60 秒刷新。连续两次测速失败显示提示，仍持续检测并在恢复后更新；测速与代理进程退出分开显示。
- **重新检测连接**：点击连接状态下方、节点名称左侧的 WiFi 图标。蓝色表示延迟 ≤ 250 ms，琥珀色表示 251–800 ms，玫红色表示 > 800 ms；灰色表示待检测，左下角红叉表示节点探测未成功或已停止。延迟表示响应时间，不代表下载带宽或模型生成速度。
- **关闭窗口**：弹窗可选“后台运行”“退出程序”“取消”。默认后台运行，系统托盘及服务继续工作。托盘“退出程序”完全停止。重启和系统关机不会弹此提示。
- **更多 → 高级设置**：严格启动检查、防火墙加固与手动组件完整性检查。
- **更多 → 帮助与使用指南**：程序内的离线帮助页，介绍项目、首次配置、日常操作、状态颜色与常见问题。查看帮助时连接继续运行；“返回主界面”或 Esc 回到聊天 / 准备页面。

## 运行组件与网络

组件管理器读取源码中的 src/EasyClaude.Core/dependencies.json。目前使用 mihomo 1.19.32、OpenCode 2.0.26 和 MetaCubeX Geo 数据；发布版本号用于保持服务 API 兼容，未锁定内部代码提交 SHA。

缺少 mihomo 时通过系统代理或系统网络取得官方组件，随后使用导入节点建立临时下载代理，获取 OpenCode 和 Geo 数据。下载界面显示进度和失败原因，允许重试；临时包未通过校验不会安装。mihomo 校验 GitHub 官方摘要，Geo 数据校验官方 SHA256 清单，OpenCode 校验官方 npm 包的 SHA512 完整性信息，WebView2 校验微软签名。

已安装组件在首次建立清单、文件变化及手动检查时进行完整性验证。健康启动只检查清单、长度和时间戳；聊天请求不进行散列校验，也不等待测速。此次本机更新沿用已验证的现有 OpenCode 与 Geo 文件，保留实际来源记录；后续缺失恢复使用官方分发入口。

## 基础保护与可选加固

模型请求的代理环境只指向本程序的回环代理，代理不可用时请求失败。代理与 OpenCode 端口绑定 127.0.0.1 并使用随机口令；mihomo 控制接口使用仅当前用户、SYSTEM 与管理员可访问的命名管道。生成的 YAML 删除额外监听、TUN、NTP 等入口；强制 Claude / OpenCode 域名走只包含真实节点的 LLM 组，补齐 IP 规则 no-resolve；WebView2 网络资源只允许当前本机 OpenCode 源。

默认检查不会因 TUN、出口地域偏好或出口 IP 变化阻止启动。严格检查开启后，在启动阶段等待路由和外网连通性检查，TUN 冲突可以明确选择继续。

防火墙加固仅针对本目录 bin\easyclaude-opencode.exe，阻止非回环 IPv4/IPv6 出网，允许它连接本地代理。启用、修复和移除需要 Windows 管理员许可；取消后按当前实际状态继续运行，不影响普通模式。实际规则状态显示并保存。该功能不改变其他客户端、系统代理或系统防火墙开关。防火墙不是 OpenCode 工具或插件的沙箱。

API Key 保存在独立 OpenCode 数据库中，受目录 ACL 保护；**该上游数据库包含明文凭据，并非 DPAPI 加密存储**。只有导入节点配置由本程序 DPAPI 加密。不要共享 data 或运行日志，不要在聊天中粘贴 Key。

## 新电脑使用

支持 Windows x64 的新电脑无需安装 Clash Verge 或 .NET。复制 app、bin 和 easyclaude.root，启动 app\EasyClaude.exe 后重新导入原始节点 YAML、填写 Claude Key；数据会在新电脑创建。不要直接复制旧电脑的 data 作为首次配置：加密节点副本绑定旧电脑的 Windows 用户，不能直接解密。WebView2 使用系统运行时，缺失时按提示安装；没有复制的运行组件由程序自动补齐。

## 从源码发布和验证

需要 .NET 10 SDK；发布脚本优先使用 .tools\dotnet\dotnet.exe，再查找系统 SDK。它先发布到暂存目录，成功后替换 app，并把旧程序临时保存在 .tools 中，保留 bin 和 data。

~~~powershell
pwsh -File tools\publish.ps1
.\.tools\dotnet\dotnet.exe test tests\EasyClaude.Core.Tests -c Release
.\.tools\dotnet\dotnet.exe build tools\EasyClaude.Smoke -c Release
~~~

端到端冒烟测试应使用隔离根目录（带 easyclaude.root）、私有组件副本及当前用户可解密的节点配置，避免使用生产凭据数据库。已有本地组件时传 --skip-restore，避免下载。

~~~powershell
.\.tools\dotnet\dotnet.exe tools\EasyClaude.Smoke\bin\Release\net10.0-windows\EasyClaude.Smoke.dll --root F:\LLM-models\EasyClaude\.tools\acceptance --skip-restore
~~~

WPF 无真实 Key 的界面检查可用 app\EasyClaude.exe --root 隔离目录 --ui-check 输出目录，生成 100%、150%、200% 界面 PNG 和检查结果。图标源 SVG、多尺寸 ICO 与生成脚本可提交；重新生成使用 powershell -File tools\generate-icons.ps1。

.gitignore 排除 app/bin/data、构建输出、EXE/DLL、数据库、凭据、真实 YAML 和下载包；仅允许 examples 中以 .example.yml/.example.yaml 结尾的无凭据示例以及 .github/workflows 中的 CI 配置。发布脚本构建主程序，已发布程序负责补齐运行组件。

参考：[微软 WebView2 部署](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)、[.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)、[OpenCode 安装](https://opencode.ai/v2/install)。

## 清理本机运行数据和构建垃圾

完全退出程序后，可先预览，再执行以下命令：

~~~powershell
pwsh -File tools\clean-local.ps1
pwsh -File tools\clean-local.ps1 -Execute
~~~

清理范围只在此 Easy Claude 目录内：删除构建 bin/obj、临时 SDK、测试实例、旧发布副本、日志、OpenCode 凭据/会话数据库和 WebView2 缓存。当前 app、运行组件 bin、data/settings.json 和加密节点配置保留。执行后需重新填写 Claude Key，旧会话无法在程序中恢复；下次从源码编译需要 .NET 10 SDK。用户的其他工作区、全局 SDK 与全局 NuGet 缓存不在清理范围内。

只清理构建和隔离测试文件、保留全部当前运行数据时，使用 `pwsh -File tools\clean-local.ps1 -BuildOnly` 预览，再追加 `-Execute` 执行。
