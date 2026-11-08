using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ZapretUi
{
    // Процессы и службы без WMI и ServiceController: command line через ntdll,
    // статус служб через sc.exe, наличие — через реестр (как Test-Path в оригинале).
    public static class Proc
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint PROCESS_TERMINATE = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        public static void CloseHandlePublic(IntPtr h) => CloseHandle(h);

        public static bool GetExitCodeProcessPublic(IntPtr h, out int code) => GetExitCodeProcess(h, out code);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr hProcess, int processInformationClass, ref PROCESS_BASIC_INFORMATION pbi, int infoLength, out int returnLength);

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr hProcess, int processInformationClass, ref UNICODE_STRING ustr, int infoLength, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr UniqueExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        static IntPtr OpenLimited(int pid)
        {
            try { return OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid); }
            catch { return IntPtr.Zero; }
        }

        public static int? GetParentId(int pid)
        {
            var h = OpenLimited(pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0)
                    return (int)pbi.InheritedFromUniqueProcessId;
            }
            catch { }
            finally { CloseHandle(h); }
            return null;
        }

        // Командная строка процесса через PEB: класс 60 (ProcessCommandLineInformation)
        // на части сборок Windows падает с STATUS_INFO_LENGTH_MISMATCH, поэтому читаем
        // RTL_USER_PROCESS_PARAMETERS.CommandLine (x64: PEB.ProcessParameters @ 0x20, CommandLine @ 0x70)
        public static string GetCommandLine(int pid)
        {
            // PROCESS_VM_READ нужен для ReadProcessMemory: с правами самого пользователя это разрешено
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | 0x0010 /*PROCESS_VM_READ*/, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                int st = NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
                if (st != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

                var bytes = new byte[8];
                if (!ReadProcessMemory(h, pbi.PebBaseAddress + 0x20, bytes, 8, out _)) return null;
                IntPtr procParams = (IntPtr)BitConverter.ToInt64(bytes, 0);
                if (procParams == IntPtr.Zero) return null;

                var usBuf = new byte[16];
                if (!ReadProcessMemory(h, procParams + 0x70, usBuf, 16, out _)) return null;
                int len = BitConverter.ToUInt16(usBuf, 0);
                IntPtr buffer = (IntPtr)BitConverter.ToInt64(usBuf, 8);
                if (len == 0 || buffer == IntPtr.Zero) return null;

                var buf = new byte[len];
                if (!ReadProcessMemory(h, buffer, buf, len, out int read) || read == 0) return null;
                return Encoding.Unicode.GetString(buf, 0, read);
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        public static Dictionary<int, int> ParentMap()
        {
            var map = new Dictionary<int, int>();
            foreach (var p in Process.GetProcesses())
            {
                int id = p.Id;
                try { p.Dispose(); } catch { }
                var parent = GetParentId(id);
                if (parent.HasValue) map[id] = parent.Value;
            }
            return map;
        }

        // Stop-ProcessTree: сначала дети, потом сам процесс
        public static void KillTree(int pid)
        {
            var parents = ParentMap();
            KillTree(pid, parents);
        }

        static void KillTree(int pid, Dictionary<int, int> parents)
        {
            foreach (var kv in parents)
                if (kv.Value == pid) KillTree(kv.Key, parents);
            Kill(pid);
        }

        public static void Kill(int pid)
        {
            var h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_TERMINATE, false, pid);
                if (h != IntPtr.Zero) TerminateProcess(h, 1);
            }
            catch { }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        public static void KillByName(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { Kill(p.Id); } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        // sc.exe со скрытым окном (как «& sc.exe ...» в оригинале)
        public static void Sc(string args) => RunHidden("sc.exe", args, null);

        public static void RunHidden(string file, string args, string workingDir)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir ?? "",
                };
                using var p = Process.Start(psi);
                if (p != null) { p.WaitForExit(15000); if (!p.HasExited) try { Kill(p.Id); } catch { } }
            }
            catch { }
        }

        public static bool ServiceExists(string name)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                return k != null;
            }
            catch { return false; }
        }

        // Статус службы через SCM API — без разбора локализованного вывода sc.exe
        // ("Состояние" вместо "STATE" в русской Windows)
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machineName, string databaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseServiceHandle(IntPtr hSCObject);

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        const uint SC_MANAGER_CONNECT = 0x0001;
        const uint SERVICE_QUERY_STATUS = 0x0004;

        // null — службы нет; иначе Running / StartPending / StopPending / Stopped / Paused
        public static string ServiceStatus(string name)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return null;
            try
            {
                IntPtr svc = OpenService(scm, name, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return null;
                try
                {
                    if (!QueryServiceStatus(svc, out var st)) return null;
                    return st.dwCurrentState switch
                    {
                        4 => "Running",
                        2 => "StartPending",
                        3 => "StopPending",
                        7 => "Paused",
                        _ => "Stopped",
                    };
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public static string Capture(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return outp;
            }
            catch { return null; }
        }
    }

    // Процесс с stdin/stdout/stderr, перенаправленными в файлы (как Start-Process -Redirect*)
    public sealed class RedirectedProcess : IDisposable
    {
        IntPtr _hProcess;
        public int Id { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateProcessW(string lpApplicationName, [In, Out] StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint OPEN_EXISTING = 3;
        const uint CREATE_ALWAYS = 2;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        const uint CREATE_NO_WINDOW = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        public static RedirectedProcess Start(string fileName, string arguments, string workingDir,
            string stdinPath, string stdoutPath, string stderrPath)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            IntPtr hIn = CreateFileW(stdinPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hIn == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr hOut = CreateFileW(stdoutPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hOut == new IntPtr(-1)) { int e = Marshal.GetLastWin32Error(); Proc.CloseHandlePublic(hIn); throw new Win32Exception(e); }
            IntPtr hErr = stderrPath != null
                ? CreateFileW(stderrPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero)
                : hOut;
            if (hErr == new IntPtr(-1)) { int e = Marshal.GetLastWin32Error(); Proc.CloseHandlePublic(hIn); Proc.CloseHandlePublic(hOut); throw new Win32Exception(e); }

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = 0x00000001 /*STARTF_USESHOWWINDOW*/ | 0x00000100 /*STARTF_USESTDHANDLES*/,
                wShowWindow = 0, // SW_HIDE
                hStdInput = hIn,
                hStdOutput = hOut,
                hStdError = hErr,
            };
            var cmd = new StringBuilder("\"" + fileName + "\" " + arguments);
            bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW,
                IntPtr.Zero, workingDir, ref si, out var pi);
            Proc.CloseHandlePublic(hIn); Proc.CloseHandlePublic(hOut);
            if (hErr != hOut) Proc.CloseHandlePublic(hErr);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            Proc.CloseHandlePublic(pi.hThread);
            return new RedirectedProcess { Id = pi.dwProcessId, _hProcess = pi.hProcess };
        }

        public bool HasExited
        {
            get
            {
                if (_hProcess == IntPtr.Zero) return true;
                if (!Proc.GetExitCodeProcessPublic(_hProcess, out int code)) return true;
                return code != 259 /*STILL_ACTIVE*/;
            }
        }

        public void KillTree() => Proc.KillTree(Id);

        public void Dispose()
        {
            if (_hProcess != IntPtr.Zero) { Proc.CloseHandlePublic(_hProcess); _hProcess = IntPtr.Zero; }
        }
    }
}
