# CLAUDE.md — GostWebUI(托盘 + Web 配置的 gost 端口转发器)

> 给 Claude Code 的项目上下文。开工前请通读本文件与 `docs/` 下三份文档。

## 一句话目标

一个 **.NET 10 单进程** Windows 程序:日常缩在**系统托盘**后台运行;**双击托盘图标打开本地网页**(正式版 `http://127.0.0.1:31847`,开发构建 `38517`);在网页里增删改转发规则、启停、设置开机启动、并做**基础连接测试**。底层用 [gost](https://github.com/go-gost/gost) 做实际的 TCP 端口转发，可使用直连或兼容的 SOCKS5 / HTTP 代理出口。

典型规则:本地 `127.0.0.1:41011` → 经 `socks5://127.0.0.1:3066`(本机 SOCKS5 代理)→ `192.0.2.10:41011`。

## 架构决策(已定,勿改动方向)

- **单进程**:同一个 `WinExe` 进程里,主线程跑 WinForms 托盘消息循环(`ApplicationContext` + `NotifyIcon`),后台非阻塞启动一个 Kestrel(ASP.NET Core Minimal API)。详见 `docs/architecture.md`。
- **手动组合 + 局部 DI**:`Program.Main` 里手动 `new` 各服务(`ConfigService` / `LogFileService` / `ForwardRuleService` / `Socks5Tester` / `StartupService`)并串起来,**不使用全局 DI 容器**;仅在 `ApiServer` 内部构建 `WebApplication` 时用到框架自带的服务容器。
- **单实例**:命名 `Mutex`(`Local\GostWebUI-SingleInstance`,Debug 构建带 `-Dev` 后缀)保护;第二个实例尝试打开配置页后立即退出,不重复起托盘。
- **Web 框架**:ASP.NET Core Minimal API。csproj 用 `Microsoft.NET.Sdk` + `<FrameworkReference Include="Microsoft.AspNetCore.App" />` + `<UseWindowsForms>true</UseWindowsForms>`,即可在同一项目里同时用 WinForms 和 WebApplication。
- **前端**:**无构建**单页,纯原生 JS + `fetch`,放在 `wwwroot/index.html`,Kestrel 静态托管。不引入 npm / Vite / Vue。
- **gost / 转发实现分派**:普通转发(`Mode="gost"`,默认)仍是被拉起的**子进程**(`Core/GostProcessManager.cs`);规则可选 `Mode="mysql"`,改由**MySQL TLS 中继** `Core/MySqlRelayManager.cs` 承载,兼容「对首包做嗅探/懒连接」的中转并访问 server-first 的 MySQL。两者共用 `Core/IForwardManager.cs` 抽象,由 `ForwardRuleService` 按 `Mode` 分派;MySQL TLS 中继不依赖 gost.exe。
- **绑定地址与默认端口**:Kestrel 默认只绑 `127.0.0.1`,不对外。端口可配置,默认值集中在 `AppConfig.DefaultWebPort` 且**按构建区分**:正式(Release)`31847`、开发(Debug)`38517`(都选冷门值防冲突);Debug 构建强制用开发端口(无视 config.json),开机启动注册值名与 Mutex 名同样按构建区分,开发/正式两环境可同时运行互不干扰。旧默认端口 18011 由 `ConfigService.Load` 一次性自动迁移;更名前(MyPortForwarder)的开机启动注册值由 `StartupService.MigrateLegacyValueName` 一次性迁移。
- **序列化契约(关键)**:REST 层用 `Results.Json` 走 ASP.NET **camelCase** 默认(与前端 `listenAddress` / `proxyType` 对齐);`config.json` / `rules.json` 落盘用 `System.Text.Json` 默认 **PascalCase**(仅本进程读写)。两套各自自洽,改动 `ApiServer` / `ConfigService` 时不可打破,否则前端读到 `undefined`。
- **数据存储拆分**:`config.json` 固定 exe 同目录(启动锚点,存全局设置与存储位置);规则本体在 `RulesPath` 指向的 `rules.json`(顶层数组,默认 exe 同目录,网页「设置」可迁移,目标已存在时拒绝防覆盖,旧文件留作备份);运行日志由 `LogFileService` 按天写 `LogDirectory`(默认 `logs`)下的 `gost-yyyyMMdd.log`,文件名日期超过 `LogRetentionDays` 天自动删除(0 = 永久)。旧版内嵌在 config.json 的 `Rules` 由 `ConfigService.Load` 一次性迁移(按 Id 去重)。

## 目录结构(分层)

```
GostWebUI/
├── CLAUDE.md
├── README.md
├── GostWebUI.csproj               # WinExe / net10.0-windows / RootNamespace=PortForwarder / AssemblyName=GostWebUI
├── app.manifest
├── app.ico                        # 应用/托盘图标(多尺寸,由 make-icon.ps1 生成)
├── make-icon.ps1                  # 程序化生成 app.ico 与 wwwroot/favicon.ico(System.Drawing,可复现)
├── Properties/PublishProfiles/
│   └── FolderProfile.pubxml       # VS「发布」配置:单文件自包含 + 压缩 + 无 pdb(产物仅 GostWebUI.exe)
├── config.json                    # 全局配置(WebPort/GostPath/GostSha256/RulesPath/LogDirectory/LogRetentionDays;固定 exe 同目录)
├── rules.json                     # 规则持久化(顶层数组;位置由 RulesPath 配置,默认 exe 同目录,网页「设置」可迁移)
├── logs/                          # 运行日志目录(默认;由 LogDirectory 配置):gost-yyyyMMdd.log 按天滚动
├── Program.cs                     # 入口:Mutex 单实例 → 手动组合服务 → ApiServer.Start → 托盘消息循环 → 退出清理
├── Models/                        # 纯数据模型(命名空间 PortForwarder.Models)
│   ├── ForwardRule.cs             # 规则模型 + gost 参数生成(含 Mode:gost/mysql)
│   ├── AppConfig.cs               # 全局配置模型(WebPort/GostPath/GostSha256/RulesPath/LogDirectory/LogRetentionDays)
│   ├── ConnectionTestResult.cs    # 连接测试结果 DTO
│   ├── GostIntegrityInfo.cs       # gost.exe 完整性状态 DTO(路径/当前指纹/锁定指纹/是否一致)
│   └── LogEntry.cs                # 日志条目(Seq/RuleId/Time/Text)
├── Core/                          # 领域 / 进程逻辑(命名空间 PortForwarder.Core)
│   ├── AppVersion.cs              # 应用版本号读取(单一来源 csproj <Version>;托盘/网页/health 共用)
│   ├── IForwardManager.cs         # 转发运行时管理器抽象(gost 子进程 / MySQL 中继共用;ForwardRuleService 按 Mode 分派)
│   ├── ForwardRuleService.cs      # 多规则注册表 + 500 行环形日志缓冲 + gost 指纹锁定策略(TOFU/信任)(API/托盘服务层)
│   ├── GostProcessManager.cs      # 单条规则的 gost 子进程启停 + 日志捕获;路径绝对化 + 启动前 SHA-256 校验(持拒写句柄防 TOCTOU)
│   ├── MySqlRelayManager.cs       # 进程内 MySQL TLS 中继(Mode=mysql):SSLRequest 分段补全兼容懒连接,TLS 端到端透传,不拉 gost
│   ├── ChildProcessJob.cs         # kill-on-close Job Object(主进程死亡时内核兜底杀 gost,防孤儿进程占端口)
│   └── Socks5Tester.cs            # TCP + SOCKS5 端到端连接测试
├── Services/                      # 应用服务(命名空间 PortForwarder.Services)
│   ├── ConfigService.cs           # config.json + rules.json 读写(Load/Save/SaveRules;旧文件名/旧端口/内嵌规则迁移;规则文件迁移防覆盖)
│   ├── LogFileService.cs          # 日志文件归档:按天滚动 gost-yyyyMMdd.log + 按保留天数清理(自持锁,IO 失败静默重试)
│   ├── StartupService.cs          # 开机启动:HKCU Run 注册表读写(Debug 构建用独立值名)
│   └── TrayService.cs             # 托盘 ApplicationContext:菜单(打开配置/全部启动/全部停止/开机启动/退出)+ 双击打开;首次运行自动开页 + 气泡引导
├── Web/                           # 内嵌 Web(命名空间 PortForwarder.Web)
│   ├── ApiServer.cs               # 构建并启动 Kestrel + Minimal API;具名实例方法做 handler + 请求 DTO
│   └── StaticFiles.cs             # wwwroot 静态托管(UseDefaultFiles + UseStaticFiles)
├── wwwroot/
│   ├── favicon.ico                # 网页图标(由 make-icon.ps1 生成)
│   └── index.html                 # 无构建单页前端(表格 + 编辑 + 测试 + 日志)
└── docs/
    ├── architecture.md            # 架构、启动时序、组件职责
    ├── api-contract.md            # REST API 契约(前后端约定)
    └── connection-test.md         # 三类连接测试的语义与实现要点
```

## 组件职责

| 组件 | 命名空间 | 职责 |
| --- | --- | --- |
| `Program` | PortForwarder | Mutex 单实例、手动组合服务、生命周期收尾 |
| `Models/*` | PortForwarder.Models | 纯数据模型与 DTO |
| `AppVersion` | PortForwarder.Core | 应用版本号读取:单一来源 csproj `<Version>`,托盘/网页/health 共用 |
| `ForwardRuleService` | PortForwarder.Core | 多规则进程注册表 + 日志环形缓冲 + gost 指纹锁定策略(TOFU/信任);按 Mode 分派 gost / mysql 两种管理器 |
| `IForwardManager` | PortForwarder.Core | 转发运行时管理器抽象(Start/Stop/IsRunning/日志事件),两种实现共用 |
| `GostProcessManager` | PortForwarder.Core | 单条 gost 子进程启停与日志捕获;路径绝对化 + 启动前 SHA-256 校验 |
| `MySqlRelayManager` | PortForwarder.Core | 进程内 MySQL TLS 中继(Mode=mysql):SSLRequest 分段补全兼容懒连接,TLS 端到端透传,不拉 gost、不接触凭据 |
| `ChildProcessJob` | PortForwarder.Core | kill-on-close Job Object:主进程死亡时内核兜底终止 gost |
| `Socks5Tester` | PortForwarder.Core | TCP / SOCKS5 端到端连接测试 |
| `ConfigService` | PortForwarder.Services | config.json 与规则文件(rules.json)读写,持有当前配置与规则列表;规则文件路径迁移(防覆盖) |
| `LogFileService` | PortForwarder.Services | 日志文件归档:按天滚动 gost-yyyyMMdd.log,超过保留天数自动清理 |
| `StartupService` | PortForwarder.Services | 开机启动:HKCU Run 注册表读写 |
| `TrayService` | PortForwarder.Services | 托盘图标、菜单、打开浏览器、退出;首次运行自动打开配置页 + 气泡引导 |
| `ApiServer` | PortForwarder.Web | 构建/启动内嵌 Web,REST 路由与 handler |
| `StaticFiles` | PortForwarder.Web | wwwroot 静态托管配置 |

## 代码约定(务必遵守)

### C# 风格
- **完整命名空间声明并带花括号**(不要 file-scoped `namespace X;`)。按分层用 `PortForwarder.Models` / `.Core` / `.Services` / `.Web`,入口在根 `PortForwarder`。
- **显式 `Program` 类**:`internal static class Program` + `[STAThread] Main`,不要顶层语句。
- **私有字段 `_camelCase`**(下划线前缀),构造函数内裸名赋值(不写 `this.`)。`const` / `static readonly` 用 PascalCase。
- **Allman 缩进**(花括号另起一行)。
- **显式类型优先于 `var`**。
- **传统方法体 + `return`**,不要表达式主体成员(`=>`)。Minimal API 用**具名实例方法**做 handler(见 `Web/ApiServer.cs`),直接用字段访问服务(不写 `[FromServices]`、不写内联 lambda 表达式)。
- 布尔组合用 `||` / `&&` 写成紧凑多行 `return`,而非拆成多个 `if`。
- 共享字典 / 缓冲用 `lock` 保护(见 `ForwardRuleService._gate`)。
- 标准 `.csproj` 工程结构。

### 前端
- **复制按钮必须有 HTTP 兜底**:`navigator.clipboard` 在非安全上下文(如绑 `0.0.0.0` 后从局域网 IP 访问)不可用,需 `document.execCommand('copy')` 或 textarea + select 兜底,避免静默失败。
- 无构建、无外部 CDN 依赖(离线可用)。

### 语言
- **中文**:文档、README、代码注释、commit message。
- **专业英文**:如需跨团队/对外沟通材料。
- 响应直接、可操作;主动标注边界情况与常见失败点。

## 关键坑位(先记着)
- gost 只在**有连接时**才向配置的代理端口拨号;代理软件未运行或该端口未监听会失败。连接测试要能明确区分「代理不可达」与「代理可达但到目标失败」。
- 目前只做 **TCP** 转发。UDP 目标(游戏/VoIP/WireGuard)不在本期范围,前端可留字段但后端先不实现。
- **序列化命名策略**:REST 走 camelCase、`config.json` / `rules.json` 走 PascalCase(见架构决策),改动 `ApiServer` / `ConfigService` 时务必保持。
- **规则与日志的落盘分工**:规则 CRUD 落盘调 `ConfigService.SaveRules()`(写规则文件),全局设置(含 TOFU 指纹)调 `Save()`(写 config.json),别混用。旧版内嵌规则由 `Load` 一次性迁移;规则文件损坏时先备份 `.bad-时间戳` 再按空列表继续,绝不静默覆盖用户数据。改规则文件路径是**迁移语义**:目标已存在返回 409 拒绝(防覆盖),旧文件留作备份。日志文件清理只认 `gost-????????.log` 命名模式且按**文件名里的日期**判断,不碰目录里其他文件;日志写失败静默丢弃 writer 待重试,绝不拖垮转发。
- **单文件发布(单应用)**:用 VS「发布」(`Properties/PublishProfiles/FolderProfile.pubxml`:单文件自包含 + 压缩 + 无 pdb;命令行等效 `dotnet publish -p:PublishProfile=FolderProfile`),**不再有 build.ps1**。发布产物**仅一个 `GostWebUI.exe`**:前端 `wwwroot/**` 内嵌进 exe——csproj 对其同时声明 `Content`(开发构建复制到输出目录、发布 `CopyToPublishDirectory=Never`)与 `EmbeddedResource`;`StaticFiles` 磁盘 `AppContext.BaseDirectory\wwwroot` 优先(开发热改/手动覆盖),缺失时回退嵌入资源(资源名前缀 `PortForwarder.wwwroot`,与 RootNamespace 绑定)。`gost.exe` 不随发布携带(发布 `Never`,开发构建仍复制到输出目录),用户自行下载放 exe 同目录;`config.json` 在首次启动或保存配置时生成(首次运行 `ConfigService.Load` 固化一次)。
- **gost.exe 完整性锁定(TOFU)**:首次成功启动时把 gost.exe 的 SHA-256 写入 `config.json`(`GostSha256`);之后每次启动前在**拒写共享句柄**内重新计算比对(句柄跨越校验与 CreateProcess,防 TOCTOU),不一致拒启并写规则日志,用户在网页「设置」里核实后信任(`GET /api/gost/integrity` / `POST /api/gost/trust`)。`GostPath` 一律经 `GostProcessManager.ResolveGostPath` 绝对化,**严禁把相对路径交给 CreateProcess**(其搜索顺序含 CWD 与 PATH,是同名劫持面)。边界要点:锁定值与文件同属用户可写位置,这是「提高门槛 + 可发现」而非同账户内硬边界;硬边界靠把程序目录放到仅管理员可写的位置。
- **锁纪律(死锁教训)**:`manager.Stop()` **严禁在 `ForwardRuleService._gate` 内调用**——gost 退出瞬间 Exited 回调线程「持 Process 内部锁 → AppendLog 等 `_gate`」,锁内 Stop 的 `Dispose` 又反过来等 Process 内部锁,构成 ABBA 死锁(实测线程栈证实,整个 API 会被卡死)。`StopRule` / `DeleteRule` / `StopAll` 均为锁内摘引用、锁外 Stop。`LogFileService` 自持锁且锁内不回调任何外部组件,锁序恒为 `_gate` → `LogFileService._gate` 单向;文件写入在回调线程上 `AppendLog` 返回(已释放 `_gate`)之后进行,不新增死锁面。
- **MySQL TLS 中继(`Mode=mysql`,懒连接兼容)**:目标经「对首包做嗅探/懒连接、且透传首字节」的中转时,server-first 的 MySQL 会握手超时(普通 gost 转发无解)。`MySqlRelayManager`(进程内 `TcpListener`,不拉 gost)连后端先只发 `SSLRequest` 的 **4 字节包头**激活懒连接(SSLRequest 是合法且**不依赖服务器 salt** 的早期包),读后端 Initial Handshake 转给客户端,读到客户端自己的 SSLRequest 后**用它的 32 字节原始 payload 补全**先前的请求包(seq 对齐:两边 SSLRequest 均 seq1,客户端 TLS 内的 Handshake Response 恰为 seq2),之后 **TLS 端到端裸透传**——中继不终止 TLS、不接触凭据、不需证书。要求客户端启用 TLS(mysqlsh `--ssl-mode=REQUIRED`),出口仅 direct/socks5。
    - **关键坑(charset 一致性,别再踩)**:MySQL 8.x 要求 SSLRequest 与随后 HandshakeResponse 的 **charset 字节完全一致**(实测 maxpacket/caps 一并复制更稳;caps 不一致后端容忍,charset 不容忍),否则后端回 `ER_HANDSHAKE_ERROR (1043) Bad handshake`。客户端 charset 各不同(mysqlsh 随系统区域=28、DBeaver/Connector-J 随驱动),中继**不能硬编码**替发 SSLRequest 的 payload,必须「先发头、后用客户端原始 payload 补全」让 charset 天然对齐(这正是上面拆两步发的原因)。曾硬编码 charset=45 → 真实客户端全数 Bad handshake;而手搓 Python 探针恰用 45 会**掩盖**此 bug——排查此类问题务必用**真实客户端**(mysql.exe/mysqlsh),假密码回 `1045 Access denied`=握手过、`1043 Bad handshake`=握手被拒。
    - 锁纪律:`RaiseLog` 一律锁外(持自身 `_gate` 时绝不回调外部,与服务层 `_gate` 无 ABBA),Stop 锁内摘引用/锁外关连接。**仅 MySQL 适用**:PostgreSQL/RDP 本就 client-first 无需;SMTP/SSH 等虽 server-first 但无「不依赖服务器数据的 client-first 前导包」可用。
