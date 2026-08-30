using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PortForwarder.Models;

namespace PortForwarder.Core
{
    // 负责单条规则对应的 gost 子进程:启动、停止、捕获 stdout/stderr。
    // 实现 IForwardManager,与进程内的 MySqlRelayManager 一同被 ForwardRuleService 统一调度。
    public class GostProcessManager : IForwardManager
    {
        private Process _process;
        private readonly ForwardRule _rule;
        private readonly object _gate;

        public string GostPath { get; set; }

        // 完整性校验钩子(由服务层注入):参数为(解析后的绝对路径, 文件实际 SHA-256),
        // 返回 null 放行,返回非 null 字符串则拒绝启动并把该字符串写入规则日志。
        // 调用时机在「已用拒写共享句柄锁住文件」之后,校验结论与实际启动的文件内容一致。
        public Func<string, string, string> IntegrityCheck { get; set; }

        // 每输出一行日志触发一次(后台线程回调,订阅方需自行处理线程切换)
        public event Action<string> LogReceived;
        public event Action StateChanged;

        // 把配置的 gost 路径解析为绝对路径:空值回退默认名,相对路径锚定到程序目录。
        // 绝不把相对路径交给 CreateProcess:其搜索顺序含当前工作目录与 PATH,
        // 本地 gost.exe 缺失时会静默命中别处的同名文件(binary planting 劫持面)。
        public static string ResolveGostPath(string configuredPath)
        {
            string path = configuredPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "gost.exe";
            }
            path = path.Trim();
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, path);
            }
            return Path.GetFullPath(path);
        }

        // 从已打开的流计算 SHA-256(大写 hex)。调用方负责流的生命周期。
        public static string ComputeSha256(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
        }

        public GostProcessManager(string gostPath, ForwardRule rule)
        {
            GostPath = gostPath;
            _rule = rule;
            _process = null;
            _gate = new object();
        }

        public ForwardRule Rule
        {
            get
            {
                return _rule;
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    if (_process == null)
                    {
                        return false;
                    }
                    try
                    {
                        return !_process.HasExited;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_process != null)
                {
                    if (!SafeHasExited())
                    {
                        return;
                    }
                    // 旧进程已自行退出:释放旧 Process 对象(句柄)再拉起新进程
                    _process.Dispose();
                    _process = null;
                }

                // 完整性护栏:解析绝对路径 → 以「拒绝写/删除共享」的只读句柄打开文件 → 从该句柄算 SHA-256。
                // 句柄保持打开直到 CreateProcess 完成,封死「校验之后、启动之前」文件被替换的窗口(TOCTOU)。
                string resolvedPath = null;
                FileStream integrityGuard = null;
                string actualSha256 = null;
                try
                {
                    resolvedPath = ResolveGostPath(GostPath);
                    integrityGuard = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    actualSha256 = ComputeSha256(integrityGuard);
                }
                catch (Exception ex)
                {
                    if (integrityGuard != null)
                    {
                        integrityGuard.Dispose();
                    }
                    string shownPath = resolvedPath;
                    if (shownPath == null)
                    {
                        shownPath = GostPath;
                    }
                    RaiseLog("启动失败: 无法读取 gost.exe(" + shownPath + "):" + ex.Message + ";请下载 gost 放到程序目录,或在网页「设置」中指定完整路径");
                    return;
                }

                try
                {
                    if (IntegrityCheck != null)
                    {
                        string denyReason = IntegrityCheck(resolvedPath, actualSha256);
                        if (denyReason != null)
                        {
                            RaiseLog("已拒绝启动: " + denyReason);
                            return;
                        }
                    }

                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = resolvedPath;
                    startInfo.UseShellExecute = false;
                    startInfo.CreateNoWindow = true;
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                    startInfo.StandardOutputEncoding = Encoding.UTF8;
                    startInfo.StandardErrorEncoding = Encoding.UTF8;

                    foreach (string arg in _rule.BuildGostArguments())
                    {
                        startInfo.ArgumentList.Add(arg);
                    }

                    RaiseLog("启动: " + resolvedPath + " " + string.Join(" ", _rule.BuildGostArguments()));

                    Process p = new Process();
                    p.StartInfo = startInfo;
                    p.EnableRaisingEvents = true;
                    p.OutputDataReceived += OnOutputDataReceived;
                    p.ErrorDataReceived += OnErrorDataReceived;
                    p.Exited += OnProcessExited;

                    try
                    {
                        p.Start();
                        // 纳入 kill-on-close Job:主程序崩溃/被强杀时由内核终止 gost,防孤儿进程占住端口
                        if (!ChildProcessJob.TryAssign(p))
                        {
                            RaiseLog("警告: 加入 Job Object 失败,主程序异常退出时该 gost 进程可能残留");
                        }
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        _process = p;
                    }
                    catch (Exception ex)
                    {
                        RaiseLog("启动失败: " + ex.Message);
                        p.Dispose();
                        _process = null;
                    }
                }
                finally
                {
                    // 进程已把文件作为映像打开(或启动失败无需再护),护栏句柄使命完成
                    integrityGuard.Dispose();
                }
            }

            RaiseStateChanged();
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_process == null)
                {
                    return;
                }
                try
                {
                    if (!SafeHasExited())
                    {
                        _process.Kill(true);
                        _process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    RaiseLog("停止异常: " + ex.Message);
                }
                finally
                {
                    _process.Dispose();
                    _process = null;
                }
            }

            RaiseStateChanged();
        }

        private bool SafeHasExited()
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                RaiseLog(e.Data);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                RaiseLog(e.Data);
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            RaiseLog("进程已退出");
            RaiseStateChanged();
        }

        private void RaiseLog(string message)
        {
            Action<string> handler = LogReceived;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
