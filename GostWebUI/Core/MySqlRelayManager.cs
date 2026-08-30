using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GostWebUI.Models;

namespace GostWebUI.Core
{
    // 进程内 MySQL TLS 中继(Mode="mysql"):兼容「对首包做嗅探/懒连接、且把首字节透传给后端」的中转,
    // 转发 server-first 的 MySQL(这类目标用普通 gost 转发会卡在初始握手包超时)。
    //
    // 原理(见 docs/connection-test.md 与项目记忆):MySQL 的 SSLRequest 是一个「合法、且不依赖
    // 服务器 salt」的早期包 —— 连上后端先替发它,既触发中转拨号(破懒连接)、又被后端正确当作
    // 「客户端请求 TLS」(不污染握手);随后连接进入 TLS,真正的认证由客户端用真实 salt/密码在 TLS 内
    // 端到端完成,本中继只搬密文、不接触凭据、也无需证书。
    //
    // 每条连接(替发 SSLRequest 分两步发,以对齐 charset —— 见下):
    //   连后端 → 先只发 SSLRequest 的 4 字节包头(破懒连接、让后端开始读这个包)→ 读后端 Initial Handshake(seq0)
    //   转发给客户端 → 读客户端自己的 SSLRequest,用它的 32 字节 payload 补全后端那半个替发包 → 之后双向裸透传。
    //   seq 对齐:两边 SSLRequest 都是 seq1,客户端 TLS 内的 Handshake Response 恰为 seq2、正是后端所期待。
    //   要求客户端启用 TLS(mysqlsh --ssl-mode=REQUIRED;现代客户端默认即走 TLS)。
    //
    // 为何分两步发 SSLRequest(关键坑位):MySQL 8.x 要求 SSLRequest 与随后 HandshakeResponse 的 charset(实测
    //   maxpacket/caps 亦然)一致,否则后端回 ER_HANDSHAKE_ERROR「Bad handshake」。而客户端 charset 各不相同
    //   (mysqlsh 随系统区域、DBeaver/Workbench 随驱动),中继无法预先猜中;故先只发包头破懒连接,待读到客户端
    //   SSLRequest 后用其原始 payload 补全,charset 天然对齐。(曾硬编码 charset=45,真实客户端全数 Bad handshake。)
    //
    // 锁纪律:所有 RaiseLog/RaiseStateChanged 一律在 _gate 外调用,使本类持有 _gate 时绝不回调外部
    //   (LogReceived → ForwardRuleService.AppendLog 会去拿其 _gate),锁序单向,不与服务层构成 ABBA。
    public class MySqlRelayManager : IForwardManager
    {
        // 客户端 SSLRequest 的 CLIENT_SSL 能力位(用于校验客户端确实启用了 TLS)
        private const uint ClientSsl = 0x800u;
        private const int SslRequestPayloadLength = 32;
        private const int ConnectTimeoutMs = 10000;
        private const int MaxHandshakePacket = 65535; // 握手阶段包极小,超此值视为异常,防越权分配
        private const int PipeBufferSize = 65536;

        private readonly ForwardRule _rule;
        private readonly object _gate;
        private readonly List<TcpClient> _active;
        private TcpListener _listener;
        private Thread _acceptThread;
        private bool _running;

        public event Action<string> LogReceived;
        public event Action StateChanged;

        public MySqlRelayManager(ForwardRule rule)
        {
            _rule = rule;
            _gate = new object();
            _active = new List<TcpClient>();
            _listener = null;
            _acceptThread = null;
            _running = false;
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
                    return _running;
                }
            }
        }

        public void Start()
        {
            string startedInfo = null;
            string failInfo = null;
            lock (_gate)
            {
                if (_running)
                {
                    return;
                }
                try
                {
                    IPAddress bindAddress = ResolveListenAddress(_rule.ListenAddress);
                    TcpListener listener = new TcpListener(bindAddress, _rule.ListenPort);
                    listener.Start();
                    _listener = listener;
                    _running = true;
                    Thread thread = new Thread(AcceptLoop);
                    thread.IsBackground = true;
                    _acceptThread = thread;
                    thread.Start();
                    startedInfo = "MySQL TLS 中继已启动:监听 " + DescribeListen() + " → " + DescribeTarget();
                }
                catch (Exception ex)
                {
                    _running = false;
                    _listener = null;
                    failInfo = "启动失败:" + ex.Message;
                }
            }
            if (startedInfo != null)
            {
                RaiseLog(startedInfo);
            }
            if (failInfo != null)
            {
                RaiseLog(failInfo);
            }
            RaiseStateChanged();
        }

        public void Stop()
        {
            TcpListener listener;
            List<TcpClient> connections;
            lock (_gate)
            {
                if (!_running && _listener == null)
                {
                    return;
                }
                _running = false;
                listener = _listener;
                _listener = null;
                connections = new List<TcpClient>(_active);
                _active.Clear();
            }
            // 锁外做 IO:停 listener 中断 accept,关活动连接中断各 pipe 的阻塞读
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception)
                {
                }
            }
            foreach (TcpClient client in connections)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
            }
            RaiseLog("MySQL TLS 中继已停止");
            RaiseStateChanged();
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpListener listener;
                lock (_gate)
                {
                    listener = _listener;
                }
                if (listener == null)
                {
                    break;
                }
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break; // listener.Stop() 会让 Accept 抛异常,正常退出循环
                }
                TcpClient accepted = client;
                Thread thread = new Thread(delegate ()
                {
                    HandleClient(accepted);
                });
                thread.IsBackground = true;
                thread.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            TcpClient backend = null;
            string clientInfo = DescribeRemote(client);
            try
            {
                backend = ConnectBackend();
                if (!Register(client, backend))
                {
                    return; // 已停止,不再服务本连接
                }
                NetworkStream clientStream = client.GetStream();
                NetworkStream backendStream = backend.GetStream();

                // ① 先只发替发 SSLRequest 的 4 字节包头(声明 payload=32、seq=1):足以破懒连接并让后端开始读这个包,
                //    但 payload(含 charset)故意留空 —— 因为 MySQL 8.x 要求 SSLRequest 与随后 HandshakeResponse 的
                //    charset 完全一致(实测 maxpacket/caps 亦然更稳),不一致后端回 ER_HANDSHAKE_ERROR「Bad handshake」。
                //    而客户端的 charset 要等它自己发 SSLRequest 才知道,故 payload 推迟到 ③.5 用客户端原始字节补全。
                byte[] sslRequestHeader = BuildSslRequestHeader();
                backendStream.Write(sslRequestHeader, 0, sslRequestHeader.Length);

                // ② 读后端 Initial Handshake(seq0),原样转发给客户端
                byte[] initialHandshake = ReadPacket(backendStream);
                if (initialHandshake.Length < 5 || initialHandshake[4] != 0x0a)
                {
                    RaiseLog("后端首包非 MySQL 握手,断开(" + clientInfo + ")");
                    return;
                }
                clientStream.Write(initialHandshake, 0, initialHandshake.Length);

                // ③ 读客户端第一个包,应为 SSLRequest(4+32=36 字节);校验后不再转发整包
                byte[] clientFirst = ReadPacket(clientStream);
                if (!IsSslRequest(clientFirst))
                {
                    // clientInfo 是来源 socket(客户端临时端口),不是让用户去连的地址;别让它被误读成连接目标
                    RaiseLog("客户端 " + clientInfo + " 未启用 TLS,已拒绝(本模式要求客户端走 TLS);请开启 TLS 后重连 "
                        + DescribeListen() + " —— mysqlsh 加 --ssl-mode=REQUIRED,DBeaver 在「驱动属性」设 sslMode=REQUIRED");
                    return;
                }

                // ③.5 用客户端 SSLRequest 的 32 字节 payload 补全 ① 只发了头的替发包。这样后端记录的
                //      caps/maxpacket/charset 与客户端 TLS 内 HandshakeResponse 天然一致,不再触发「Bad handshake」。
                //      客户端自己的这份 SSLRequest 到此为止被「吞掉」——只借它的 payload 字节,不整包转发。
                backendStream.Write(clientFirst, 4, clientFirst.Length - 4);

                RaiseLog("客户端接入 " + clientInfo + ",进入 TLS 透传");

                // ④ 之后 TLS 端到端裸透传(本中继不终止 TLS、不接触凭据)
                NetworkStream backwardSrc = backendStream;
                NetworkStream backwardDst = clientStream;
                Thread backwardThread = new Thread(delegate ()
                {
                    Pipe(backwardSrc, backwardDst);
                });
                backwardThread.IsBackground = true;
                backwardThread.Start();
                Pipe(clientStream, backendStream);
            }
            catch (Exception ex)
            {
                // 后端连不上 / 握手中断 / 对端提前关闭:记一条,不影响其它连接
                RaiseLog("连接处理结束(" + clientInfo + "):" + ex.Message);
            }
            finally
            {
                Unregister(client, backend);
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
                if (backend != null)
                {
                    try
                    {
                        backend.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // 已停止时返回 false:调用方不再服务本连接。注册与 Stop 的清空同锁,避免 Stop 后仍启动新 pipe。
        private bool Register(TcpClient client, TcpClient backend)
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return false;
                }
                _active.Add(client);
                _active.Add(backend);
                return true;
            }
        }

        private void Unregister(TcpClient client, TcpClient backend)
        {
            lock (_gate)
            {
                _active.Remove(client);
                if (backend != null)
                {
                    _active.Remove(backend);
                }
            }
        }

        private TcpClient ConnectBackend()
        {
            string proxyType = _rule.ProxyType == null ? "" : _rule.ProxyType.Trim().ToLowerInvariant();
            if (proxyType == "socks5")
            {
                return ConnectViaSocks5(_rule.ProxyHost, _rule.ProxyPort, _rule.TargetHost, _rule.TargetPort);
            }
            return ConnectDirect(_rule.TargetHost, _rule.TargetPort);
        }

        // 带超时的同步连接(BeginConnect + 等待句柄):支持域名,超时即放弃
        private static TcpClient ConnectDirect(string host, int port)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    throw new TimeoutException("连接后端超时:" + host + ":" + port.ToString());
                }
                client.EndConnect(ar);
            }
            catch (Exception)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
                throw;
            }
            return client;
        }

        // 经 socks5(免认证)CONNECT 到目标;域名以 ATYP=3 传给代理,由代理侧解析
        private static TcpClient ConnectViaSocks5(string proxyHost, int proxyPort, string targetHost, int targetPort)
        {
            TcpClient client = ConnectDirect(proxyHost, proxyPort);
            try
            {
                NetworkStream stream = client.GetStream();

                byte[] greeting = new byte[] { 0x05, 0x01, 0x00 };
                stream.Write(greeting, 0, greeting.Length);
                byte[] methodResp = new byte[2];
                ReadExact(stream, methodResp, 0, 2);
                if (methodResp[0] != 0x05 || methodResp[1] != 0x00)
                {
                    throw new Exception("socks5 握手被拒(需免认证)");
                }

                byte[] hostBytes = Encoding.ASCII.GetBytes(targetHost == null ? "" : targetHost);
                if (hostBytes.Length == 0 || hostBytes.Length > 255)
                {
                    throw new Exception("socks5 目标域名长度非法");
                }
                byte[] request = new byte[7 + hostBytes.Length];
                request[0] = 0x05; // VER
                request[1] = 0x01; // CMD = CONNECT
                request[2] = 0x00; // RSV
                request[3] = 0x03; // ATYP = 域名
                request[4] = (byte)hostBytes.Length;
                Array.Copy(hostBytes, 0, request, 5, hostBytes.Length);
                request[5 + hostBytes.Length] = (byte)((targetPort >> 8) & 0xff);
                request[6 + hostBytes.Length] = (byte)(targetPort & 0xff);
                stream.Write(request, 0, request.Length);

                byte[] head = new byte[4];
                ReadExact(stream, head, 0, 4);
                if (head[1] != 0x00)
                {
                    throw new Exception("socks5 连接目标失败 code=" + head[1].ToString());
                }
                int bndLength;
                if (head[3] == 0x01)
                {
                    bndLength = 4 + 2;
                }
                else if (head[3] == 0x04)
                {
                    bndLength = 16 + 2;
                }
                else if (head[3] == 0x03)
                {
                    byte[] lenByte = new byte[1];
                    ReadExact(stream, lenByte, 0, 1);
                    bndLength = lenByte[0] + 2;
                }
                else
                {
                    throw new Exception("socks5 未知地址类型");
                }
                byte[] bnd = new byte[bndLength];
                ReadExact(stream, bnd, 0, bndLength);
            }
            catch (Exception)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
                throw;
            }
            return client;
        }

        // 只构造 SSLRequest 的 4 字节包头(声明 payload 长度 32、seq=1)。
        // 真正的 payload(caps/maxpacket/charset/filler)不在这里造,而是在 HandleClient ③.5 处
        // 直接复制客户端 SSLRequest 的 payload —— 从而 charset 等字段与客户端 HandshakeResponse 天然一致。
        private static byte[] BuildSslRequestHeader()
        {
            byte[] header = new byte[4];
            header[0] = (byte)(SslRequestPayloadLength & 0xff);
            header[1] = (byte)((SslRequestPayloadLength >> 8) & 0xff);
            header[2] = (byte)((SslRequestPayloadLength >> 16) & 0xff);
            header[3] = 0x01; // sequence id = 1
            return header;
        }

        // 客户端首包是否为 SSLRequest:长度恰为 4+32 且能力位含 CLIENT_SSL(否则说明未启用 TLS)
        private static bool IsSslRequest(byte[] packet)
        {
            if (packet.Length != 4 + SslRequestPayloadLength)
            {
                return false;
            }
            uint caps = (uint)(packet[4] | (packet[5] << 8) | (packet[6] << 16) | (packet[7] << 24));
            return (caps & ClientSsl) != 0;
        }

        // 读取一个 MySQL 包(4 字节头:3 字节小端长度 + 1 字节序号;随后 payload)。仅用于握手阶段。
        private static byte[] ReadPacket(NetworkStream stream)
        {
            byte[] header = new byte[4];
            ReadExact(stream, header, 0, 4);
            int payloadLength = header[0] | (header[1] << 8) | (header[2] << 16);
            if (payloadLength > MaxHandshakePacket)
            {
                throw new InvalidDataException("握手包异常过大:" + payloadLength.ToString());
            }
            byte[] packet = new byte[4 + payloadLength];
            Array.Copy(header, 0, packet, 0, 4);
            ReadExact(stream, packet, 4, payloadLength);
            return packet;
        }

        private static void ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException("对端在读取中关闭");
                }
                read += n;
            }
        }

        // 单向搬运直到一端关闭/出错;另一方向随 socket 关闭而退出(由 HandleClient 的 finally 收尾)
        private static void Pipe(NetworkStream src, NetworkStream dst)
        {
            byte[] buffer = new byte[PipeBufferSize];
            try
            {
                while (true)
                {
                    int n = src.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                    {
                        break;
                    }
                    dst.Write(buffer, 0, n);
                }
            }
            catch (Exception)
            {
            }
        }

        private static IPAddress ResolveListenAddress(string address)
        {
            string a = address == null ? "" : address.Trim();
            if (a.Length == 0 || a == "0.0.0.0")
            {
                return IPAddress.Any;
            }
            if (a == "::" || a == "[::]")
            {
                return IPAddress.IPv6Any;
            }
            return IPAddress.Parse(a);
        }

        private string DescribeListen()
        {
            string host = string.IsNullOrWhiteSpace(_rule.ListenAddress) ? "0.0.0.0" : _rule.ListenAddress;
            return host + ":" + _rule.ListenPort.ToString();
        }

        private string DescribeTarget()
        {
            string proxyType = _rule.ProxyType == null ? "" : _rule.ProxyType.Trim().ToLowerInvariant();
            string via;
            if (proxyType == "socks5")
            {
                via = "经 socks5 " + _rule.ProxyHost + ":" + _rule.ProxyPort.ToString();
            }
            else
            {
                via = "直连";
            }
            return _rule.TargetHost + ":" + _rule.TargetPort.ToString() + "(" + via + ")";
        }

        private static string DescribeRemote(TcpClient client)
        {
            try
            {
                return client.Client.RemoteEndPoint.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
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
