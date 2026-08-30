using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using GostWebUI.Core;
using GostWebUI.Models;
using GostWebUI.Services;

namespace GostWebUI.Web
{
    // ===== 请求体 DTO =====
    public class ConfigUpdateDto
    {
        public int WebPort { get; set; }
        public string GostPath { get; set; }
        // 以下三项缺省(null)表示不修改;RulesPath / LogDirectory 传空串表示恢复默认位置
        public string RulesPath { get; set; }
        public string LogDirectory { get; set; }
        public int? LogRetentionDays { get; set; }
    }

    public class TestProxyDto
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public int TimeoutMs { get; set; }
    }

    public class TestTargetDto
    {
        public string ProxyHost { get; set; }
        public int ProxyPort { get; set; }
        public string TargetHost { get; set; }
        public int TargetPort { get; set; }
        public int TimeoutMs { get; set; }
    }

    public class TestListenerDto
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public int TimeoutMs { get; set; }
    }

    public class StartupUpdateDto
    {
        public bool Enabled { get; set; }
    }

    // 内嵌 Web 服务:构建并非阻塞启动 Kestrel + Minimal API。
    // 持有服务层实例,所有 handler 为具名实例方法,直接使用字段(不经 [FromServices])。
    // 响应统一走 Results.Json(默认 Web camelCase 命名),与前端 index.html 契约一致。
    public class ApiServer
    {
        private readonly ConfigService _configService;
        private readonly ForwardRuleService _ruleService;
        private readonly Socks5Tester _tester;
        private readonly StartupService _startupService;
        private WebApplication _app;

        public ApiServer(ConfigService configService, ForwardRuleService ruleService, Socks5Tester tester, StartupService startupService)
        {
            _configService = configService;
            _ruleService = ruleService;
            _tester = tester;
            _startupService = startupService;
            _app = null;
        }

        // 非阻塞启动:构建 WebApplication,挂静态文件与路由,StartAsync 后返回。
        public void Start()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:" + _configService.Config.WebPort.ToString());

            WebApplication app = builder.Build();

            // DNS rebinding 防护:先于静态文件与所有路由校验 Host 头
            app.Use(ValidateHostHeader);

            StaticFiles.Configure(app);
            MapEndpoints(app);

            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
        }

        public void Stop()
        {
            if (_app != null)
            {
                _app.StopAsync().GetAwaiter().GetResult();
                _app = null;
            }
        }

        // DNS rebinding 防护:只放行本机回环 Host(127.0.0.1 / localhost / [::1]),其余一律 403。
        // 恶意网页可把自有域名的 DNS 解析切到 127.0.0.1,让访问者浏览器以「同源」身份读写本 API
        // (进而把 GostPath 改成任意程序并启动);校验 Host 后此路不通。
        // 注意:若日后把绑定改为 0.0.0.0 开放局域网访问,需同步放宽这里的白名单。
        private Task ValidateHostHeader(HttpContext context, RequestDelegate next)
        {
            string host = context.Request.Host.Host;
            if (host == "127.0.0.1" ||
                host == "[::1]" ||
                string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsync("Forbidden: invalid Host header");
        }

        // Minimal API 路由映射。所有 handler 为具名实例方法(不写内联 lambda 表达式)。
        private void MapEndpoints(WebApplication app)
        {
            app.MapGet("/api/health", GetHealth);

            app.MapGet("/api/config", GetConfig);
            app.MapPut("/api/config", UpdateConfig);

            app.MapGet("/api/gost/integrity", GetGostIntegrity);
            app.MapPost("/api/gost/trust", TrustGost);

            app.MapGet("/api/startup", GetStartup);
            app.MapPut("/api/startup", UpdateStartup);

            app.MapGet("/api/rules", GetRules);
            app.MapPost("/api/rules", CreateRule);
            app.MapPut("/api/rules/{id}", UpdateRule);
            app.MapDelete("/api/rules/{id}", DeleteRule);

            // 字面量段优先于 {id} 模板匹配,与 /api/rules/{id}/start 等不冲突
            app.MapPost("/api/rules/start-all", StartAllRules);
            app.MapPost("/api/rules/stop-all", StopAllRules);

            app.MapPost("/api/rules/{id}/start", StartRule);
            app.MapPost("/api/rules/{id}/stop", StopRule);
            app.MapGet("/api/rules/{id}/status", GetRuleStatus);
            app.MapGet("/api/rules/{id}/logs", GetRuleLogs);

            app.MapPost("/api/test/proxy", TestProxy);
            app.MapPost("/api/test/target", TestTarget);
            app.MapPost("/api/test/listener", TestListener);
        }

        private IResult GetHealth()
        {
            return Results.Json(new { ok = true, version = AppVersion.Display });
        }

        private IResult GetConfig()
        {
            AppConfig config = _configService.Config;
            // rulesPath / logDirectory 返回解析后的生效绝对路径(空配置也显示默认位置,前端直接回显);
            // rules 取锁内快照,避免序列化期间列表被其他请求并发增删
            return Results.Json(new
            {
                webPort = config.WebPort,
                gostPath = config.GostPath,
                rulesPath = _configService.ResolveRulesPath(),
                logDirectory = _configService.ResolveLogDirectory(),
                logRetentionDays = config.LogRetentionDays,
                rules = _ruleService.GetRules()
            });
        }

        private IResult UpdateConfig([FromBody] ConfigUpdateDto dto)
        {
            if (dto == null)
            {
                return Fail(StatusCodes.Status400BadRequest, "请求体不能为空");
            }
            // 越界端口此前被服务层静默忽略却仍返回 ok,用户会误以为已保存;这里显式拒绝
            if (dto.WebPort != 0 &&
                (dto.WebPort < 1 || dto.WebPort > 65535))
            {
                return Fail(StatusCodes.Status400BadRequest, "Web 端口需在 1-65535 之间(传 0 表示不修改)");
            }
            if (dto.LogRetentionDays.HasValue &&
                (dto.LogRetentionDays.Value < 0 || dto.LogRetentionDays.Value > AppConfig.MaxLogRetentionDays))
            {
                return Fail(StatusCodes.Status400BadRequest, "日志保留天数需在 0-" + AppConfig.MaxLogRetentionDays.ToString() + " 之间(0 表示永久保留)");
            }
            // 经服务层在锁内更新并落盘,与规则增删的保存串行化。改 Web 端口需重启进程才生效。
            // 规则文件路径变更是迁移语义:目标已存在会被拒绝(防覆盖),错误信息可直接展示。
            string error = _ruleService.UpdateGlobalConfig(dto.GostPath, dto.WebPort, dto.RulesPath, dto.LogDirectory, dto.LogRetentionDays);
            if (error != null)
            {
                return Fail(StatusCodes.Status409Conflict, error);
            }
            return Results.Json(new { ok = true, restartRequired = true });
        }

        // gost.exe 完整性状态:解析路径 + 当前文件指纹 + 锁定指纹比对(见 ForwardRuleService 完整性注释)
        private IResult GetGostIntegrity()
        {
            return Results.Json(_ruleService.GetGostIntegrity());
        }

        // 信任当前 gost.exe(把锁定指纹更新为当前文件指纹)。文件缺失/不可读时返回 409 不改动锁定值。
        private IResult TrustGost()
        {
            GostIntegrityInfo info = _ruleService.TrustCurrentGost();
            if (!info.Trusted)
            {
                return Fail(StatusCodes.Status409Conflict, "无法读取 gost.exe(" + info.GostPath + "),未更新锁定指纹");
            }
            return Results.Json(new { ok = true, pinnedSha256 = info.PinnedSha256 });
        }

        private IResult GetStartup()
        {
            return Results.Json(new { enabled = _startupService.IsEnabled() });
        }

        private IResult UpdateStartup([FromBody] StartupUpdateDto dto)
        {
            if (dto == null)
            {
                return Fail(StatusCodes.Status400BadRequest, "请求体不能为空");
            }
            try
            {
                if (dto.Enabled)
                {
                    _startupService.Enable();
                }
                else
                {
                    _startupService.Disable();
                }
                return Results.Json(new { ok = true, enabled = _startupService.IsEnabled() });
            }
            catch (Exception ex)
            {
                return Fail(StatusCodes.Status500InternalServerError, "设置开机启动失败:" + ex.Message);
            }
        }

        private IResult GetRules()
        {
            return Results.Json(_ruleService.GetRules());
        }

        // 规则字段服务端校验(前端已有同等校验,这里兜底防御直接调 API 的场景):
        // 返回 null 表示合法,否则为可直接展示的错误信息。
        private string ValidateRule(ForwardRule rule)
        {
            if (rule == null)
            {
                return "规则为空";
            }
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                return "规则名称不能为空";
            }
            if (rule.ListenPort < 1 || rule.ListenPort > 65535)
            {
                return "监听端口需在 1-65535 之间";
            }
            if (string.IsNullOrWhiteSpace(rule.TargetHost))
            {
                return "目标地址不能为空";
            }
            if (rule.TargetPort < 1 || rule.TargetPort > 65535)
            {
                return "目标端口需在 1-65535 之间";
            }
            // 与 ForwardRule.BuildGostArguments 的语义对齐:direct(或未填类型)不需要代理字段
            bool needProxy = rule.ProxyType != null &&
                             rule.ProxyType.ToLowerInvariant() != "direct";
            if (needProxy &&
                (string.IsNullOrWhiteSpace(rule.ProxyHost) || rule.ProxyPort < 1 || rule.ProxyPort > 65535))
            {
                return "代理地址与端口不完整(direct 直连规则无需填写)";
            }
            // 转发模式:仅 gost(默认)/ mysql;mysql 为进程内 MySQL TLS 中继,出口仅支持 direct 或 socks5
            string mode = rule.Mode == null ? "gost" : rule.Mode.Trim().ToLowerInvariant();
            if (mode.Length == 0)
            {
                mode = "gost";
            }
            if (mode != "gost" && mode != "mysql")
            {
                return "转发模式不支持(仅 gost 或 mysql)";
            }
            if (mode == "mysql")
            {
                string proxyType = rule.ProxyType == null ? "" : rule.ProxyType.Trim().ToLowerInvariant();
                if (proxyType != "direct" && proxyType != "socks5")
                {
                    return "MySQL TLS 中继模式的出口仅支持 直连(direct) 或 socks5";
                }
            }
            return null;
        }

        // 增/改/删/启停统一响应信封:成功 { ok:true, ... },失败 { ok:false, message } + 对应状态码,
        // 前端据 ok 字段判定结果(见 docs/api-contract.md)。
        private IResult CreateRule([FromBody] ForwardRule rule)
        {
            string error = ValidateRule(rule);
            if (error != null)
            {
                return Fail(StatusCodes.Status400BadRequest, error);
            }
            string conflictError;
            ForwardRule created = _ruleService.AddRule(rule, out conflictError);
            if (created == null)
            {
                return Fail(StatusCodes.Status409Conflict, conflictError);
            }
            return Results.Json(new { ok = true, rule = created });
        }

        private IResult UpdateRule(string id, [FromBody] ForwardRule body)
        {
            string error = ValidateRule(body);
            if (error != null)
            {
                return Fail(StatusCodes.Status400BadRequest, error);
            }
            if (_ruleService.GetRule(id) == null)
            {
                return Fail(StatusCodes.Status404NotFound, "规则不存在");
            }
            if (_ruleService.IsRunning(id))
            {
                return Fail(StatusCodes.Status409Conflict, "规则运行中,请先停止再编辑");
            }
            string conflictError;
            bool updated = _ruleService.UpdateRule(id, body, out conflictError);
            if (!updated)
            {
                // conflictError 非空为监听冲突;为空表示规则在校验后被并发删除
                if (conflictError != null)
                {
                    return Fail(StatusCodes.Status409Conflict, conflictError);
                }
                return Fail(StatusCodes.Status404NotFound, "规则不存在");
            }
            return Results.Json(new { ok = true, rule = _ruleService.GetRule(id) });
        }

        private IResult DeleteRule(string id)
        {
            bool removed = _ruleService.DeleteRule(id);
            if (!removed)
            {
                return Fail(StatusCodes.Status404NotFound, "规则不存在");
            }
            return Results.Json(new { ok = true });
        }

        private IResult StartRule(string id)
        {
            bool ok = _ruleService.StartRule(id);
            if (!ok)
            {
                return Fail(StatusCodes.Status404NotFound, "规则不存在");
            }
            return Results.Json(new { ok = true, running = _ruleService.IsRunning(id) });
        }

        private IResult StopRule(string id)
        {
            // 与 start/status 对齐:先判存在,不存在返回 404(此前对任意 id 都回 ok:true,前端无从区分规则已被删)
            if (_ruleService.GetRule(id) == null)
            {
                return Fail(StatusCodes.Status404NotFound, "规则不存在");
            }
            _ruleService.StopRule(id);
            return Results.Json(new { ok = true, running = _ruleService.IsRunning(id) });
        }

        // 全部启动/停止:后端一次完成并返回统计,前端不再依赖本地运行状态快照逐条调用
        // (快照过期时会漏启/漏停,「全部启动后立刻全部停止」曾因此报「没有运行中的规则」)。
        private IResult StartAllRules()
        {
            _ruleService.StartAll();
            int total;
            int running = _ruleService.CountRunning(out total);
            return Results.Json(new { ok = true, total = total, running = running });
        }

        private IResult StopAllRules()
        {
            _ruleService.StopAll();
            int total;
            int running = _ruleService.CountRunning(out total);
            return Results.Json(new { ok = true, total = total, running = running });
        }

        private IResult GetRuleStatus(string id)
        {
            // 不存在返回 404:前端状态轮询据此发现「规则已在别的页面被删」并重拉列表
            if (_ruleService.GetRule(id) == null)
            {
                return Fail(StatusCodes.Status404NotFound, "规则不存在");
            }
            return Results.Json(new { id = id, running = _ruleService.IsRunning(id) });
        }

        private static IResult Fail(int statusCode, string message)
        {
            return Results.Json(new { ok = false, message = message }, statusCode: statusCode);
        }

        private IResult GetRuleLogs(string id, [FromQuery] long afterSeq)
        {
            List<LogEntry> entries = _ruleService.GetLogs(id, afterSeq);
            return Results.Json(entries);
        }

        private async Task<IResult> TestProxy([FromBody] TestProxyDto dto)
        {
            if (dto == null)
            {
                return Fail(StatusCodes.Status400BadRequest, "请求体不能为空");
            }
            ConnectionTestResult result = await _tester.TestTcpAsync(dto.Host, dto.Port, dto.TimeoutMs);
            return Results.Json(result);
        }

        private async Task<IResult> TestTarget([FromBody] TestTargetDto dto)
        {
            if (dto == null)
            {
                return Fail(StatusCodes.Status400BadRequest, "请求体不能为空");
            }
            ConnectionTestResult result = await _tester.TestThroughSocks5Async(dto.ProxyHost, dto.ProxyPort, dto.TargetHost, dto.TargetPort, dto.TimeoutMs);
            return Results.Json(result);
        }

        private async Task<IResult> TestListener([FromBody] TestListenerDto dto)
        {
            if (dto == null)
            {
                return Fail(StatusCodes.Status400BadRequest, "请求体不能为空");
            }
            // 规则可能监听非回环地址(如具体网卡 IP),按传入地址探测;
            // 省略或 0.0.0.0(全网卡)时用回环地址,避免直连 0.0.0.0 误报
            string host = dto.Host;
            if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0")
            {
                host = "127.0.0.1";
            }
            ConnectionTestResult result = await _tester.TestTcpAsync(host, dto.Port, dto.TimeoutMs);
            return Results.Json(result);
        }
    }
}
