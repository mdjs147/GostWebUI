# 第三方项目与声明

本文说明 GostWebUI 与文档中提及的第三方项目之间的技术关系、来源和分发边界。各第三方项目的版权、商标及其他权利仍归其各自权利人所有。

## GOST（GO Simple Tunnel）

- 官方仓库：<https://github.com/go-gost/gost>
- 官方文档：<https://gost.run/>
- 官方发布：<https://github.com/go-gost/gost/releases>
- 上游许可证：[MIT License](https://github.com/go-gost/gost/blob/master/LICENSE)
- 上游版权声明：`Copyright (c) 2016 ginuerzh`

### 与 GostWebUI 的关系

GostWebUI 的普通 TCP 转发模式通过命令行启动用户提供的 `gost.exe`，并读取其标准输出和退出状态。两者是独立进程；GostWebUI 不链接、嵌入或修改 GOST 源码。

本仓库不包含 `gost.exe`，默认发布配置也明确排除该文件。用户应从 GOST 官方渠道获取二进制文件，并自行核对版本、完整性和适用许可证。GostWebUI 首次成功启动 GOST 时会记录 SHA-256 指纹，后续变更需要用户重新确认；该机制不能替代官方签名、哈希或来源验证。

GostWebUI 不是 GOST 官方项目，与 GOST 作者及维护者不存在隶属、合作、认证或背书关系。“GOST”和“GO Simple Tunnel”仅用于识别互操作的外部程序。

## Microsoft .NET 与 ASP.NET Core

GostWebUI 以 `.NET 10` 为目标框架，使用 Windows Forms 和 ASP.NET Core。源码通过 .NET SDK 与框架引用构建，没有把 .NET 或 ASP.NET Core 源码复制到本仓库。

自包含发布会把所选运行时包中的 .NET 组件打包进应用。重新分发此类二进制文件时，发布者应保留与实际 SDK、目标平台和运行时包对应的许可证及第三方声明。以下官方资料用于定位适用条款，不能替代发布时随实际运行时包提供的完整文件：

- [.NET 许可证与分发说明](https://github.com/dotnet/core/blob/main/license-information.md)
- [.NET Runtime LICENSE.TXT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [.NET Runtime THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [ASP.NET Core LICENSE.txt](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt)
- [ASP.NET Core THIRD-PARTY-NOTICES.txt](https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt)

## 分发说明

- `dotnet publish` 生成的单文件应用不等于一份完整的公开发行包。
- 对外发布时，应同时提供 GostWebUI 自身的 `LICENSE`、本文档，以及实际自包含运行时要求保留的许可证和第三方声明。
- 不要把本机的 `config.json`、`rules.json`、日志、`gost.exe`、调试符号或用户专属发布配置打入源码仓库或公开发行包。
- 若将来直接复制、修改或内嵌第三方源码、图标、文档或其他资源，应在引入变更时同步更新本文及发行包中的适用许可证文本。
