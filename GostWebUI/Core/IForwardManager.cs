using System;
using PortForwarder.Models;

namespace PortForwarder.Core
{
    // 一条转发规则的运行时管理器抽象。两种实现共用此契约,由 ForwardRuleService 统一按 Id
    // 注册、启停、订阅日志:
    //   - GostProcessManager  : 拉起 gost 子进程做通用 TCP 转发(Mode="gost")
    //   - MySqlRelayManager   : 进程内 MySQL TLS 中继,以 SSLRequest 分段补全兼容懒连接中转(Mode="mysql")
    // 接口只暴露服务层需要的最小面;gost 特有的 GostPath / IntegrityCheck 不进接口,
    // 由 ForwardRuleService 在创建 GostProcessManager 时按具体类型设置。
    public interface IForwardManager
    {
        ForwardRule Rule { get; }
        bool IsRunning { get; }
        event Action<string> LogReceived;
        event Action StateChanged;
        void Start();
        void Stop();
    }
}
