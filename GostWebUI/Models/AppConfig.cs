namespace GostWebUI.Models
{
    // 全局配置:Web 端口、gost 路径、存储位置(规则文件 / 日志目录 / 日志保留天数)。
    // 序列化到 exe 同目录 config.json(启动锚点,固定位置);规则本体单独存放于 RulesPath 指向的文件。
    public class AppConfig
    {
        // 默认 Web 端口:开发(Debug)与正式(Release)构建各用一个冷门端口,
        // 两个环境互不抢占,且避开常见服务端口与 Windows 临时端口段(49152+),降低冲突概率。
#if DEBUG
        public const int DefaultWebPort = 38517;
#else
        public const int DefaultWebPort = 31847;
#endif

        // 日志文件保留天数:默认一周;0 = 永久保留(不清理);上限防手滑输错
        public const int DefaultLogRetentionDays = 7;
        public const int MaxLogRetentionDays = 3650;

        public int WebPort { get; set; }
        public string GostPath { get; set; }
        // gost.exe 的锁定指纹(SHA-256 hex)。null/空 = 尚未锁定:首次成功启动时自动写入(TOFU),
        // 之后每次启动前比对,不一致则拒绝启动(防同目录文件被恶意替换)。用户升级 gost 后在网页「设置」里确认信任。
        public string GostSha256 { get; set; }
        // 规则文件路径:null/空 = exe 同目录 rules.json(跟随程序目录,可移植);
        // 网页「设置」修改后存解析出的绝对路径。规则不再内嵌本文件。
        public string RulesPath { get; set; }
        // 日志目录:null/空 = exe 同目录 logs 子目录;gost 运行日志按天写入其中的 gost-yyyyMMdd.log
        public string LogDirectory { get; set; }
        // 日志文件最大保留天数,文件日期超期自动删除;0 = 永久保留。
        public int LogRetentionDays { get; set; }

        public AppConfig()
        {
            WebPort = DefaultWebPort;
            GostPath = "gost.exe";
            GostSha256 = null;
            RulesPath = null;
            LogDirectory = null;
            LogRetentionDays = DefaultLogRetentionDays;
        }
    }
}
