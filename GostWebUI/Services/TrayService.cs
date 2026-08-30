using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GostWebUI.Core;

namespace GostWebUI.Services
{
    // 托盘上下文:承载 NotifyIcon 与右键菜单。程序主线程运行本上下文的消息循环。
    // 关闭浏览器不影响本进程;只有「退出」菜单才真正结束。
    public class TrayService : ApplicationContext
    {
        private readonly ForwardRuleService _ruleService;
        private readonly int _webPort;
        private readonly StartupService _startupService;
        private readonly ToolStripMenuItem _startupItem;
        private readonly NotifyIcon _trayIcon;

        public TrayService(ForwardRuleService ruleService, int webPort, StartupService startupService, bool isFirstRun)
        {
            _ruleService = ruleService;
            _webPort = webPort;
            _startupService = startupService;

            _startupItem = new ToolStripMenuItem("开机启动", null, OnToggleStartup);
            _startupItem.Checked = startupService.IsEnabled();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开配置", null, OnOpenConfig);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("全部启动", null, OnStartAll);
            menu.Items.Add("全部停止", null, OnStopAll);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, OnExit);
            menu.Opening += OnMenuOpening;

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "GostWebUI v" + AppVersion.Display;
            _trayIcon.Icon = LoadTrayIcon();
            _trayIcon.Visible = true;
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += OnOpenConfig;

            if (isFirstRun)
            {
                // 首次启动:自动打开配置页并弹托盘气泡引导。放在托盘图标 Visible 之后,
                // 气泡消息随后由 Application.Run 的消息循环泵出显示。
                ShowFirstRunGuidance();
            }
        }

        // 从嵌入资源(app.ico,由 make-icon.ps1 生成)加载托盘图标,按当前 DPI 的小图标尺寸取最合适的一帧。
        // 资源缺失时退回系统默认图标,不让托盘起不来。
        private static Icon LoadTrayIcon()
        {
            Stream stream = typeof(TrayService).Assembly.GetManifestResourceStream("GostWebUI.app.ico");
            if (stream == null)
            {
                return SystemIcons.Application;
            }
            using (stream)
            {
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }

        private string GetConfigUrl()
        {
            return "http://127.0.0.1:" + _webPort.ToString();
        }

        // 每次弹菜单前刷新「开机启动」勾选:该开关也可能刚在网页设置里被切换过
        private void OnMenuOpening(object sender, CancelEventArgs e)
        {
            _startupItem.Checked = _startupService.IsEnabled();
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            try
            {
                OpenConfigPage();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开浏览器失败:" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 用系统默认浏览器打开本地配置页。失败向上抛,由调用方决定提示还是静默。
        private void OpenConfigPage()
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = GetConfigUrl();
            psi.UseShellExecute = true; // 交给系统默认浏览器打开
            Process.Start(psi);
        }

        // 首次运行引导:自动打开配置页,并弹托盘气泡告知程序常驻托盘、以后可双击托盘图标再次打开。
        // 打开浏览器失败时静默——气泡里的「双击托盘打开」指引即为兜底,不打断托盘启动。
        private void ShowFirstRunGuidance()
        {
            try
            {
                OpenConfigPage();
            }
            catch (Exception)
            {
                // 忽略:仍弹气泡提示用户手动双击托盘打开
            }

            _trayIcon.BalloonTipTitle = "GostWebUI 已在后台运行";
            _trayIcon.BalloonTipText = "已为你打开配置页进行首次设置。程序常驻系统托盘,之后可双击托盘图标随时打开配置。";
            _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(10000);
        }

        private void OnStartAll(object sender, EventArgs e)
        {
            _ruleService.StartAll();
        }

        private void OnStopAll(object sender, EventArgs e)
        {
            _ruleService.StopAll();
        }

        private void OnToggleStartup(object sender, EventArgs e)
        {
            try
            {
                if (_startupService.IsEnabled())
                {
                    _startupService.Disable();
                }
                else
                {
                    _startupService.Enable();
                }
                _startupItem.Checked = _startupService.IsEnabled();
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机启动失败:" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            // 退出消息循环;真正的进程收尾在 Program.Main 里 Application.Run 之后完成
            ExitThread();
        }
    }
}
