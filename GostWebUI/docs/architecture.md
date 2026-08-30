# 架构说明

## 进程模型:单进程,双子系统

同一个 `WinExe` 进程内并存两套东西:

```
┌────────────────────────────── GostWebUI.exe (单进程) ──────────────────────────────┐
│                                                                                          │
│   主线程 (STA)                              后台 (Kestrel 线程池)                          │
│   ┌────────────────────────┐               ┌────────────────────────────────────────┐   │
│   │ WinForms 消息循环         │               │ ASP.NET Core Minimal API (Kestrel)      │   │
│   │ Application.Run(         │               │ 绑定 127.0.0.1:{WebPort}                 │   │
│   │   TrayService)           │               │  ├─ /            → wwwroot/index.html    │   │
│   │  ├─ NotifyIcon 托盘图标   │               │  └─ /api/*       → ApiServer             │   │
│   │  └─ 右键菜单/双击打开浏览器 │               └───────────────┬────────────────────────┘   │
│   └────────────────────────┘                               │ 字段直调(无全局 DI)          │
│                                                            ▼                             │
│                                          ┌──────────────────────────────────┐            │
│                                          │ ForwardRuleService (服务层)        │            │
│                                          │  ├─ Dictionary<id, GostProcessMgr>│            │
│                                          │  └─ 每规则日志环形缓冲(500 行)     │            │
│                                          └───────────────┬──────────────────┘            │
│                                                          │ 启动/停止                      │
│                                                          ▼                               │
│                                          gost.exe 子进程(每条规则一个)                    │
│                                          -L tcp://:41011/192.0.2.10:41011                │
│                                          -F socks5://127.0.0.1:3066                       │
└──────────────────────────────────────────────────────────────────────────────────────┘
                                                          │
                                                          ▼
                                     本地代理(全代理入站 127.0.0.1:3066)→ 节点 → 目标
```

`ForwardRuleService` 按规则的 `Mode` 分派两种运行时管理器(共用 `IForwardManager` 抽象):

- `gost`(默认):`GostProcessManager` 拉起 gost 子进程做通用 TCP 转发(即上图);
- `mysql`:`MySqlRelayManager` 在**本进程内**起 `TcpListener` 做 MySQL TLS 中继,不拉子进程 —— 以 SSLRequest 分段补全兼容「对首包做嗅探/懒连接、且透传首字节」的中转并访问 server-first 的 MySQL。原理:连后端先发送 MySQL `SSLRequest` 包头(合法且不依赖服务器 salt 的早期包)激活懒连接,再用客户端原始 payload 补全请求;随后 TLS 端到端裸透传,认证由客户端在 TLS 内用真实 salt/密码完成 —— 中继不终止 TLS、不接触凭据。详见 `docs/connection-test.md`。

## 启动时序(Program.Main)

1. 命名 `Mutex`(`Local\GostWebUI-SingleInstance`,Debug 构建带 `-Dev` 后缀)判断单实例;已有实例则打开配置页后退出,不重复起托盘。
2. `ConfigService.Load()` 读 `config.json`,再读 `RulesPath` 指向的规则文件(默认 exe 同目录 `rules.json`)。首次运行(`config.json` 不存在)时置 `IsFirstRun` 并立即固化一次配置,使初次引导只在真正的第一次启动触发。
3. 手动 `new` 组合服务:`ConfigService` → `LogFileService(日志目录, 保留天数)`(构造时清理一次过期日志)→ `ForwardRuleService(configService, logFile)` → `Socks5Tester` → `StartupService`(已启用开机启动时刷新注册路径,exe 移动后自动修正)。
4. `new ApiServer(...).Start()`:构建 `WebApplication`,`UseUrls("http://127.0.0.1:{WebPort}")`,挂静态文件(`StaticFiles.Configure`)与 REST 路由,`StartAsync()` **非阻塞**启动 Kestrel。
5. `ForwardRuleService.StartAutoStartRules()` 拉起勾选自动运行的规则。
6. `Application.Run(new TrayService(..., configService.IsFirstRun))` 进入托盘消息循环(阻塞主线程直到「退出」)。**首次运行**(`config.json` 不存在)时,`TrayService` 构造中自动打开配置页并弹托盘气泡,引导用户去设置。
7. 退出后:`ForwardRuleService.StopAll()`(收尾日志仍能落盘)→ `ApiServer.Stop()` → `LogFileService.Dispose()` → 释放 Mutex。

## 组件职责

| 组件 | 命名空间 | 职责 |
| --- | --- | --- |
| `Program` | GostWebUI | Mutex 单实例、手动组合服务、生命周期收尾 |
| `TrayService` | GostWebUI.Services | 托盘图标、菜单、打开浏览器、退出;首次运行自动打开配置页 + 气泡引导 |
| `ApiServer` | GostWebUI.Web | 构建/启动内嵌 Web,REST 路由与具名实例方法 handler |
| `StaticFiles` | GostWebUI.Web | wwwroot 静态托管 |
| `AppVersion` | GostWebUI.Core | 应用版本号读取：单一来源 csproj `<Version>`，托盘/网页/health 共用 |
| `ForwardRuleService` | GostWebUI.Core | 多规则进程注册表 + 日志环形缓冲 + gost 指纹锁定策略(TOFU/信任)(API 服务层);按 Mode 分派 gost / mysql 两种管理器 |
| `IForwardManager` | GostWebUI.Core | 转发运行时管理器抽象(Start/Stop/IsRunning/日志事件),GostProcessManager 与 MySqlRelayManager 共用 |
| `GostProcessManager` | GostWebUI.Core | 单条规则的 gost 子进程启停与日志捕获;路径绝对化 + 启动前 SHA-256 校验 |
| `MySqlRelayManager` | GostWebUI.Core | 进程内 MySQL TLS 中继(Mode="mysql"):SSLRequest 分段补全兼容懒连接,TLS 端到端透传,不拉 gost、不接触凭据 |
| `ChildProcessJob` | GostWebUI.Core | kill-on-close Job Object:主进程死亡时内核兜底终止 gost 子进程 |
| `Socks5Tester` | GostWebUI.Core | TCP / SOCKS5 端到端连接测试 |
| `ConfigService` | GostWebUI.Services | config.json 与规则文件(rules.json)读写,持有当前配置与规则列表;规则文件路径迁移(防覆盖) |
| `LogFileService` | GostWebUI.Services | 日志文件归档:按天滚动写 gost-yyyyMMdd.log,超过保留天数自动清理;自持锁,IO 失败静默重试 |
| `StartupService` | GostWebUI.Services | 开机启动:HKCU Run 注册表读写(Debug 构建用独立值名) |
| `ForwardRule` / `AppConfig` / `ConnectionTestResult` / `LogEntry` / `GostIntegrityInfo` | GostWebUI.Models | 数据模型与 DTO |

## 关键约束

- Kestrel **只绑 `127.0.0.1`**,不对外暴露;`ApiServer.ValidateHostHeader` 对所有请求校验 Host 头(仅放行 `127.0.0.1` / `localhost` / `[::1]`),防 DNS rebinding 借访问者浏览器读写本 API。若日后要局域网访问,改 `UseUrls` 为 `0.0.0.0`、**同步放宽 Host 白名单**,并处理防火墙 + 前端复制兜底(已在前端预留)。
- **默认端口按构建区分**(集中在 `AppConfig.DefaultWebPort`):正式(Release)`31847`,开发(Debug)`38517`;都选冷门值以避开常见服务与 Windows 临时端口段(49152+)。Debug 构建在 `Program.Main` 里**强制**使用开发端口(无视 config.json),与正式版彻底隔离;单实例 Mutex 名也按构建区分,两个环境可同时运行互不干扰。
- 改 `WebPort` 需重启进程生效(端口在启动时绑定)。
- **序列化命名策略**:REST(`ApiServer`)走 `Results.Json` 默认 camelCase(与前端字段对齐);`config.json` 与 `rules.json`(`ConfigService`)落盘走 System.Text.Json 默认 PascalCase(仅本进程读写)。两者不可混淆。
- **数据存储布局**:`config.json` 固定 exe 同目录(启动锚点,存 WebPort/GostPath/GostSha256/RulesPath/LogDirectory/LogRetentionDays);规则本体在 `RulesPath` 指向的 `rules.json`(顶层数组,默认 exe 同目录,网页「设置」可迁移,目标已存在时拒绝防覆盖,旧文件留作备份);运行日志按天写 `LogDirectory` 下的 `gost-yyyyMMdd.log`(默认 exe 同目录 `logs`),文件名日期超过 `LogRetentionDays` 自动删除(0 = 永久)。规则文件损坏时先备份为 `.bad-时间戳` 再按空列表继续,不静默覆盖用户数据。网页实时日志走内存环形缓冲,与文件归档互不依赖。
- **LogFileService 锁纪律**:日志文件写入在 gost 输出回调线程上、`AppendLog`(取 `_gate`)返回之后进行;`LogFileService` 自持锁且锁内不回调外部组件,锁序恒为 `_gate` → `LogFileService._gate` 单向,不新增死锁面。日志 IO 失败一律吞掉并丢弃写入器待重试,绝不拖垮转发功能。
- 单文件自包含发布(单应用):经 VS「发布」(`Properties/PublishProfiles/FolderProfile.pubxml`)产出**仅一个 `GostWebUI.exe`**。前端 `wwwroot/**` 作为 `EmbeddedResource` 内嵌进 exe,发布时不落地(`CopyToPublishDirectory=Never`);`StaticFiles` 优先用磁盘 `AppContext.BaseDirectory\wwwroot`(开发构建仍复制到输出目录,改前端即时生效),磁盘缺失时回退嵌入资源。`gost.exe` 不随发布携带,用户自行下载放 exe 同目录(开发构建仍复制到输出目录便于调试);`config.json` 在首次启动或保存配置时自动生成(首次运行 `ConfigService.Load` 会固化一次)。
- **gost.exe 完整性锁定(TOFU)**:防同目录 gost.exe 被恶意替换。`GostProcessManager.Start` 先把 `GostPath` 解析为绝对路径(相对路径锚定程序目录,**不让 CreateProcess 走 CWD/PATH 搜索**),再以「只读 + 拒绝写/删除共享」句柄打开文件、从句柄算 SHA-256,句柄保持到 CreateProcess 完成(封死校验与启动之间的 TOCTOU 替换窗口)。校验策略在 `ForwardRuleService.CheckGostIntegrity`:锁定值(`config.json` 的 `GostSha256`)为空时首次信任并落盘;不一致时拒启,由用户在网页「设置」核实指纹后通过 `POST /api/gost/trust` 信任。此机制是「提高门槛 + 可发现」,不是同账户内的硬安全边界(能改文件的攻击者往往也能改锁定值);硬边界靠把程序目录放到仅管理员可写的位置。
- **锁纪律**:`GostProcessManager.Stop()` 严禁在 `ForwardRuleService._gate` 内调用——gost 退出瞬间,Exited 回调线程「持 `Process` 内部锁 → `AppendLog` 等 `_gate`」,若停止线程「持 `_gate` → `Dispose` 等 `Process` 内部锁」即构成 ABBA 死锁(实测线程栈证实,表现为所有取 `_gate` 的 API 永久挂起)。`StopRule` / `DeleteRule` / `StopAll` 统一为「锁内摘引用,锁外 Stop」,启停互斥由 manager 自身的锁保证。
