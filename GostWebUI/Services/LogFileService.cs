using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace GostWebUI.Services
{
    // gost 运行日志的文件落盘:按天滚动(gost-yyyyMMdd.log),文件日期超过保留天数自动删除。
    // 网页上的实时日志仍走 ForwardRuleService 的内存环形缓冲,本服务只负责持久化归档。
    // 线程安全:所有状态由自持的 _gate 保护;锁内不回调任何外部组件,
    // 与 ForwardRuleService._gate 无嵌套关系,不新增死锁面。
    public class LogFileService : IDisposable
    {
        private const string FilePrefix = "gost-";
        private const string FileSuffix = ".log";
        private const string FileDateFormat = "yyyyMMdd";

        private readonly object _gate;
        private string _directory;        // 生效日志目录(绝对路径)
        private int _retentionDays;       // 0 = 永久保留(不清理)
        private StreamWriter _writer;     // 当天日志文件的写入器(懒打开;写失败时丢弃待重试)
        private DateTime _writerDate;     // _writer 对应的本地日期,用于跨天切换

        public LogFileService(string directory, int retentionDays)
        {
            _gate = new object();
            _directory = directory;
            _retentionDays = retentionDays;
            _writer = null;
            _writerDate = DateTime.MinValue;
            lock (_gate)
            {
                CleanupExpired();
            }
        }

        // 追加一行日志(gost 输出回调线程调用,调用方不持任何锁)。
        // 任何 IO 失败都吞掉并丢弃写入器,下一行日志时重试——日志落盘绝不能反过来拖垮转发功能。
        public void Append(string ruleName, string text)
        {
            lock (_gate)
            {
                DateTime now = DateTime.Now;
                try
                {
                    EnsureWriter(now.Date);
                    if (_writer != null)
                    {
                        _writer.WriteLine(now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + ruleName + "] " + text);
                    }
                }
                catch (Exception)
                {
                    DropWriter();
                }
            }
        }

        // 应用新的目录 / 保留天数(网页「设置」保存后调用):
        // 目录变化时关闭当前写入器,后续日志写入新位置(旧文件原样保留);随后按新天数立即清理一次。
        public void UpdateSettings(string directory, int retentionDays)
        {
            lock (_gate)
            {
                if (!string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    DropWriter();
                    _directory = directory;
                }
                _retentionDays = retentionDays;
                CleanupExpired();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                DropWriter();
            }
        }

        // 保证 _writer 指向 date 当天的日志文件:首次写入或跨天时(重)打开,跨天顺带清理过期文件。
        // 打开失败向上抛,由 Append 统一吞掉。调用方持 _gate。
        private void EnsureWriter(DateTime date)
        {
            if (_writer != null && _writerDate == date)
            {
                return;
            }
            bool crossedDay = _writer != null;
            DropWriter();
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, FilePrefix + date.ToString(FileDateFormat) + FileSuffix);
            // FileShare.ReadWrite:允许用户用 tail / 编辑器同时打开查看
            FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.AutoFlush = true;
            _writer = writer;
            _writerDate = date;
            if (crossedDay)
            {
                CleanupExpired();
            }
        }

        private void DropWriter()
        {
            if (_writer != null)
            {
                try
                {
                    _writer.Dispose();
                }
                catch (Exception)
                {
                }
                _writer = null;
            }
            _writerDate = DateTime.MinValue;
        }

        // 删除超过保留天数的日志文件。只认本服务的命名模式(gost-yyyyMMdd.log),
        // 且按文件名里的日期判断(不依赖文件系统时间戳,拷贝/恢复不影响判定),
        // 绝不触碰目录里的其他文件。当天文件差值为 0,任何正的保留天数下都不会被删。调用方持 _gate。
        private void CleanupExpired()
        {
            if (_retentionDays <= 0)
            {
                return;
            }
            try
            {
                if (!Directory.Exists(_directory))
                {
                    return;
                }
                DateTime today = DateTime.Now.Date;
                string[] files = Directory.GetFiles(_directory, FilePrefix + "*" + FileSuffix);
                foreach (string file in files)
                {
                    DateTime fileDate;
                    if (!TryParseFileDate(Path.GetFileName(file), out fileDate))
                    {
                        continue;
                    }
                    if ((today - fileDate).TotalDays >= _retentionDays)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception)
                        {
                            // 被占用等:跳过,下次清理再试
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 目录枚举失败:静默,清理属尽力而为
            }
        }

        private static bool TryParseFileDate(string fileName, out DateTime date)
        {
            date = DateTime.MinValue;
            if (fileName == null ||
                !fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string middle = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileSuffix.Length);
            return DateTime.TryParseExact(middle, FileDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}
