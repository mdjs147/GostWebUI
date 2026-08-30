using System;
using Microsoft.Win32;

namespace PortForwarder.Services
{
    // 开机启动管理:读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表值。
    // 作用于当前用户,无需管理员权限;值为当前 exe 完整路径(带引号)。
    public class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

#if DEBUG
        // 开发构建用独立值名:避免调试时把 bin 下的临时 exe 覆盖掉正式版的开机启动注册
        private const string ValueName = "GostWebUI-Dev";
        // 项目由 MyPortForwarder 更名而来,旧值名仅用于一次性迁移
        private const string LegacyValueName = "MyPortForwarder-Dev";
#else
        private const string ValueName = "GostWebUI";
        private const string LegacyValueName = "MyPortForwarder";
#endif

        public bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                object value = key.GetValue(ValueName);
                return value != null;
            }
        }

        public void Enable()
        {
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new InvalidOperationException("无法获取当前可执行文件路径");
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            {
                // 路径带引号,防止含空格的目录被系统截断解析
                key.SetValue(ValueName, "\"" + exePath + "\"");
            }
        }

        public void Disable()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        // 启动时调用:把旧程序名(MyPortForwarder)时期的开机启动注册值迁移到新值名,
        // 继承启用状态并指向当前 exe;失败静默(不影响程序启动)。
        public void MigrateLegacyValueName()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        return;
                    }
                    object legacy = key.GetValue(LegacyValueName);
                    if (legacy == null)
                    {
                        return;
                    }
                    if (key.GetValue(ValueName) == null)
                    {
                        // 旧值路径指向旧 exe,直接用当前 exe 路径重写到新值名
                        Enable();
                    }
                    key.DeleteValue(LegacyValueName, false);
                }
            }
            catch (Exception)
            {
                // 尽力而为:注册表不可写时不影响程序启动
            }
        }

        // 启动时调用:已启用开机启动的情况下重写注册路径,
        // 使 exe 被移动/升级换目录后注册项自动指向新位置。失败静默(下次手动切换时再报错)。
        public void RefreshPathIfEnabled()
        {
            try
            {
                if (IsEnabled())
                {
                    Enable();
                }
            }
            catch (Exception)
            {
                // 尽力而为:注册表不可写时不影响程序启动
            }
        }
    }
}
