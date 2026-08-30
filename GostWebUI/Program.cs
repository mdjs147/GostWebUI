using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using PortForwarder.Core;
using PortForwarder.Models;
using PortForwarder.Services;
using PortForwarder.Web;

namespace PortForwarder
{
    internal static class Program
    {
        // 单实例互斥体名:Local\ 前缀,作用于当前登录会话。
#if DEBUG
        // 开发构建:用独立互斥体名,避免和已安装的正式版单实例互斥(否则调试实例会检测到「已运行」直接退出)
        private const string MutexName = @"Local\GostWebUI-SingleInstance-Dev";
#else
        private const string MutexName = @"Local\GostWebUI-SingleInstance";
#endif

        [STAThread]
        private static void Main()
        {
            bool createdNew = false;
            Mutex mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 已有实例在运行:尝试打开其配置页后退出,不重复起托盘。
                TryOpenExistingInstance();
                return;
            }

            ForwardRuleService ruleService = null;
            ApiServer apiServer = null;
            LogFileService logFile = null;
            try
            {
                // 手动组合服务(局部 DI:仅在 ApiServer 内部构建 Kestrel)
                ConfigService configService = new ConfigService();
                configService.Load();
#if DEBUG
                // 开发构建:强制用开发默认端口(无视 config.json),和正式版端口彻底隔离。
                // 之后保存配置时该端口会随之落盘到 Debug 输出目录的 config.json,无碍:
                // 下次启动仍会强制覆盖,且与正式版目录互不相干。
                configService.Config.WebPort = AppConfig.DefaultWebPort;
#endif

                // 日志文件归档:按天滚动写入日志目录,构造时顺带清理一次过期文件
                logFile = new LogFileService(configService.ResolveLogDirectory(), configService.Config.LogRetentionDays);
                ruleService = new ForwardRuleService(configService, logFile);
                Socks5Tester tester = new Socks5Tester();
                StartupService startupService = new StartupService();

                // 项目更名(MyPortForwarder → GostWebUI)后的一次性注册值迁移
                startupService.MigrateLegacyValueName();
                // 已启用开机启动时刷新注册路径(exe 被移动/升级后自动指向新位置)
                startupService.RefreshPathIfEnabled();

                // 非阻塞启动内嵌 Web
                apiServer = new ApiServer(configService, ruleService, tester, startupService);
                apiServer.Start();

                // 启动时自动运行勾选了 AutoStart 的规则
                ruleService.StartAutoStartRules();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

                // 进入托盘消息循环(阻塞主线程直到「退出」)
                // 首次运行(config.json 不存在)时,托盘会自动打开配置页并弹气泡引导
                TrayService tray = new TrayService(ruleService, configService.Config.WebPort, startupService, configService.IsFirstRun);
                Application.Run(tray);
            }
            catch (Exception ex)
            {
                // 启动阶段未处理异常(最常见:Web 端口被其他程序占用导致 Kestrel 绑定失败)。
                // 明确弹窗提示后正常退出,不让进程无声崩溃或只留下系统崩溃对话框。
                ShowStartupError(ex);
            }
            finally
            {
                // 收尾放在 finally:Application.Run 之前/期间抛出未处理异常时也能停掉 gost 与 Web;
                // finally 都来不及执行的场景(强杀/崩溃)由 ChildProcessJob 的内核 kill-on-close 兜底
                Shutdown(ruleService, apiServer, logFile);
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }

        // 启动失败弹窗:识别端口占用给出针对性指引,其余显示原始异常信息。
        private static void ShowStartupError(Exception ex)
        {
            string message;
            if (IsAddressInUse(ex))
            {
                message = "启动失败:Web 管理端口被其他程序占用。\n\n请关闭占用端口的程序,或修改 exe 同目录 config.json 中的 WebPort 后重新启动。\n\n详细信息:" + ex.Message;
            }
            else
            {
                message = "启动失败:" + ex.Message;
            }
            MessageBox.Show(message, "GostWebUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // 判断异常链中是否为「地址/端口已被占用」:Kestrel 绑定失败抛 AddressInUseException,
        // 内层通常包着 SocketException(AddressAlreadyInUse)。
        private static bool IsAddressInUse(Exception ex)
        {
            Exception current = ex;
            while (current != null)
            {
                SocketException socketEx = current as SocketException;
                if (socketEx != null && socketEx.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    return true;
                }
                if (current.GetType().Name == "AddressInUseException")
                {
                    return true;
                }
                current = current.InnerException;
            }
            return false;
        }

        // 退出收尾:先停掉所有 gost(其「进程已退出」日志仍能落盘),再关内嵌 Web,最后关日志文件。
        // 任一步失败都吞掉,不让收尾异常盖掉原始异常或阻断 Mutex 释放;残留进程由 Job Object 兜底。
        private static void Shutdown(ForwardRuleService ruleService, ApiServer apiServer, LogFileService logFile)
        {
            try
            {
                if (ruleService != null)
                {
                    ruleService.StopAll();
                }
            }
            catch (Exception)
            {
                // 忽略:进程即将退出
            }

            try
            {
                if (apiServer != null)
                {
                    apiServer.Stop();
                }
            }
            catch (Exception)
            {
                // 忽略:Kestrel 全部为后台线程,进程退出时随 CLR 一并结束
            }

            try
            {
                if (logFile != null)
                {
                    logFile.Dispose();
                }
            }
            catch (Exception)
            {
                // 忽略:进程即将退出
            }
        }

        // 第二个实例:打开已运行实例的配置页,然后退出。
        private static void TryOpenExistingInstance()
        {
            int webPort = AppConfig.DefaultWebPort;
#if !DEBUG
            // 正式版:端口可能被用户在网页里改过,从 config.json 读取。
            // 开发版端口固定为 DefaultWebPort(Main 里强制覆盖,config.json 里的值无效),直接用常量。
            try
            {
                ConfigService configService = new ConfigService();
                configService.Load();
                webPort = configService.Config.WebPort;
            }
            catch (Exception)
            {
                webPort = AppConfig.DefaultWebPort;
            }
#endif

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "http://127.0.0.1:" + webPort.ToString();
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception)
            {
                // 打开失败静默:主实例仍在运行,用户可自行从托盘打开
            }
        }
    }
}
