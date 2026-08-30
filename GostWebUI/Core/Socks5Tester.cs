using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortForwarder.Models;

namespace PortForwarder.Core
{
    // 连接测试工具,提供三类测试:
    //   1) TestTcpAsync            —— 目标 host:port 的裸 TCP 可达性(用于测本地监听端口 / 代理端口)
    //   2) TestThroughSocks5Async  —— 经 SOCKS5 代理端到端连到目标(验证整条代理链路)
    // 只依赖 System.Net.Sockets,无第三方库。
    public class Socks5Tester
    {
        private const int DefaultTimeoutMs = 5000;

        // ===== 裸 TCP 连接测试 =====
        public async Task<ConnectionTestResult> TestTcpAsync(string host, int port, int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = DefaultTimeoutMs;
            }

            Stopwatch sw = Stopwatch.StartNew();
            TcpClient client = new TcpClient();
            try
            {
                Task connectTask = client.ConnectAsync(host, port);
                Task completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                sw.Stop();

                if (completed != connectTask)
                {
                    return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "连接超时(" + timeoutMs.ToString() + "ms):" + host + ":" + port.ToString());
                }

                await connectTask;
                return ConnectionTestResult.Ok(sw.ElapsedMilliseconds, "TCP 可达:" + host + ":" + port.ToString());
            }
            catch (Exception ex)
            {
                sw.Stop();
                return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "连接失败:" + ex.Message);
            }
            finally
            {
                client.Close();
            }
        }

        // ===== 经 SOCKS5 端到端测试 =====
        // 先连代理端口,做 SOCKS5 握手(无认证),再发 CONNECT 请求到目标,读应答 REP。
        // REP=0x00 表示代理成功与目标建立了连接,证明整条链路(本机→SOCKS5 代理→目标)通。
        public async Task<ConnectionTestResult> TestThroughSocks5Async(string proxyHost, int proxyPort, string targetHost, int targetPort, int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = DefaultTimeoutMs;
            }

            Stopwatch sw = Stopwatch.StartNew();
            TcpClient client = new TcpClient();
            CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
            try
            {
                Task connectTask = client.ConnectAsync(proxyHost, proxyPort);
                Task completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                if (completed != connectTask)
                {
                    sw.Stop();
                    return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "代理不可达(连接超时):" + proxyHost + ":" + proxyPort.ToString() + " —— 确认代理软件已运行且该端口正在监听");
                }
                await connectTask;

                NetworkStream stream = client.GetStream();

                // 1) 握手:VER=5, NMETHODS=1, METHOD=0x00(无认证)
                byte[] greeting = new byte[] { 0x05, 0x01, 0x00 };
                await stream.WriteAsync(greeting, 0, greeting.Length, cts.Token);

                byte[] methodResp = new byte[2];
                await ReadExactAsync(stream, methodResp, 2, cts.Token);
                if (methodResp[0] != 0x05)
                {
                    sw.Stop();
                    return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "非 SOCKS5 应答(VER=" + methodResp[0].ToString() + "),该端口可能不是 SOCKS5 代理");
                }
                if (methodResp[1] == 0xFF)
                {
                    sw.Stop();
                    return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "代理拒绝:无可接受的认证方法(需要认证?)");
                }

                // 2) CONNECT 请求:VER=5, CMD=1(CONNECT), RSV=0, ATYP=3(域名), 域名长度, 域名, 端口(大端)
                byte[] request = BuildConnectRequest(targetHost, targetPort);
                await stream.WriteAsync(request, 0, request.Length, cts.Token);

                // 3) 读应答头部:VER, REP, RSV, ATYP
                byte[] head = new byte[4];
                await ReadExactAsync(stream, head, 4, cts.Token);
                byte rep = head[1];

                // 把绑定地址部分读完,避免流残留(不影响判定,但保持整洁)
                await DrainBoundAddressAsync(stream, head[3], cts.Token);

                sw.Stop();

                if (rep == 0x00)
                {
                    return ConnectionTestResult.Ok(sw.ElapsedMilliseconds, "链路通:经 " + proxyHost + ":" + proxyPort.ToString() + " 已连到 " + targetHost + ":" + targetPort.ToString());
                }
                return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "代理可达,但到目标失败:" + DescribeSocks5Reply(rep) + "(目标 " + targetHost + ":" + targetPort.ToString() + ")");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "SOCKS5 握手/连接超时(" + timeoutMs.ToString() + "ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                return ConnectionTestResult.Fail(sw.ElapsedMilliseconds, "测试异常:" + ex.Message);
            }
            finally
            {
                cts.Dispose();
                client.Close();
            }
        }

        private byte[] BuildConnectRequest(string targetHost, int targetPort)
        {
            byte[] hostBytes = Encoding.ASCII.GetBytes(targetHost);
            byte[] request = new byte[4 + 1 + hostBytes.Length + 2];
            request[0] = 0x05; // VER
            request[1] = 0x01; // CMD = CONNECT
            request[2] = 0x00; // RSV
            request[3] = 0x03; // ATYP = 域名
            request[4] = (byte)hostBytes.Length;
            Array.Copy(hostBytes, 0, request, 5, hostBytes.Length);
            int portIndex = 5 + hostBytes.Length;
            request[portIndex] = (byte)((targetPort >> 8) & 0xFF);
            request[portIndex + 1] = (byte)(targetPort & 0xFF);
            return request;
        }

        // 根据 ATYP 读掉应答里的 BND.ADDR + BND.PORT
        private async Task DrainBoundAddressAsync(NetworkStream stream, byte atyp, CancellationToken token)
        {
            int addrLen = 0;
            if (atyp == 0x01)
            {
                addrLen = 4; // IPv4
            }
            else if (atyp == 0x04)
            {
                addrLen = 16; // IPv6
            }
            else if (atyp == 0x03)
            {
                byte[] lenByte = new byte[1];
                await ReadExactAsync(stream, lenByte, 1, token);
                addrLen = lenByte[0];
            }

            int total = addrLen + 2; // 地址 + 2 字节端口
            if (total > 0)
            {
                byte[] buffer = new byte[total];
                await ReadExactAsync(stream, buffer, total, token);
            }
        }

        private async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, token);
                if (read <= 0)
                {
                    throw new System.IO.IOException("连接被对端关闭,已读 " + offset.ToString() + "/" + count.ToString() + " 字节");
                }
                offset += read;
            }
        }

        private string DescribeSocks5Reply(byte rep)
        {
            switch (rep)
            {
                case 0x01:
                    return "常规 SOCKS 服务器故障";
                case 0x02:
                    return "规则不允许连接";
                case 0x03:
                    return "网络不可达";
                case 0x04:
                    return "主机不可达";
                case 0x05:
                    return "目标拒绝连接(端口未开?)";
                case 0x06:
                    return "TTL 过期";
                case 0x07:
                    return "命令不支持";
                case 0x08:
                    return "地址类型不支持";
                default:
                    return "未知错误码 0x" + rep.ToString("X2");
            }
        }
    }
}
