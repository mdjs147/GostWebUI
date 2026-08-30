# GostWebUI

GostWebUI 是一个面向 Windows 的本地端口转发管理工具。程序常驻系统托盘，通过仅监听回环地址的 Web 页面管理转发规则、进程状态、连接测试与日志。

普通 TCP 转发由独立安装的 [GOST（GO Simple Tunnel）](https://github.com/go-gost/gost) 可执行文件完成；MySQL TLS 中继模式由 GostWebUI 在进程内实现，不依赖 `gost.exe`。

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

## 许可证

GostWebUI 采用 [MIT License](LICENSE)，版权归 `mdjs147` 所有。

GOST、.NET 及其他第三方项目分别适用其各自许可证；第三方许可证不会自动成为 GostWebUI 的项目许可证。
