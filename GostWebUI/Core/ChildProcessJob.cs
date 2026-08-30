using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GostWebUI.Core
{
    // 进程级 kill-on-close Job Object:gost 子进程启动后加入本 Job,
    // 主进程无论正常退出、崩溃还是被 taskkill /F 强杀,内核在 Job 句柄随进程回收时
    // 自动终止所有成员进程,从机制上杜绝孤儿 gost(残留会占住监听端口,导致下次启动 bind 失败)。
    // Job 句柄有意全程持有、永不关闭:关闭句柄即触发 kill-on-close。
    public static class ChildProcessJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly object Gate = new object();
        private static IntPtr _jobHandle = IntPtr.Zero;
        private static bool _createFailed = false;

        // 把已启动的子进程加入 Job。返回 false 表示未纳入内核兜底
        // (仅影响主进程异常退出时的清理,不影响转发本身),调用方记日志即可。
        public static bool TryAssign(Process process)
        {
            lock (Gate)
            {
                if (_createFailed)
                {
                    return false;
                }
                if (_jobHandle == IntPtr.Zero)
                {
                    _jobHandle = CreateKillOnCloseJob();
                    if (_jobHandle == IntPtr.Zero)
                    {
                        _createFailed = true;
                        return false;
                    }
                }

                try
                {
                    return AssignProcessToJobObject(_jobHandle, process.Handle);
                }
                catch (Exception)
                {
                    // 进程可能在启动后立即退出,取 Handle 会抛 InvalidOperationException
                    return false;
                }
            }
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, infoPtr, length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            return job;
        }

        // ===== Win32 互操作声明 =====

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, int cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
