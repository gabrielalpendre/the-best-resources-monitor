// Windows: contadores PDH, lista de processos por uma unica chamada ao kernel, Visualizador de Eventos e WMI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EvidenceMonitor
{
    [SupportedOSPlatform("windows")]
    static class WinNative
    {
        [StructLayout(LayoutKind.Explicit)]
        public struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double doubleValue; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] public static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE v);
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("psapi.dll")] public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint size);
        [DllImport("kernel32.dll")] public static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int needed);
    }

    [SupportedOSPlatform("windows")]
    class WindowsPlatform : IPlatform
    {
        public string Os { get { return "windows"; } }
        public string OsName { get { return "Windows"; } }
        public string RebootWord { get { return "o Windows"; } }
        public string AdminTerminalLabel { get { return "Abrir PowerShell como administrador"; } }
        public bool HasWsl { get { return true; } }
        public string EventsHint { get { return null; } }

        static readonly Dictionary<string, string> Labels = new Dictionary<string, string> {
            { "mem", "Memória esgotada" }, { "dwm", "Tela travou (DWM)" }, { "crash", "App falhou" },
            { "hang", "App sem resposta" }, { "svc", "Serviço caiu" }, { "crit", "Crítico (energia/hardware/térmico)" } };
        public Dictionary<string, string> CatLabels { get { return Labels; } }

        static readonly Regex CriticalRx = new Regex(@"^(System|Registry|smss|csrss|wininit|winlogon|services|lsass|lsaiso|Memory Compression|Secure System|fontdrvhost|dwm|svchost)$", RegexOptions.IgnoreCase);
        public Regex Critical { get { return CriticalRx; } }
        static readonly Regex Vnic = new Regex(@"vEthernet|Hyper-V|Loopback|isatap|Teredo|WAN Miniport|Kernel Debug", RegexOptions.IgnoreCase);
        public Regex VirtualNic { get { return Vnic; } }

        // --- metricas (PDH) ---
        IntPtr query;
        readonly Dictionary<string, IntPtr> counters = new Dictionary<string, IntPtr>();
        readonly object pdhLock = new object();

        public WindowsPlatform()
        {
            WinNative.PdhOpenQuery(null, IntPtr.Zero, out query);
            Add("cpu", @"\Processor Information(_Total)\% Processor Time");
            Add("perf", @"\Processor Information(_Total)\% Processor Performance");
            Add("freq_base", @"\Processor Information(_Total)\Processor Frequency");
            Add("mem_avail", @"\Memory\Available MBytes");
            Add("commit", @"\Memory\% Committed Bytes In Use");
            Add("pages", @"\Memory\Pages/sec");
            Add("disk_idle", @"\PhysicalDisk(_Total)\% Idle Time");
            Add("disk_bps", @"\PhysicalDisk(_Total)\Disk Bytes/sec");
            Add("disk_q", @"\PhysicalDisk(_Total)\Current Disk Queue Length");
            WinNative.PdhCollectQueryData(query);
        }

        void Add(string key, string path)
        {
            IntPtr c;
            if (WinNative.PdhAddEnglishCounter(query, path, IntPtr.Zero, out c) == 0) counters[key] = c;
        }

        double Get(string key)
        {
            IntPtr c;
            if (!counters.TryGetValue(key, out c)) return -1;
            WinNative.PDH_FMT_COUNTERVALUE v;
            if (WinNative.PdhGetFormattedCounterValue(c, 0x200 | 0x8000, IntPtr.Zero, out v) != 0) return -1;
            return v.doubleValue;
        }

        static double R(double v, int d) { return Math.Round(v, d); }

        public void ReadSystem(Dictionary<string, object> row)
        {
            lock (pdhLock)
            {
                WinNative.PdhCollectQueryData(query);
                double cpu = Get("cpu"), perf = Get("perf"), fb = Get("freq_base");
                row["cpu"] = R(Sampler.Clamp(cpu, 0, 100), 1);
                row["freq_base"] = R(fb, 0);
                row["freq"] = (perf >= 0 && fb > 0) ? R(fb * perf / 100.0, 0) : -1;
                row["mem_avail"] = R(Get("mem_avail"), 0);
                row["commit"] = R(Get("commit"), 1);
                row["pages"] = R(Get("pages"), 0);
                double idle = Get("disk_idle");
                row["disk_busy"] = idle >= 0 ? R(Sampler.Clamp(100 - idle, 0, 100), 1) : -1;
                double bps = Get("disk_bps");
                row["disk_mbs"] = bps >= 0 ? R(bps / 1048576.0, 1) : -1;
                row["disk_q"] = R(Get("disk_q"), 0);
            }
            WinNative.PERFORMANCE_INFORMATION pi;
            if (WinNative.GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(WinNative.PERFORMANCE_INFORMATION))))
            {
                double page = (double)pi.PageSize.ToUInt64();
                row["commit_gb"] = R(pi.CommitTotal.ToUInt64() * page / 1073741824.0, 1);
                row["limit_gb"] = R(pi.CommitLimit.ToUInt64() * page / 1073741824.0, 1);
                row["ram_gb"] = R(pi.PhysicalTotal.ToUInt64() * page / 1073741824.0, 1);
            }
        }

        public bool CpuTimes(out long idle, out long total)
        {
            long k, u;
            if (!WinNative.GetSystemTimes(out idle, out k, out u)) { total = 0; return false; }
            total = k + u;   // o tempo de kernel ja inclui o ocioso
            return true;
        }

        // Uma unica chamada ao kernel (SystemProcessInformation) traz memoria, CPU e faltas de pagina de todos os
        // processos, sem abrir handle em cada um. Offsets da SYSTEM_PROCESS_INFORMATION em x64.
        IntPtr spiBuf = IntPtr.Zero; int spiLen = 0;
        readonly object spiLock = new object();

        public List<RawProc> ReadProcs()
        {
            var list = new List<RawProc>();
            lock (spiLock)
            {
                if (spiBuf == IntPtr.Zero) { spiLen = 1 << 20; spiBuf = Marshal.AllocHGlobal(spiLen); }
                int need, st;
                while ((st = WinNative.NtQuerySystemInformation(5, spiBuf, spiLen, out need)) == unchecked((int)0xC0000004))
                {
                    Marshal.FreeHGlobal(spiBuf); spiLen = Math.Max(need, spiLen) + (256 << 10); spiBuf = Marshal.AllocHGlobal(spiLen);
                }
                if (st != 0) return list;
                long off = 0;
                while (true)
                {
                    IntPtr e = new IntPtr(spiBuf.ToInt64() + off);
                    int next = Marshal.ReadInt32(e, 0x00);
                    int pid = (int)Marshal.ReadIntPtr(e, 0x50).ToInt64();
                    if (pid != 0)
                    {
                        int nameLen = Marshal.ReadInt16(e, 0x38) & 0xFFFF;
                        IntPtr nameBuf = Marshal.ReadIntPtr(e, 0x40);
                        string name = nameBuf != IntPtr.Zero && nameLen > 0 ? Marshal.PtrToStringUni(nameBuf, nameLen / 2) : "pid" + pid;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                        list.Add(new RawProc
                        {
                            Name = name, Pid = pid, Parent = (int)Marshal.ReadIntPtr(e, 0x58).ToInt64(),
                            StartKey = Marshal.ReadInt64(e, 0x20),
                            CpuTime100ns = Marshal.ReadInt64(e, 0x28) + Marshal.ReadInt64(e, 0x30),
                            Faults = (uint)Marshal.ReadInt32(e, 0x10),
                            Ws = Marshal.ReadIntPtr(e, 0x90).ToInt64(), Priv = Marshal.ReadIntPtr(e, 0xC8).ToInt64()
                        });
                    }
                    if (next == 0) break;
                    off += next;
                }
            }
            return list;
        }

        public Dictionary<int, string> Services(out Dictionary<int, string> roots)
        {
            var svc = new Dictionary<int, string>(); roots = new Dictionary<int, string>();
            using (var q = new ManagementObjectSearcher("SELECT Name, DisplayName, ProcessId, PathName FROM Win32_Service WHERE ProcessId > 0"))
                foreach (ManagementObject o in q.Get())
                {
                    int pid = Convert.ToInt32(o["ProcessId"]); string n = Convert.ToString(o["Name"]), path = Convert.ToString(o["PathName"]) ?? "";
                    string dn = Convert.ToString(o["DisplayName"]) ?? "";
                    string label = dn.Length > 0 && !string.Equals(dn, n, StringComparison.OrdinalIgnoreCase) ? n + " (" + dn + ")" : n;
                    string cur; svc[pid] = svc.TryGetValue(pid, out cur) ? cur + ", " + label : label;
                    // Teramind: servico tsvchst (e tudo que ele inicia, inclusive o dwm.exe falso)
                    if (string.Equals(n, "tsvchst", StringComparison.OrdinalIgnoreCase) || path.IndexOf("{4CEC2908-5CE4-48F0-A717-8FC833D8017A}", StringComparison.OrdinalIgnoreCase) >= 0)
                        roots[pid] = "Teramind (monitoramento)";
                }
            return svc;
        }

        // --- eventos (Visualizador de Eventos) ---
        const string SystemFilter =
            "(Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004)" +
            " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)" +
            " or (Provider[@Name='EventLog'] and EventID=6008)" +
            " or (Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034))" +
            " or (Provider[@Name='Microsoft-Windows-WHEA-Logger'])" +
            " or (Provider[@Name='Display'] and EventID=4101)" +
            " or (Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=37)";
        const string AppFilter =
            "(Provider[@Name='Application Error'] and EventID=1000)" +
            " or (Provider[@Name='Application Hang'] and EventID=1002)" +
            " or (Provider[@Name='Dwminit'])";

        static string Category(string prov)
        {
            if (prov.Contains("Resource-Exhaustion")) return "mem";
            if (prov == "Dwminit") return "dwm";
            if (prov == "Application Error") return "crash";
            if (prov == "Application Hang") return "hang";
            if (prov == "Service Control Manager") return "svc";
            return "crit";
        }

        static Dictionary<string, object> ToRow(EventRecord r)
        {
            string msg = "";
            try { msg = r.FormatDescription() ?? ""; } catch { }
            msg = Regex.Replace(msg, @"\s+", " ").Trim();
            if (msg.Length > 600) msg = msg.Substring(0, 600);
            return new Dictionary<string, object> {
                { "t", Fmt.Ts(r.TimeCreated ?? DateTime.Now) }, { "prov", r.ProviderName }, { "id", r.Id }, { "c", Category(r.ProviderName) }, { "msg", msg } };
        }

        public List<Dictionary<string, object>> ReadEvents(DateTime from)
        {
            var list = new List<Dictionary<string, object>>();
            long ms = (long)(DateTime.Now - from).TotalMilliseconds;
            foreach (var pair in new[] { new[] { "System", SystemFilter }, new[] { "Application", AppFilter } })
            {
                string xpath = "*[System[(" + pair[1] + ") and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]";
                try
                {
                    using (var reader = new EventLogReader(new EventLogQuery(pair[0], PathType.LogName, xpath)))
                    {
                        EventRecord rec;
                        while ((rec = reader.ReadEvent()) != null) using (rec) list.Add(ToRow(rec));
                    }
                }
                catch { }
            }
            return list.OrderBy(e => (string)e["t"]).ToList();
        }

        class Watchers : IDisposable
        {
            public List<EventLogWatcher> W = new List<EventLogWatcher>();
            public void Dispose() { foreach (var w in W) { try { w.Enabled = false; w.Dispose(); } catch { } } }
        }

        public IDisposable WatchEvents(Action<Dictionary<string, object>> onEvent)
        {
            var ws = new Watchers();
            foreach (var pair in new[] { new[] { "System", SystemFilter }, new[] { "Application", AppFilter } })
            {
                try
                {
                    var w = new EventLogWatcher(new EventLogQuery(pair[0], PathType.LogName, "*[System[" + pair[1] + "]]"));
                    w.EventRecordWritten += (s, e) => { if (e.EventRecord != null) using (e.EventRecord) onEvent(ToRow(e.EventRecord)); };
                    w.Enabled = true;
                    ws.W.Add(w);
                }
                catch { }
            }
            return ws;
        }

        // --- informacoes ---
        static string Wmi(string cls, string prop)
        {
            try { using (var s = new ManagementObjectSearcher("SELECT " + prop + " FROM " + cls)) foreach (ManagementObject o in s.Get()) return Convert.ToString(o[prop]); }
            catch { }
            return "";
        }
        public string Model() { return Wmi("Win32_ComputerSystem", "Model"); }
        public string CpuName() { return Wmi("Win32_Processor", "Name"); }
        public string OsVersion() { return Wmi("Win32_OperatingSystem", "Caption") + " build " + Wmi("Win32_OperatingSystem", "BuildNumber"); }
        public DateTime? BootTime() { try { return ManagementDateTimeConverter.ToDateTime(Wmi("Win32_OperatingSystem", "LastBootUpTime")); } catch { return null; } }

        // --- instalacao ---
        static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EvidenceMonitor");
        public string InstallTarget { get { return Path.Combine(InstallDir, "EvidenceMonitor.exe"); } }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "EvidenceMonitor";

        public bool IsInstalled() { return string.Equals(Path.GetFullPath(Paths.Self), Path.GetFullPath(InstallTarget), StringComparison.OrdinalIgnoreCase); }

        public void Install()
        {
            Directory.CreateDirectory(InstallDir);
            if (!IsInstalled()) File.Copy(Paths.Self, InstallTarget, true);
            SetStartup(true);
        }

        public void LaunchInstalled(string args) { Process.Start(new ProcessStartInfo(InstallTarget, args ?? "") { UseShellExecute = false }); }

        public bool StartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }

        public void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + (IsInstalled() || !File.Exists(InstallTarget) ? Paths.Self : InstallTarget) + "\""); else k.DeleteValue(RunName, false);
            }
        }

        public bool IsElevated()
        {
            try { using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // --- acoes ---
        static string Sys(string exe) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe); }
        static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        // Pasta Downloads do usuario (FOLDERID_Downloads); cai para %USERPROFILE%\Downloads se a API falhar
        public string Downloads()
        {
            IntPtr p;
            if (WinNative.SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
            {
                string d = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(d)) return d;
            }
            return Path.Combine(Paths.Home, "Downloads");
        }

        public void OpenFolder(string dir) { Process.Start(Explorer, "\"" + dir + "\""); }
        public void Reveal(string file) { Process.Start(Explorer, "/select,\"" + file + "\""); }
        public void OpenFile(string file) { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }

        public bool RebootScheduled(out string error)
        {
            error = Proc.Run(Sys("shutdown.exe"), "/r /f /t 30 /d p:4:1 /c \"Monitor de Evidencias: reinicio forcado para liberar memoria/processo preso\"");
            return error == "ok";
        }
        public bool CancelReboot() { return Proc.Run(Sys("shutdown.exe"), "/a") == "ok"; }

        static readonly Regex SafeService = new Regex(@"^[A-Za-z0-9_.$\-]{1,80}$");
        public string KillServiceCommand(int pid)
        {
            using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE ProcessId = " + pid.ToString(CultureInfo.InvariantCulture)))
            {
                var names = s.Get().Cast<ManagementObject>().Select(o => Convert.ToString(o["Name"])).Where(n => SafeService.IsMatch(n)).ToArray();
                return names.Length > 0 ? string.Join(Environment.NewLine, names.Select(n => "Restart-Service " + n + " -Force")) : null;
            }
        }
        public string ProcessStartStamp(int pid) { return null; }

        public bool OpenAdminTerminal(out string error)
        {
            error = null;
            try
            {
                Process.Start(new ProcessStartInfo(Sys(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoExit -NoProfile") { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode != 1223) error = ex.Message; return false; }
        }

        public string WslShutdown() { return Proc.Run(Sys("wsl.exe"), "--shutdown"); }

        public string RestartDocker(out string elevated)
        {
            elevated = null;
            var killed = new List<string>();
            foreach (var n in new[] { "Docker Desktop", "com.docker.backend", "com.docker.build" })
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { p.Kill(); killed.Add(n + ":" + p.Id); } catch { }
                    finally { p.Dispose(); }
                }
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\Docker Desktop.exe");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(5000);
                if (File.Exists(exe)) { try { Process.Start(exe); } catch { } }
            });
            return "processos encerrados: " + killed.Count;
        }

        public static void HardenDllSearch() { try { WinNative.SetDefaultDllDirectories(0x00000800); } catch { } }
    }

    // Executa um programa do sistema (sempre por caminho absoluto) e devolve "ok" ou o erro
    static class Proc
    {
        public static string Run(string exe, string args, int timeoutMs = 60000)
        {
            string o; return Run(exe, args, out o, timeoutMs);
        }

        public static string Run(string exe, string args, out string stdout, int timeoutMs = 60000)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "tempo esgotado"; }
                    stdout = outTask.Result;
                    return p.ExitCode == 0 ? "ok" : "código " + p.ExitCode;
                }
            }
            catch (Exception ex) { return "falhou: " + ex.Message; }
        }

        public static string Run(string exe, IEnumerable<string> args, out string stdout, int timeoutMs = 60000)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "tempo esgotado"; }
                    stdout = outTask.Result;
                    return p.ExitCode == 0 ? "ok" : "código " + p.ExitCode;
                }
            }
            catch (Exception ex) { return "falhou: " + ex.Message; }
        }

        // primeiro caminho absoluto que existir
        public static string Find(params string[] paths) { return paths.FirstOrDefault(File.Exists); }
    }
}
