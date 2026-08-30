namespace GostWebUI.Models
{
    // gost.exe 完整性状态 DTO(REST 层序列化为 camelCase 供前端展示)。
    public class GostIntegrityInfo
    {
        // 解析后的绝对路径(相对路径已锚定到程序目录)
        public string GostPath { get; set; }
        public bool FileExists { get; set; }
        // 当前文件的实际 SHA-256;文件缺失或不可读时为 null
        public string CurrentSha256 { get; set; }
        // 已锁定的 SHA-256;null 表示尚未锁定(首次成功启动时自动锁定)
        public string PinnedSha256 { get; set; }
        // 已锁定且与当前文件一致
        public bool Trusted { get; set; }

        public GostIntegrityInfo()
        {
            GostPath = null;
            FileExists = false;
            CurrentSha256 = null;
            PinnedSha256 = null;
            Trusted = false;
        }
    }
}
