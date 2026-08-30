<div align="center">

<h1>GostWebUI</h1>

<p><strong>本地优先的 Windows 端口转发管理工具</strong></p>

<p>通过系统托盘和仅监听回环地址的 Web 页面，集中管理 GOST TCP 转发规则、链路诊断与运行日志；另提供进程内 MySQL TLS 中继。</p>

<p>
  <a href="https://github.com/mdjs147/GostWebUI/releases"><img alt="release" src="https://img.shields.io/github/v/release/mdjs147/GostWebUI?display_name=tag&amp;sort=semver&amp;label=release"></a>
  <a href="LICENSE"><img alt="license: MIT" src="https://img.shields.io/badge/license-MIT-68a51c"></a>
  <img alt="platform: Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-0078d4">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4">
  <img alt="UI: Embedded Web" src="https://img.shields.io/badge/UI-Embedded%20Web-0f766e">
</p>

</div>

---

## 这是什么

在 Windows 上维护多条 GOST 命令、进程和日志并不直观。GostWebUI 把这些操作收进一个常驻托盘程序：在本地网页中创建和启停转发规则，选择直连、SOCKS5 或 HTTP 代理出口，诊断本地监听与目标链路，并查看和归档运行日志。

普通 TCP 转发由用户独立安装的 [GOST（GO Simple Tunnel）](https://github.com/go-gost/gost) 可执行文件完成，GostWebUI 负责配置、进程管理和状态展示。对于需要兼容首包嗅探或懒连接的场景，项目还提供不依赖 `gost.exe` 的进程内 MySQL TLS 中继模式。

## 项目关系

- GostWebUI 是独立社区项目，不是 GOST 官方项目，也未获得 GOST 作者或维护者的背书。
- `gost.exe` 不包含在本仓库和 GostWebUI 的发布产物中。用户需要从 [GOST 官方 Releases](https://github.com/go-gost/gost/releases) 自行下载，并遵守其 [MIT License](https://github.com/go-gost/gost/blob/master/LICENSE)。
- GostWebUI 不绑定特定代理软件；任何与规则配置兼容的 SOCKS5 或 HTTP 代理服务都可以使用。
- “GOST”及相关项目名称仅用于说明兼容性和来源，不表示隶属、合作、认证或背书关系。

完整的第三方项目、许可证和分发边界见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 功能概览

- Windows 托盘常驻与单实例运行
- 本地 Web 管理页面，无前端构建和外部 CDN
- 多规则 TCP 端口转发与启停管理
- 直连、SOCKS5 和 HTTP 代理出口
- TCP、SOCKS5 目标链路和本地监听诊断
- `gost.exe` SHA-256 指纹锁定
- 进程内 MySQL TLS 中继（懒连接兼容）
- 本地规则、配置与按日滚动日志

## 快速开始

详细的下载、开发、发布和使用说明见 [GostWebUI/README.md](GostWebUI/README.md)。

```powershell
cd .\GostWebUI
dotnet run
```

运行普通转发规则前，请另外下载官方 `gost.exe` 并放到程序目录，或在设置中填写其完整路径。

## 文档

- [使用与开发说明](GostWebUI/README.md)
- [架构说明](GostWebUI/docs/architecture.md)
- [REST API 契约](GostWebUI/docs/api-contract.md)
- [连接测试说明](GostWebUI/docs/connection-test.md)
- [第三方声明](THIRD-PARTY-NOTICES.md)
- [贡献指南](CONTRIBUTING.md)
- [安全政策](SECURITY.md)

## 持续集成与发布

GitHub Actions 在提交到 `main`、针对 `main` 的 Pull Request 以及手动运行时执行 Release 构建，并保留 7 天的 `win-x64` 单文件构建产物。

发布工作流支持两种触发方式：

- 推送符合 `vMAJOR.MINOR.PATCH` 的标签，例如 `v2.1.1`；
- 在 GitHub Actions 的 `Release` 工作流中手动输入标签。

标签中的基础版本必须与 `GostWebUI/GostWebUI.csproj` 的 `<Version>` 一致。工作流会创建 GitHub Release，并上传：

- `GostWebUI-<version>-win-x64.zip`：`GostWebUI.exe`、MIT、第三方声明和使用文档；
- 同名 `.sha256` 文件：用于校验 ZIP 完整性。

正式发布前应确认 CI 为绿色、版本号正确，并在本地完成单文件发布验证。Release 工作流使用仓库内置的 `GITHUB_TOKEN`，不需要保存个人访问令牌。

## 许可证

GostWebUI 采用 [MIT License](LICENSE)，版权归 `mdjs147` 所有。

GOST、.NET 及其他第三方项目分别适用其各自许可证；第三方许可证不会自动成为 GostWebUI 的项目许可证。
