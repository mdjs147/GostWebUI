namespace PortForwarder.Models
{
    // 连接测试结果 DTO,直接序列化为 JSON 返回前端。
    public class ConnectionTestResult
    {
        public bool Success { get; set; }
        public long ElapsedMs { get; set; }
        public string Message { get; set; }

        public ConnectionTestResult()
        {
            Success = false;
            ElapsedMs = 0;
            Message = "";
        }

        public static ConnectionTestResult Ok(long elapsedMs, string message)
        {
            ConnectionTestResult result = new ConnectionTestResult();
            result.Success = true;
            result.ElapsedMs = elapsedMs;
            result.Message = message;
            return result;
        }

        public static ConnectionTestResult Fail(long elapsedMs, string message)
        {
            ConnectionTestResult result = new ConnectionTestResult();
            result.Success = false;
            result.ElapsedMs = elapsedMs;
            result.Message = message;
            return result;
        }
    }
}
