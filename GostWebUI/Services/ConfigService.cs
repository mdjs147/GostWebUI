using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PortForwarder.Models;

namespace PortForwarder.Services
{
    // 配置与规则的读写服务。启动时 Load,变更后 Save / SaveRules;持有当前配置与规则列表。
    // config.json 固定在 exe 同目录(启动锚点,默认 PascalCase,仅本进程读写);
    // 规则本体单独存 RulesPath 指向的 JSON 文件(默认 exe 同目录 rules.json),位置可在网页「设置」修改。
    public class ConfigService
    {
        private const string LegacyFileName = "portforwarder.config.json";
        private const string DefaultRulesFileName = "rules.json";
        private const string DefaultLogDirectoryName = "logs";

        private readonly string _configPath;
        private readonly object _saveGate;
        private AppConfig _config;
        private List<ForwardRule> _rules;
        private bool _isFirstRun;

        public ConfigService()
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            _saveGate = new object();
            _config = new AppConfig();
            _rules = new List<ForwardRule>();
            _isFirstRun = false;
        }

        public AppConfig Config
        {
            get
            {
                return _config;
            }
        }

        // 当前规则列表(运行时唯一实例):从规则文件载入,增删改后由调用方 SaveRules 落盘。
        // 并发访问由 ForwardRuleService._gate 串行化,此处不再加锁。
        public List<ForwardRule> Rules
        {
            get
            {
                return _rules;
            }
        }

        public string ConfigPath
        {
            get
            {
                return _configPath;
            }
        }

        // 首次运行标志:Load 时 config.json 与旧名配置文件均不存在(程序第一次在此目录启动)。
        // 供 Program / TrayService 决定是否弹出初次引导(自动打开配置页 + 托盘气泡)。
        // Load 内已在首次运行时固化 config.json,保证该引导只在真正的第一次启动触发一次。
        public bool IsFirstRun
        {
            get
            {
                return _isFirstRun;
            }
        }

        // 规则文件的生效绝对路径:RulesPath 空 = exe 同目录 rules.json,相对路径锚定程序目录
        public string ResolveRulesPath()
        {
            return ResolveAgainstBaseDirectory(_config.RulesPath, DefaultRulesFileName);
        }

        // 日志目录的生效绝对路径:LogDirectory 空 = exe 同目录 logs 子目录
        public string ResolveLogDirectory()
        {
            return ResolveAgainstBaseDirectory(_config.LogDirectory, DefaultLogDirectoryName);
        }

        public AppConfig Load()
        {
            // 必须在读取 / 落盘之前捕获:一旦 Save 写出 config.json,就不再是首次运行
            _isFirstRun = DetectFirstRun();
            bool saveConfig = LoadConfigDocument();
            bool migratedRules = LoadRulesDocument();
            if (migratedRules)
            {
                // 旧 config.json 内嵌的 Rules 已并入规则文件:两处一并落盘固化,config.json 不再含规则
                SaveRules();
                saveConfig = true;
            }
            if (_isFirstRun)
            {
                // 首次运行:立即固化 config.json(即便用户未改任何设置),
                // 让初次引导只在真正的第一次启动触发,之后 config.json 已存在便不再判为首次。
                saveConfig = true;
            }
            if (saveConfig)
            {
                Save();
            }
            return _config;
        }

        // 判断是否首次运行:config.json 与旧名配置文件均不存在。
        // 必须在 LoadConfigDocument / Save 之前调用——任一路径落盘 config.json 后就不再是首次。
        private bool DetectFirstRun()
        {
            if (File.Exists(_configPath))
            {
                return false;
            }
            string legacyPath = Path.Combine(AppContext.BaseDirectory, LegacyFileName);
            if (File.Exists(legacyPath))
            {
                return false;
            }
            return true;
        }

        public void Save()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            // 防并发写坏文件:调用方一般已在 ForwardRuleService._gate 内,这里自持一把锁兜底裸调路径
            lock (_saveGate)
            {
                string json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(_configPath, json);
            }
        }

        // 规则列表落盘(顶层数组,PascalCase)。目录被用户删除时先补建,避免保存失败。
        public void SaveRules()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            lock (_saveGate)
            {
                string rulesPath = ResolveRulesPath();
                EnsureParentDirectory(rulesPath);
                string json = JsonSerializer.Serialize(_rules, options);
                File.WriteAllText(rulesPath, json);
            }
        }

        // 变更规则文件存储路径:把当前规则写入新位置,成功后更新 RulesPath 并保存 config.json;
        // 旧文件原样保留(作为备份)。目标已存在时拒绝——绝不覆盖、也不隐式切换到未知规则集。
        // 返回 null 成功,否则为可直接展示的错误信息。调用方(ForwardRuleService)负责持锁串行化。
        public string ChangeRulesPath(string newPath)
        {
            string resolved;
            try
            {
                resolved = ResolveAgainstBaseDirectory(newPath, DefaultRulesFileName);
            }
            catch (Exception ex)
            {
                return "规则文件路径无效:" + ex.Message;
            }

            // 空输入存 null(跟随程序目录,可移植);显式路径存解析后的绝对值,回显无歧义
            string storedValue = string.IsNullOrWhiteSpace(newPath) ? null : resolved;

            string current = ResolveRulesPath();
            if (string.Equals(resolved, current, StringComparison.OrdinalIgnoreCase))
            {
                _config.RulesPath = storedValue;
                Save();
                return null;
            }

            if (Directory.Exists(resolved))
            {
                return "规则文件路径指向一个已存在的目录:" + resolved;
            }
            if (File.Exists(resolved))
            {
                return "目标文件已存在(" + resolved + "),为防覆盖已拒绝;请先移走该文件或换一个路径";
            }

            try
            {
                EnsureParentDirectory(resolved);
                JsonSerializerOptions options = new JsonSerializerOptions();
                options.WriteIndented = true;
                lock (_saveGate)
                {
                    string json = JsonSerializer.Serialize(_rules, options);
                    File.WriteAllText(resolved, json);
                }
            }
            catch (Exception ex)
            {
                return "写入新规则文件失败:" + ex.Message;
            }

            _config.RulesPath = storedValue;
            Save();
            return null;
        }

        // 变更日志目录:立即尝试创建以尽早暴露无效路径,成功后更新配置并落盘。
        // 旧目录及其日志文件原样保留;新日志由 LogFileService 写入新目录。返回 null 成功。
        public string ChangeLogDirectory(string newDirectory)
        {
            string resolved;
            try
            {
                resolved = ResolveAgainstBaseDirectory(newDirectory, DefaultLogDirectoryName);
            }
            catch (Exception ex)
            {
                return "日志目录路径无效:" + ex.Message;
            }

            if (File.Exists(resolved))
            {
                return "日志目录指向一个已存在的文件:" + resolved;
            }

            try
            {
                Directory.CreateDirectory(resolved);
            }
            catch (Exception ex)
            {
                return "创建日志目录失败:" + ex.Message;
            }

            _config.LogDirectory = string.IsNullOrWhiteSpace(newDirectory) ? null : resolved;
            Save();
            return null;
        }

        // 读 config.json(或旧名文件)到 _config 并规范化;返回是否需要落盘(旧文件名 / 旧默认端口迁移)
        private bool LoadConfigDocument()
        {
            string sourcePath = _configPath;

            // 向后兼容:新配置文件不存在但存在旧名文件时,从旧文件读入并迁移到新文件名。
            if (!File.Exists(sourcePath))
            {
                string legacyPath = Path.Combine(AppContext.BaseDirectory, LegacyFileName);
                if (File.Exists(legacyPath))
                {
                    sourcePath = legacyPath;
                }
                else
                {
                    _config = new AppConfig();
                    return false;
                }
            }

            bool needsSave = sourcePath != _configPath;
            try
            {
                string json = File.ReadAllText(sourcePath);
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config == null)
                {
                    _config = new AppConfig();
                    return false;
                }
                if (string.IsNullOrWhiteSpace(config.GostPath))
                {
                    config.GostPath = "gost.exe";
                }
                if (config.WebPort == AppConfig.LegacyDefaultWebPort)
                {
                    // 旧默认端口 18011 一次性迁移到当前默认端口;用户显式改过的其他端口保持不动
                    config.WebPort = AppConfig.DefaultWebPort;
                    needsSave = true;
                }
                if (config.WebPort <= 0 || config.WebPort > 65535)
                {
                    config.WebPort = AppConfig.DefaultWebPort;
                }
                if (config.LogRetentionDays < 0 || config.LogRetentionDays > AppConfig.MaxLogRetentionDays)
                {
                    // 手改文件越界时回落默认;0 合法(永久保留)
                    config.LogRetentionDays = AppConfig.DefaultLogRetentionDays;
                }
                _config = config;
                return needsSave;
            }
            catch (Exception)
            {
                _config = new AppConfig();
                return false;
            }
        }

        // 读规则文件到 _rules,并把旧 config.json 内嵌的 Rules 一次性并入(按 Id 去重,规则文件优先)。
        // 返回是否发生迁移(调用方据此把规则文件与 config.json 一并落盘)。
        // 规则文件存在但解析失败时,先复制备份为 .bad-时间戳 再按空列表继续——绝不让后续保存静默覆盖用户数据。
        private bool LoadRulesDocument()
        {
            List<ForwardRule> rules = new List<ForwardRule>();
            try
            {
                string rulesPath = ResolveRulesPath();
                if (File.Exists(rulesPath))
                {
                    try
                    {
                        string json = File.ReadAllText(rulesPath);
                        List<ForwardRule> parsed = JsonSerializer.Deserialize<List<ForwardRule>>(json);
                        if (parsed != null)
                        {
                            rules = parsed;
                        }
                    }
                    catch (Exception)
                    {
                        TryBackupCorruptRulesFile(rulesPath);
                    }
                }
            }
            catch (Exception)
            {
                // RulesPath 非法(手改配置)导致解析路径失败:按空规则继续,保存时会走同样路径再报错
            }

            bool migrated = false;
            if (_config.Rules != null)
            {
                foreach (ForwardRule legacy in _config.Rules)
                {
                    if (FindById(rules, legacy.Id) == null)
                    {
                        rules.Add(legacy);
                    }
                }
                _config.Rules = null;
                migrated = true;
            }

            // 兼容旧数据:补齐缺失的 Id
            foreach (ForwardRule rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    rule.Id = Guid.NewGuid().ToString("N");
                }
            }

            _rules = rules;
            return migrated;
        }

        private static ForwardRule FindById(List<ForwardRule> rules, string id)
        {
            foreach (ForwardRule rule in rules)
            {
                if (rule.Id == id)
                {
                    return rule;
                }
            }
            return null;
        }

        // 把损坏的规则文件复制为 rules.json.bad-yyyyMMddHHmmss,保住原数据供手工恢复;
        // 备份失败(被占用等)时原样保留,由用户自行处置
        private static void TryBackupCorruptRulesFile(string rulesPath)
        {
            try
            {
                string backupPath = rulesPath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(rulesPath, backupPath, true);
            }
            catch (Exception)
            {
            }
        }

        private static void EnsureParentDirectory(string filePath)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        // 路径解析:空白回退默认名,相对路径锚定程序目录(与 GostProcessManager.ResolveGostPath 同纪律)
        private static string ResolveAgainstBaseDirectory(string configured, string defaultName)
        {
            string path = configured;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = defaultName;
            }
            path = path.Trim();
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, path);
            }
            return Path.GetFullPath(path);
        }
    }
}
