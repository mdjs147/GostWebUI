using System;
using System.Collections.Generic;

namespace GostWebUI.Models
{
    // 一条端口转发规则。Id 用于 REST API 定位;其余字段与前端表单一一对应。
    public class ForwardRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ListenAddress { get; set; }
        public int ListenPort { get; set; }
        public string TargetHost { get; set; }
        public int TargetPort { get; set; }
        public string ProxyType { get; set; }
        public string ProxyHost { get; set; }
        public int ProxyPort { get; set; }
        public bool AutoStart { get; set; }
        // 转发模式:"gost"(默认)= 拉起 gost 子进程做通用 TCP 转发;
        // "mysql" = 进程内 MySQL TLS 中继,以 SSLRequest 分段补全兼容「会对首包做嗅探/懒连接」的中转,
        // server-first 的 MySQL(这类目标用普通 gost 转发会卡在初始握手包超时)。详见 Core/MySqlRelayManager。
        // 反序列化时缺少此字段则保持构造函数默认值 "gost"。
        public string Mode { get; set; }

        public ForwardRule()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "新转发";
            ListenAddress = "127.0.0.1";
            ListenPort = 41011;
            TargetHost = "192.0.2.10";
            TargetPort = 41011;
            ProxyType = "socks5";
            ProxyHost = "127.0.0.1";
            ProxyPort = 3066;
            AutoStart = false;
            Mode = "gost";
        }

        // 生成 gost 命令行参数,例如:
        // -L tcp://127.0.0.1:41011/192.0.2.10:41011 -F socks5://127.0.0.1:3066
        public List<string> BuildGostArguments()
        {
            List<string> args = new List<string>();

            string listenHost = string.IsNullOrWhiteSpace(ListenAddress) ? "" : ListenAddress;
            string listen = "tcp://" + listenHost + ":" + ListenPort.ToString() + "/" + TargetHost + ":" + TargetPort.ToString();
            args.Add("-L");
            args.Add(listen);

            if (ProxyType != null && ProxyType.ToLowerInvariant() != "direct" && !string.IsNullOrWhiteSpace(ProxyHost))
            {
                string forward = ProxyType.ToLowerInvariant() + "://" + ProxyHost + ":" + ProxyPort.ToString();
                args.Add("-F");
                args.Add(forward);
            }

            return args;
        }

        // 是否为 MySQL TLS 中继模式(进程内 SSLRequest 分段补全中继,不拉 gost 子进程)。
        public bool IsMySqlRelay()
        {
            return Mode != null && Mode.Trim().ToLowerInvariant() == "mysql";
        }

        // 把可编辑字段从另一实例覆盖过来(用于 PUT 更新时保留 Id)
        public void CopyEditableFrom(ForwardRule other)
        {
            Name = other.Name;
            ListenAddress = other.ListenAddress;
            ListenPort = other.ListenPort;
            TargetHost = other.TargetHost;
            TargetPort = other.TargetPort;
            ProxyType = other.ProxyType;
            ProxyHost = other.ProxyHost;
            ProxyPort = other.ProxyPort;
            AutoStart = other.AutoStart;
            Mode = other.Mode;
        }
    }
}
