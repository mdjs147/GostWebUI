namespace PortForwarder.Models
{
    // 一条日志(带自增序号,便于前端增量拉取)
    public class LogEntry
    {
        public long Seq { get; set; }
        public string RuleId { get; set; }
        public string Time { get; set; }
        public string Text { get; set; }
    }
}
