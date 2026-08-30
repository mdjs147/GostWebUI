using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GostWebUI.Models;
using GostWebUI.Services;

namespace GostWebUI.Core
{
    // 多规则的 gost 进程注册表 + 每规则日志环形缓冲。作为 API / 托盘的服务层。
    // 依赖 ConfigService 取配置与落盘、LogFileService 做日志文件归档;
    // 线程安全:对内部字典/缓冲的访问都在 _gate 锁内。
    public class ForwardRuleService
    {
        private const int MaxLogLinesPerRule = 500;

        private readonly ConfigService _configService;
        private readonly LogFileService _logFile;
        private readonly Dictionary<string, IForwardManager> _managers;
        private readonly Dictionary<string, LinkedList<LogEntry>> _logs;
        private readonly object _gate;
        private long _seqCounter;

        public ForwardRuleService(ConfigService configService, LogFileService logFile)
        {
            _configService = configService;
            _logFile = logFile;
            _managers = new Dictionary<string, IForwardManager>();
            _logs = new Dictionary<string, LinkedList<LogEntry>>();
            _gate = new object();
            _seqCounter = 0;
        }

        // ===== 全局配置 =====

        // 更新全局配置(gost 路径 / Web 端口 / 规则文件路径 / 日志目录 / 日志保留天数)并落盘。
        // 放在 _gate 内执行,与规则增删的「改集合 + SaveRules」串行化,避免迁移规则文件时集合被并发改动。
        // 各参数 null 表示不改(webPort 传 0 或越界表示不改);改 Web 端口需重启进程才生效。
        // 返回 null 成功;返回字符串为可直接展示的失败原因(规则文件迁移被拒 / 日志目录无效等),
        // 失败字段之前的字段已生效,调用方提示后用户改正重提交即可(操作幂等)。
        public string UpdateGlobalConfig(string gostPath, int webPort, string rulesPath, string logDirectory, int? logRetentionDays)
        {
            lock (_gate)
            {
                if (gostPath != null)
                {
                    _configService.Config.GostPath = gostPath;
                }
                if (webPort > 0 && webPort <= 65535)
                {
                    _configService.Config.WebPort = webPort;
                }
                if (logRetentionDays.HasValue &&
                    logRetentionDays.Value >= 0 &&
                    logRetentionDays.Value <= AppConfig.MaxLogRetentionDays)
                {
                    _configService.Config.LogRetentionDays = logRetentionDays.Value;
                }
                _configService.Save();

                if (logDirectory != null)
                {
                    string error = _configService.ChangeLogDirectory(logDirectory);
                    if (error != null)
                    {
                        return error;
                    }
                }
                if (rulesPath != null)
                {
                    string error = _configService.ChangeRulesPath(rulesPath);
                    if (error != null)
                    {
                        return error;
                    }
                }

                // 目录或保留天数可能已变化:让日志落盘组件切到新设置并立即清理一次。
                // 锁序恒为 _gate → LogFileService._gate,后者锁内不回调外部,无反向嵌套。
                _logFile.UpdateSettings(_configService.ResolveLogDirectory(), _configService.Config.LogRetentionDays);
                return null;
            }
        }

        // ===== 规则 CRUD =====

        public List<ForwardRule> GetRules()
        {
            lock (_gate)
            {
                // 返回快照:活动列表可能被其他请求并发增删,直接交给 JSON 延迟序列化会枚举中途被改
                return new List<ForwardRule>(_configService.Rules);
            }
        }

        public ForwardRule GetRule(string id)
        {
            lock (_gate)
            {
                return FindRule(id);
            }
        }

        // 新增规则。监听地址端口与现有规则冲突时不添加,返回 null 并经 conflictError 给出可展示的原因;
        // 查重与入列同锁完成,并发 POST 相同监听时只有一条能成功。
        public ForwardRule AddRule(ForwardRule rule, out string conflictError)
        {
            lock (_gate)
            {
                ForwardRule conflict = FindListenConflict(rule, null);
                if (conflict != null)
                {
                    conflictError = DescribeListenConflict(conflict);
                    return null;
                }
                if (!IsSafeId(rule.Id) || FindRule(rule.Id) != null)
                {
                    // 空 Id、含特殊字符(会破坏 REST 路径与前端定位)或与现有规则撞 Id 时重新生成,
                    // 保证 Id 唯一且仅含安全字符;否则启停/删除按 Id 定位会失效
                    rule.Id = Guid.NewGuid().ToString("N");
                }
                _configService.Rules.Add(rule);
                _configService.SaveRules();
                conflictError = null;
                return rule;
            }
        }

        // 更新规则。返回 false 时:conflictError 非空为监听冲突,为空表示规则不存在(调用方据此区分 409/404)。
        public bool UpdateRule(string id, ForwardRule body, out string conflictError)
        {
            lock (_gate)
            {
                ForwardRule existing = FindRule(id);
                if (existing == null)
                {
                    conflictError = null;
                    return false;
                }
                ForwardRule conflict = FindListenConflict(body, id);
                if (conflict != null)
                {
                    conflictError = DescribeListenConflict(conflict);
                    return false;
                }
                existing.CopyEditableFrom(body);
                _configService.SaveRules();
                conflictError = null;
                return true;
            }
        }

        public bool DeleteRule(string id)
        {
            // 锁内摘除运行时与配置,锁外停进程(持 _gate 调 Stop 会与 Exited 回调 ABBA 死锁,见 StopRule)。
            // 先摘后停:摘除后新的启停请求不会再命中该 manager,进程本体随后由锁外 Stop 收掉。
            IForwardManager manager;
            lock (_gate)
            {
                ForwardRule existing = FindRule(id);
                if (existing == null)
                {
                    return false;
                }
                manager = DetachRuntime(id);
                _configService.Rules.Remove(existing);
                _configService.SaveRules();
            }
            if (manager != null)
            {
                manager.Stop();
            }
            return true;
        }

        // ===== 启停 =====

        public bool StartRule(string id)
        {
            lock (_gate)
            {
                ForwardRule rule = FindRule(id);
                if (rule == null)
                {
                    return false;
                }
                IForwardManager manager = GetOrCreate(rule);
                manager.Start();
                return true;
            }
        }

        public bool StopRule(string id)
        {
            // Stop 必须在 _gate 外调用:gost 退出瞬间,Exited 回调线程会「持 Process 内部锁 → AppendLog 等 _gate」,
            // 若本线程「持 _gate → Stop 内 Dispose 等 Process 内部锁」则互为 ABBA 死锁(实测栈证实)。
            // 与 StopAll 同纪律:锁内取引用,锁外停止;启停互斥由 manager 自身的锁保证。
            IForwardManager manager;
            lock (_gate)
            {
                if (!_managers.ContainsKey(id))
                {
                    return true;
                }
                manager = _managers[id];
            }
            manager.Stop();
            return true;
        }

        public void StartAutoStartRules()
        {
            lock (_gate)
            {
                foreach (ForwardRule rule in _configService.Rules)
                {
                    if (rule.AutoStart)
                    {
                        IForwardManager manager = GetOrCreate(rule);
                        manager.Start();
                    }
                }
            }
        }

        public void StartAll()
        {
            lock (_gate)
            {
                foreach (ForwardRule rule in _configService.Rules)
                {
                    IForwardManager manager = GetOrCreate(rule);
                    manager.Start();
                }
            }
        }

        public void StopAll()
        {
            // 先在锁内取快照,再在锁外并行停止:单个 gost 卡死最多拖 3 秒,不再串行累加,
            // 也避免停止期间长时间持 _gate 阻塞状态查询。每个 manager 自身有锁保证启停互斥。
            List<IForwardManager> managers;
            lock (_gate)
            {
                managers = new List<IForwardManager>(_managers.Values);
            }

            List<Task> tasks = new List<Task>();
            foreach (IForwardManager manager in managers)
            {
                IForwardManager target = manager;
                tasks.Add(Task.Run(delegate { target.Stop(); }));
            }
            Task.WaitAll(tasks.ToArray());
        }

        public bool IsRunning(string id)
        {
            lock (_gate)
            {
                if (!_managers.ContainsKey(id))
                {
                    return false;
                }
                return _managers[id].IsRunning;
            }
        }

        // 统计规则总数与运行中数量(锁内快照),供批量启停接口返回结果
        public int CountRunning(out int total)
        {
            lock (_gate)
            {
                total = _configService.Rules.Count;
                int running = 0;
                foreach (ForwardRule rule in _configService.Rules)
                {
                    if (_managers.ContainsKey(rule.Id) && _managers[rule.Id].IsRunning)
                    {
                        running = running + 1;
                    }
                }
                return running;
            }
        }

        // ===== 日志(环形缓冲 500 行 + afterSeq 增量拉取)=====

        // 返回序号大于 afterSeq 的日志(增量拉取);afterSeq=0 取当前缓冲全部。
        public List<LogEntry> GetLogs(string id, long afterSeq)
        {
            lock (_gate)
            {
                List<LogEntry> result = new List<LogEntry>();
                if (!_logs.ContainsKey(id))
                {
                    return result;
                }
                foreach (LogEntry entry in _logs[id])
                {
                    if (entry.Seq > afterSeq)
                    {
                        result.Add(entry);
                    }
                }
                return result;
            }
        }

        // ===== gost.exe 完整性(TOFU 指纹锁定) =====
        // 防同目录 gost.exe 被恶意替换:首次成功启动时锁定其 SHA-256,之后每次启动前比对,不一致拒启。
        // 边界要点:锁定值存在 config.json 里,能改文件的攻击者往往也能改锁定值——这是「提高门槛 + 可发现」,
        // 不是同账户内的硬安全边界;硬边界靠把程序目录放到仅管理员可写的位置。

        // 当前完整性状态:解析路径、现场读文件算指纹、与锁定值比对。供 API 查询展示。
        public GostIntegrityInfo GetGostIntegrity()
        {
            lock (_gate)
            {
                return BuildIntegrityInfo();
            }
        }

        // 信任当前 gost.exe:把锁定指纹更新为当前文件的实际指纹并落盘(用户在网页上确认升级/更换后调用)。
        // 文件缺失或不可读时不改动锁定值,返回的 Trusted 保持 false,由调用方提示。
        public GostIntegrityInfo TrustCurrentGost()
        {
            lock (_gate)
            {
                GostIntegrityInfo info = BuildIntegrityInfo();
                if (info.CurrentSha256 != null)
                {
                    _configService.Config.GostSha256 = info.CurrentSha256;
                    _configService.Save();
                    info.PinnedSha256 = info.CurrentSha256;
                    info.Trusted = true;
                }
                return info;
            }
        }

        // GostProcessManager 启动前的校验回调:此时文件已被拒写共享句柄锁住,actualSha256 即将启动内容的指纹。
        // 返回 null 放行;返回字符串为拒启原因。未锁定时首次信任并落盘(TOFU)。
        // 锁序说明:调用链恒为 StartRule/StartAll(持 _gate)→ manager.Start(持 manager._gate)→ 本回调,
        // 此处对 _gate 的加锁是同线程重入,不会阻塞;若日后出现绕过服务层直接调 manager.Start 的路径,
        // 会形成 manager._gate → _gate 的反向锁序,须先回到服务层入口。
        private string CheckGostIntegrity(string path, string actualSha256)
        {
            lock (_gate)
            {
                string pinned = _configService.Config.GostSha256;
                if (string.IsNullOrWhiteSpace(pinned))
                {
                    _configService.Config.GostSha256 = actualSha256;
                    _configService.Save();
                    return null;
                }
                if (string.Equals(pinned, actualSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return "gost.exe 与锁定指纹不一致(锁定 " + ShortHash(pinned) + ",当前 " + ShortHash(actualSha256) +
                       "),文件可能被替换;若是你主动更新的,请在网页「设置」中信任当前文件后重试";
            }
        }

        // 组装完整性状态(调用方持 _gate):路径解析失败/文件缺失/不可读时 CurrentSha256 为 null。
        private GostIntegrityInfo BuildIntegrityInfo()
        {
            GostIntegrityInfo info = new GostIntegrityInfo();
            if (!string.IsNullOrWhiteSpace(_configService.Config.GostSha256))
            {
                info.PinnedSha256 = _configService.Config.GostSha256;
            }
            try
            {
                info.GostPath = GostProcessManager.ResolveGostPath(_configService.Config.GostPath);
                info.FileExists = File.Exists(info.GostPath);
                if (info.FileExists)
                {
                    using (FileStream stream = new FileStream(info.GostPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        info.CurrentSha256 = GostProcessManager.ComputeSha256(stream);
                    }
                }
            }
            catch (Exception)
            {
                // 路径非法或文件存在但读不了:保持 CurrentSha256 为 null,前端按「不可读」展示
                if (info.GostPath == null)
                {
                    info.GostPath = _configService.Config.GostPath;
                }
            }
            info.Trusted = info.PinnedSha256 != null &&
                           info.CurrentSha256 != null &&
                           string.Equals(info.PinnedSha256, info.CurrentSha256, StringComparison.OrdinalIgnoreCase);
            return info;
        }

        private static string ShortHash(string hash)
        {
            if (hash == null || hash.Length <= 16)
            {
                return hash;
            }
            return hash.Substring(0, 16) + "…";
        }

        // ===== 私有 =====

        // 为某条规则拿到(或创建)对应的运行时管理器,按 Mode 分派两种实现。
        // gost 模式:GostProcessManager(拉子进程,需 gost 路径与完整性校验);
        // mysql 模式:MySqlRelayManager(进程内 MySQL TLS 中继,无 gost 依赖)。
        private IForwardManager GetOrCreate(ForwardRule rule)
        {
            if (_managers.ContainsKey(rule.Id))
            {
                // gost 子进程管理器需跟随最新配置路径;MySQL TLS 中继无 gost 依赖,无需处理
                if (_managers[rule.Id] is GostProcessManager existingGost)
                {
                    existingGost.GostPath = _configService.Config.GostPath;
                }
                return _managers[rule.Id];
            }

            IForwardManager manager;
            if (rule.IsMySqlRelay())
            {
                manager = new MySqlRelayManager(rule);
            }
            else
            {
                GostProcessManager gost = new GostProcessManager(_configService.Config.GostPath, rule);
                gost.IntegrityCheck = CheckGostIntegrity;
                manager = gost;
            }
            string ruleId = rule.Id;
            manager.LogReceived += delegate (string line)
            {
                AppendLog(ruleId, line);
                // 文件归档跟在内存缓冲之后、同一回调线程上:此刻不持 _gate,
                // LogFileService 自持锁且锁内不回调外部,与 _gate 无嵌套死锁面。
                // 规则名直接读当前值,改名后新日志用新名,短暂新旧混用无害。
                _logFile.Append(rule.Name, line);
            };
            _managers.Add(rule.Id, manager);
            return manager;
        }

        private ForwardRule FindRule(string id)
        {
            foreach (ForwardRule rule in _configService.Rules)
            {
                if (rule.Id == id)
                {
                    return rule;
                }
            }
            return null;
        }

        // 监听冲突检查(调用方持 _gate):同端口且监听地址互相覆盖即冲突,excludeId 用于更新时排除自身。
        // 前端 index.html 的 findListenConflict 与此语义一致,改动时两处同步。
        private ForwardRule FindListenConflict(ForwardRule candidate, string excludeId)
        {
            foreach (ForwardRule rule in _configService.Rules)
            {
                if (excludeId != null && rule.Id == excludeId)
                {
                    continue;
                }
                if (rule.ListenPort == candidate.ListenPort &&
                    ListenAddressOverlaps(rule.ListenAddress, candidate.ListenAddress))
                {
                    return rule;
                }
            }
            return null;
        }

        // 两个监听地址是否覆盖同一绑定:空白 / 0.0.0.0 / :: 表示绑定所有网卡(gost 语义),
        // 与任何地址都冲突;其余地址按不区分大小写精确比较。
        private static bool ListenAddressOverlaps(string a, string b)
        {
            return IsWildcardAddress(a) ||
                   IsWildcardAddress(b) ||
                   string.Equals(NormalizeAddress(a), NormalizeAddress(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWildcardAddress(string address)
        {
            string normalized = NormalizeAddress(address);
            return normalized.Length == 0 ||
                   normalized == "0.0.0.0" ||
                   normalized == "::" ||
                   normalized == "[::]";
        }

        private static string NormalizeAddress(string address)
        {
            if (address == null)
            {
                return "";
            }
            return address.Trim();
        }

        private static string DescribeListenConflict(ForwardRule conflict)
        {
            string address = NormalizeAddress(conflict.ListenAddress);
            if (address.Length == 0)
            {
                address = "0.0.0.0";
            }
            return "监听地址与规则「" + conflict.Name + "」冲突(" + address + ":" + conflict.ListenPort.ToString() + ")";
        }

        // Id 参与 REST 路径与前端 DOM 定位,只接受 1-64 位字母数字与短横线/下划线;
        // 其余(空值、含 / ? # 等)一律由服务端重新生成
        private static bool IsSafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                return false;
            }
            foreach (char c in id)
            {
                bool safe = (c >= '0' && c <= '9') ||
                            (c >= 'a' && c <= 'z') ||
                            (c >= 'A' && c <= 'Z') ||
                            c == '-' ||
                            c == '_';
                if (!safe)
                {
                    return false;
                }
            }
            return true;
        }

        // 删除规则前调用(调用方已持 _gate 锁):从注册表与日志缓冲摘除,返回待停止的管理器(无则 null)。
        // 不在此处停进程——必须由调用方在 _gate 外调用 Stop(锁内停会死锁,见 StopRule 注释)。
        private IForwardManager DetachRuntime(string id)
        {
            IForwardManager manager = null;
            if (_managers.ContainsKey(id))
            {
                manager = _managers[id];
                _managers.Remove(id);
            }
            if (_logs.ContainsKey(id))
            {
                _logs.Remove(id);
            }
            return manager;
        }

        private void AppendLog(string ruleId, string line)
        {
            lock (_gate)
            {
                if (!_logs.ContainsKey(ruleId))
                {
                    _logs[ruleId] = new LinkedList<LogEntry>();
                }
                LinkedList<LogEntry> buffer = _logs[ruleId];

                _seqCounter = _seqCounter + 1;
                LogEntry entry = new LogEntry();
                entry.Seq = _seqCounter;
                entry.RuleId = ruleId;
                entry.Time = DateTime.Now.ToString("HH:mm:ss");
                entry.Text = line;
                buffer.AddLast(entry);

                while (buffer.Count > MaxLogLinesPerRule)
                {
                    buffer.RemoveFirst();
                }
            }
        }
    }
}
