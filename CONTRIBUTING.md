# 贡献指南

感谢你关注 GostWebUI。提交问题或代码前，请先阅读本指南以及 [CLAUDE.md](CLAUDE.md) 中的架构约束。

## 当前许可证状态

项目自身的 `LICENSE` 尚未确定。在许可证添加前，建议先通过 Issue 讨论改动，不要假定公开仓库中的代码已经获得复制、修改或再分发授权。维护者确定项目许可证后，本节应同步更新。

## 报告问题

Issue 应至少包含：

- GostWebUI 版本、Windows 版本和 .NET SDK 版本
- 使用的转发模式：`gost` 或 `mysql`
- GOST 版本及获取来源（如适用）
- 可复现步骤、预期结果和实际结果
- 已脱敏的错误信息或日志

请删除目标公网地址、用户名、密码、Token、代理订阅、完整规则文件和本机绝对路径。安全漏洞请按 [SECURITY.md](SECURITY.md) 报告，不要直接公开利用细节。

## 开发与验证

```powershell
dotnet restore .\GostWebUI\GostWebUI.csproj -r win-x64
dotnet build .\GostWebUI\GostWebUI.slnx
dotnet publish .\GostWebUI\GostWebUI.csproj --no-restore -p:PublishProfile=FolderProfile
```

文档变更至少应检查 Markdown 链接和示例一致性。代码变更应运行与改动最相关的构建或测试，并在 Pull Request 中说明未能完成的验证。

## 变更边界

- 保持 .NET 单进程、WinForms 托盘和本地 ASP.NET Core Web 管理页的既有架构。
- 不提交 `gost.exe`、运行时配置、规则、日志、构建产物、IDE 用户文件或任何凭据。
- GOST 兼容性变更应引用其官方仓库或文档，并保持“外部独立进程、不随项目分发”的边界。
- 项目不绑定特定代理软件；新功能应基于通用协议能力，避免引入不必要的客户端专属依赖。
- 不引入外部 CDN 或前端构建工具，除非先形成明确的架构决策。
- 一个 Pull Request 聚焦一个目标，避免顺带重构或格式化无关文件。

## 第三方内容

引入第三方源码、资源或文档前，必须确认其许可证允许当前使用方式，并同步更新 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。不要仅因为内容可以在互联网访问就认为可以复制到仓库。
