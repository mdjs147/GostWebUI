# GostWebUI(托盘 + Web 配置的 gost 端口转发器)

.NET 10 单进程程序:**托盘常驻**,双击图标打开本地网页做配置。普通转发由外部 [GOST（GO Simple Tunnel）](https://github.com/go-gost/gost) 进程完成，可经任意兼容的 SOCKS5 或 HTTP 代理转发到目标。

**默认规则**:`127.0.0.1:41011` → 经 `socks5://127.0.0.1:3066`(本机 SOCKS5 代理)→ `192.0.2.10:41011`。

## 与 GOST 及代理软件的关系

- GostWebUI 是独立社区项目，不是 GOST 的官方组件，也未获得 GOST 作者或维护者的背书。
- 普通转发模式通过命令行启动用户自行提供的 `gost.exe`；本仓库和默认发布产物都不包含 GOST 二进制或源码。
- MySQL TLS 中继模式由 GostWebUI 在进程内实现，用于兼容会做首包嗅探或懒连接的中转，不依赖 `gost.exe`。
- GostWebUI 不绑定特定代理软件；任何与规则配置兼容的 SOCKS5 或 HTTP 代理服务都可以使用。

第三方项目、许可证来源与分发边界见仓库根目录的 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 获取 gost.exe

- 下载地址(GitHub Releases):<https://github.com/go-gost/gost/releases>
- 官方项目与文档：<https://github.com/go-gost/gost> / <https://gost.run/>
- 上游许可证：[MIT License](https://github.com/go-gost/gost/blob/master/LICENSE)
- Windows 选 `gost_x.y.z_windows_amd64.zip`(32 位系统选 `windows_386`),解压出 `gost.exe`。
- 放到本程序同目录(默认查找位置),或在网页「设置 → gost 可执行文件路径」填完整路径(设置面板里也有此下载链接)。

## 快速开始

```powershell
# 1. 下载 gost.exe(见上节),放到项目/程序目录
# 2. 开发运行
dotnet run
# 或发布:VS 里右键项目 →「发布」(FolderProfile);命令行等效:
dotnet publish GostWebUI.csproj -p:PublishProfile=FolderProfile
```

`dotnet publish` 的程序产物**仅一个 `GostWebUI.exe`**(单文件自包含,前端已内嵌,无 pdb):把下载的 gost.exe 放到 exe 同目录即可用;`config.json` / `rules.json` 在网页里保存配置/规则时自动生成,日志目录 `logs` 在规则首次产生日志时自动创建。制作公开发行包时，还应附带项目许可证、第三方声明及实际自包含运行时要求保留的许可证文件；详见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

运行后:
- 系统托盘出现图标,程序在后台运行。
- **双击托盘图标**(或右键「打开配置」)→ 浏览器打开 `http://127.0.0.1:31847`(开发构建 `dotnet run` 用独立端口 `38517`,与正式版互不干扰)。
- 网页里:填 gost 路径 → 新增/编辑规则 → 启动 → 「诊断链路」验证;「设置」对话框里可开关「开机启动」。
- 右键托盘菜单:打开配置 / 全部启动 / 全部停止 / 开机启动 / 退出。**只有「退出」才真正结束进程。**
- 程序已在运行时再次启动 exe,只会打开配置页,不会起第二个实例(命名 Mutex 保护)。

## 目录结构

按 `Models` / `Core` / `Services` / `Web` 分层,命名空间对应 `GostWebUI.*`:

```
Program.cs            入口:Mutex 单实例 → 手动组合服务 → 启动内嵌 Web → 托盘消息循环
Models/               数据模型:ForwardRule / AppConfig / ConnectionTestResult / LogEntry / GostIntegrityInfo
Core/                 逻辑:ForwardRuleService(规则+进程+日志) / GostProcessManager / ChildProcessJob / Socks5Tester / AppVersion
Services/             服务:ConfigService(config.json + rules.json 读写) / LogFileService(日志文件归档) / StartupService / TrayService(托盘)
Web/                  内嵌 Web:ApiServer(Minimal API) / StaticFiles(静态托管)
wwwroot/index.html    无构建单页前端
```

开发前请读:
- `../README.md` — 公开仓库入口、项目关系与许可证
- `CLAUDE.md` — 上下文、架构决策、目录规范、代码约定
- `docs/architecture.md` — 单进程双子系统架构与启动时序
- `docs/api-contract.md` — 前后端 REST 契约
- `docs/connection-test.md` — 三类连接测试语义

## 连接测试

网页每条规则有「诊断链路」按钮,后端用自实现的最小 SOCKS5 CONNECT 做端到端验证:能明确区分「SOCKS5 代理不可达」与「代理可达但到目标失败」,不必先起转发就能验证代理链路。细节见 `docs/connection-test.md`。

## 注意
- **gost.exe 防替换(指纹锁定)**:首次成功启动规则时会自动锁定 gost.exe 的 SHA-256 指纹;之后文件内容变化(被替换或你升级了 gost)会**拒绝启动**并在规则日志说明原因,到网页「设置」里核对指纹后点「信任当前文件」即可恢复。想要更硬的防线,把程序目录放到仅管理员可写的位置(如 `C:\Program Files\GostWebUI\`;该形态下保存配置/规则/日志会失败,可在「设置 → 存储」把规则文件与日志目录指到用户可写的位置,`config.json` 仍需单独放宽写权限)。
- gost 只在有连接时才向代理端口拨号;使用前请确认所选代理软件已运行，并且配置的 SOCKS5 或 HTTP 端口正在监听。
- 目前只做 **TCP**;UDP 目标不在本期范围。
- Kestrel 只绑 `127.0.0.1`,不对外。改端口需重启。默认端口:正式版 `31847`、开发构建 `38517`。
- **数据存储**:全局配置存于 exe 同目录 `config.json`;**规则单独存于 `rules.json`**(默认 exe 同目录)。规则文件的位置可在网页「设置 → 存储」里修改:修改即把当前规则迁移到新位置,新位置已有同名文件时会拒绝(防覆盖),旧文件保留作备份。
- **运行日志落盘**:每条规则的 gost 输出除了网页实时显示(内存 500 行),还按天写入日志目录下的 `gost-yyyyMMdd.log`(默认 exe 同目录 `logs`,行首带时间与规则名)。日志目录与**最大保留天数**都在「设置 → 存储」里配置:文件日期超过保留天数自动删除,`0` 表示永久保留(默认 7 天)。
- 「开机启动」写 `HKCU\...\Run` 注册表(当前用户、免管理员);exe 移动位置后,下次启动会自动修正注册路径。

## 许可证、贡献与安全

- GostWebUI 自身采用 [MIT License](../LICENSE)；GOST、.NET 等第三方项目仍分别适用其各自许可证。
- 第三方归属和分发边界：[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)
- 贡献说明：[CONTRIBUTING.md](../CONTRIBUTING.md)
- 安全问题报告：[SECURITY.md](../SECURITY.md)
