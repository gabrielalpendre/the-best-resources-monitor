// macOS: libproc/mach para processos e memoria, sysctl para hardware, DiagnosticReports para crashes/jetsam/panic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EvidenceMonitor
{
    [SupportedOSPlatform("macos")]
    static unsafe class MacNative
    {
        const string Lib = "/usr/lib/libSystem.dylib";
        [DllImport(Lib)] public static extern int proc_listallpids(int* buffer, int buffersize);
        [DllImport(Lib)] public static extern int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int buffersize);
        [DllImport(Lib)] public static extern int proc_pid_rusage(int pid, int flavor, void* buffer);
        [DllImport(Lib)] public static extern int proc_name(int pid, byte* buffer, uint buffersize);
        [DllImport(Lib)] public static extern int sysctlbyname(string name, void* oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);
        [DllImport(Lib)] public static extern uint mach_host_self();
        [DllImport(Lib)] public static extern int host_statistics(uint host, int flavor, int* info, ref uint count);
        [DllImport(Lib)] public static extern int host_statistics64(uint host, int flavor, int* info, ref uint count);
        [DllImport(Lib)] public static extern int host_page_size(uint host, out UIntPtr size);
        [DllImport(Lib, EntryPoint = "mach_timebase_info")] public static extern int mach_timebase_info2(uint* info);

        public const int PROC_PIDTASKINFO = 4, PROC_PIDT_SHORTBSDINFO = 13, RUSAGE_INFO_V2 = 2;
        public const int HOST_CPU_LOAD_INFO = 3, HOST_VM_INFO64 = 4;

        public static string Str(string name)
        {
            IntPtr len = IntPtr.Zero;
            if (sysctlbyname(name, null, ref len, IntPtr.Zero, IntPtr.Zero) != 0 || len == IntPtr.Zero) return "";
            var buf = new byte[(int)len];
            fixed (byte* p = buf) if (sysctlbyname(name, p, ref len, IntPtr.Zero, IntPtr.Zero) != 0) return "";
            return Encoding.UTF8.GetString(buf, 0, Math.Max(0, (int)len - 1)).TrimEnd('\0');
        }

        public static long Long(string name)
        {
            long v = 0; IntPtr len = (IntPtr)8;
            if (sysctlbyname(name, &v, ref len, IntPtr.Zero, IntPtr.Zero) != 0) return -1;
            return (int)len == 4 ? (int)v : v;
        }

        public static bool Bytes(string name, byte[] buf)
        {
            IntPtr len = (IntPtr)buf.Length;
            fixed (byte* p = buf) return sysctlbyname(name, p, ref len, IntPtr.Zero, IntPtr.Zero) == 0;
        }
    }

    [SupportedOSPlatform("macos")]
    unsafe class MacPlatform : IPlatform
    {
        public string Os { get { return "macos"; } }
        public string OsName { get { return "macOS"; } }
        public string RebootWord { get { return "o Mac"; } }
        public string AdminTerminalLabel { get { return "Abrir Terminal"; } }
        public bool HasWsl { get { return false; } }
        public string WslShutdown() { return "não disponível no macOS"; }
        public string EventsHint { get { return null; } }

        static readonly Dictionary<string, string> Labels = new Dictionary<string, string> {
            { "mem", "Memória esgotada (jetsam)" }, { "dwm", "Tela travou (WindowServer)" }, { "crash", "App falhou" },
            { "hang", "App sem resposta" }, { "svc", "Serviço caiu (launchd)" }, { "crit", "Crítico (kernel panic/térmico)" } };
        public Dictionary<string, string> CatLabels { get { return Labels; } }

        static readonly Regex CriticalRx = new Regex(@"^(kernel_task|launchd|WindowServer|loginwindow|logd|opendirectoryd|securityd|configd|notifyd|UserEventAgent|mds|mds_stores|coreservicesd|syslogd|diskarbitrationd|powerd|hidd|cfprefsd|distnoted|trustd|kextd|watchdogd|Finder|Dock|SystemUIServer|ControlCenter)$", RegexOptions.IgnoreCase);
        public Regex Critical { get { return CriticalRx; } }
        static readonly Regex Vnic = new Regex(@"^(lo\d*|utun\d*|awdl\d*|llw\d*|bridge\d*|gif\d*|stf\d*|anpi\d*|ap\d*|vmenet\d*|vmnet\d*|ipsec\d*|ppp\d*)$", RegexOptions.IgnoreCase);
        public Regex VirtualNic { get { return Vnic; } }

        readonly uint host = MacNative.mach_host_self();
        readonly double absTo100ns;   // unidades de tempo absoluto do mach -> 100 ns
        readonly long pageSize;

        public MacPlatform()
        {
            uint* tb = stackalloc uint[2];
            absTo100ns = MacNative.mach_timebase_info2(tb) == 0 && tb[1] != 0 ? (double)tb[0] / tb[1] / 100.0 : 0.01;
            UIntPtr ps; pageSize = MacNative.host_page_size(host, out ps) == 0 ? (long)ps.ToUInt64() : Environment.SystemPageSize;
        }

        // --- metricas ---
        long[] prevVm; DateTime prevAt = DateTime.MinValue; long[] prevCpu;
        long diskTotal; long prevDiskTotal = -1; readonly object diskLock = new object();
        Dictionary<int, long[]> diskByPid = new Dictionary<int, long[]>();

        public bool CpuTimes(out long idle, out long total)
        {
            idle = total = 0;
            int* t = stackalloc int[4]; uint n = 4;
            if (MacNative.host_statistics(host, MacNative.HOST_CPU_LOAD_INFO, t, ref n) != 0) return false;
            // user, system, idle, nice (ticks acumulados, 32 bits sem sinal)
            idle = (uint)t[2]; total = (long)(uint)t[0] + (uint)t[1] + (uint)t[2] + (uint)t[3];
            return true;
        }

        public void ReadSystem(Dictionary<string, object> row)
        {
            var now = DateTime.Now;
            double el = prevAt == DateTime.MinValue ? 0 : (now - prevAt).TotalSeconds;
            long idle, total;
            if (CpuTimes(out idle, out total))
            {
                if (prevCpu != null && total > prevCpu[1]) row["cpu"] = Math.Round(100.0 * (1 - (double)(idle - prevCpu[0]) / (total - prevCpu[1])), 1);
                prevCpu = new[] { idle, total };
            }
            long fb = MacNative.Long("hw.cpufrequency");   // so existe em Mac Intel (frequencia nominal)
            row["freq_base"] = fb > 0 ? Math.Round(fb / 1e6) : -1;
            row["freq"] = -1;   // o macOS nao expoe o clock efetivo sem privilegio (powermetrics exige root)

            int* vm = stackalloc int[38]; uint cnt = 38;
            if (MacNative.host_statistics64(host, MacNative.HOST_VM_INFO64, vm, ref cnt) == 0)
            {
                byte* b = (byte*)vm;
                long free = *(uint*)(b + 0), inactive = *(uint*)(b + 8), speculative = *(uint*)(b + 92);
                long pageins = *(long*)(b + 32), pageouts = *(long*)(b + 40), swapins = *(long*)(b + 112), swapouts = *(long*)(b + 120);
                double ram = MacNative.Long("hw.memsize");
                double avail = (free + inactive + speculative) * (double)pageSize;
                var sw = new byte[32]; double swT = 0, swU = 0;
                if (MacNative.Bytes("vm.swapusage", sw)) { swT = BitConverter.ToUInt64(sw, 0); swU = BitConverter.ToUInt64(sw, 16); }
                if (ram > 0)
                {
                    // "comprometida" = RAM em uso + swap em uso, sobre RAM + swap (mesma regra do Linux)
                    double used = Math.Max(0, ram - avail) + swU, limit = ram + swT;
                    row["mem_avail"] = Math.Round(avail / 1048576, 0);
                    row["commit"] = Math.Round(100 * used / limit, 1);
                    row["commit_gb"] = Math.Round(used / 1073741824, 1);
                    row["limit_gb"] = Math.Round(limit / 1073741824, 1);
                    row["ram_gb"] = Math.Round(ram / 1073741824, 1);
                }
                var cur = new[] { pageins + pageouts + swapins + swapouts };
                if (prevVm != null && el > 0) row["pages"] = Math.Round(Math.Max(0, cur[0] - prevVm[0]) / el, 0);
                prevVm = cur;
            }

            // disco: soma das leituras/gravacoes de todos os processos (o macOS nao tem "% ocupado" sem privilegio)
            long dt; lock (diskLock) dt = diskTotal;
            if (prevDiskTotal >= 0 && el > 0) row["disk_mbs"] = Math.Round(Math.Max(0, dt - prevDiskTotal) / el / 1048576.0, 1);
            prevDiskTotal = dt; prevAt = now;
        }

        static string NameOf(int pid)
        {
            byte* buf = stackalloc byte[256];
            int n = MacNative.proc_name(pid, buf, 256);
            return n > 0 ? Encoding.UTF8.GetString(buf, n) : null;
        }

        readonly object procLock = new object();
        public List<RawProc> ReadProcs() { lock (procLock) return ReadProcsLocked(); }

        List<RawProc> ReadProcsLocked()
        {
            var list = new List<RawProc>();
            int n = MacNative.proc_listallpids(null, 0);
            if (n <= 0) return list;
            var pids = new int[n + 64];
            fixed (int* p = pids) n = MacNative.proc_listallpids(p, pids.Length * 4);
            byte* ru = stackalloc byte[160]; byte* ti = stackalloc byte[96]; byte* sb = stackalloc byte[64];
            var disk = new Dictionary<int, long[]>(); long diskDelta = 0;
            for (int i = 0; i < n; i++)
            {
                int pid = pids[i]; if (pid <= 0) continue;
                var r = new RawProc { Pid = pid };
                if (MacNative.proc_pidinfo(pid, MacNative.PROC_PIDT_SHORTBSDINFO, 0, sb, 64) == 64) r.Parent = (int)*(uint*)(sb + 4);
                r.Name = NameOf(pid);
                if (r.Name == null) { if (r.Parent == 0 && pid != 1) continue; r.Name = "pid" + pid; }
                if (MacNative.proc_pid_rusage(pid, MacNative.RUSAGE_INFO_V2, ru) == 0)
                {
                    r.CpuTime100ns = (long)((*(ulong*)(ru + 16) + *(ulong*)(ru + 24)) * absTo100ns);
                    r.Faults = (long)*(ulong*)(ru + 48);
                    r.Ws = (long)*(ulong*)(ru + 64);
                    r.Priv = (long)*(ulong*)(ru + 72);          // phys_footprint: o "Memória" do Monitor de Atividade
                    r.StartKey = (long)*(ulong*)(ru + 80);
                    long io = (long)(*(ulong*)(ru + 144) + *(ulong*)(ru + 152));
                    long[] o;
                    if (diskByPid.TryGetValue(pid, out o) && o[0] == r.StartKey && io >= o[1]) diskDelta += io - o[1];
                    disk[pid] = new[] { r.StartKey, io };
                }
                else if (MacNative.proc_pidinfo(pid, MacNative.PROC_PIDTASKINFO, 0, ti, 96) == 96)
                {
                    r.Ws = r.Priv = (long)*(ulong*)(ti + 8);
                    r.CpuTime100ns = (long)((*(ulong*)(ti + 16) + *(ulong*)(ti + 24)) * absTo100ns);
                    r.Faults = *(int*)(ti + 56);
                    r.StartKey = pid;
                }
                list.Add(r);
            }
            lock (diskLock) { diskTotal += diskDelta; diskByPid = disk; }
            return list;
        }

        static readonly string Launchctl = "/bin/launchctl";
        static readonly Regex AgentLabel = new Regex(@"teramind", RegexOptions.IgnoreCase);

        public Dictionary<int, string> Services(out Dictionary<int, string> roots)
        {
            var svc = new Dictionary<int, string>(); roots = new Dictionary<int, string>();
            string o; Proc.Run(Launchctl, new[] { "list" }, out o, 15000);
            foreach (var line in o.Split('\n').Skip(1))
            {
                var f = line.Split('\t'); int pid;
                if (f.Length < 3 || !int.TryParse(f[0], out pid) || pid <= 0) continue;
                string label = f[2].Trim();
                if (label.StartsWith("application.")) continue;   // apps abertos pelo usuario, nao servicos
                svc[pid] = label;
                if (AgentLabel.IsMatch(label)) roots[pid] = "Teramind (monitoramento)";
            }
            return svc;
        }

        // --- eventos: relatorios do DiagnosticReports (crash, jetsam, panic, hang) + limitacao termica (pmset) ---
        static readonly string[] ReportDirs = {
            Path.Combine(Paths.Home, "Library", "Logs", "DiagnosticReports"), "/Library/Logs/DiagnosticReports" };
        static readonly Regex ReportExt = new Regex(@"\.(ips|crash|hang|spin|panic)$", RegexOptions.IgnoreCase);

        static Dictionary<string, object> ParseReport(string path)
        {
            string name = Path.GetFileName(path), text;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[Math.Min(65536, fs.Length)]; int got = fs.Read(buf, 0, buf.Length);
                    text = Encoding.UTF8.GetString(buf, 0, got);
                }
            }
            catch { return null; }
            string head = text.Split('\n')[0];
            Func<string, string> H = k => { var m = Regex.Match(head, "\"" + k + "\"\\s*:\\s*\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : ""; };
            Func<string, string> B = rx => { var m = Regex.Match(text, rx); return m.Success ? m.Groups[1].Value : ""; };
            string bug = H("bug_type"), app = H("app_name"); if (app.Length == 0) app = H("name");
            string c, msg;
            if (name.StartsWith("JetsamEvent") || bug == "298")
            {
                c = "mem"; string big = B("\"largestProcess\"\\s*:\\s*\"([^\"]+)\"");
                msg = "Memória esgotada: o macOS encerrou processos (jetsam)" + (big.Length > 0 ? "; maior processo " + big : "");
            }
            else if (name.EndsWith(".panic") || name.Contains("panic") || bug == "210")
            { c = "crit"; msg = "Kernel panic: o Mac reiniciou sozinho. " + B("\"panicString\"\\s*:\\s*\"([^\"]{0,200})"); }
            else if (name.StartsWith("shutdownStall")) { c = "crit"; msg = "Desligamento travado (shutdown stall)"; }
            else if (Regex.IsMatch(name, @"\.(hang|spin)$")) { c = name.StartsWith("WindowServer") ? "dwm" : "hang"; msg = (app.Length > 0 ? app : name.Split('_', '-')[0]) + " ficou sem resposta"; }
            else if (bug == "309" || name.EndsWith(".crash"))
            {
                c = name.StartsWith("WindowServer") ? "dwm" : "crash";
                string ex = B("\"exception\"\\s*:\\s*\\{[^}]*\"type\"\\s*:\\s*\"([^\"]+)\""), sig = B("\"signal\"\\s*:\\s*\"([^\"]+)\"");
                if (ex.Length == 0) ex = B(@"Exception Type:\s*(\S+)");
                msg = (app.Length > 0 ? app : name.Split('-')[0]) + " falhou" + (ex.Length > 0 ? ": " + ex : "") + (sig.Length > 0 ? " (" + sig + ")" : "");
            }
            else return null;
            DateTime t = File.GetLastWriteTime(path);
            var tm = Regex.Match(H("timestamp"), @"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)(?:\.\d+)? ([+-]\d\d)(\d\d)$");
            DateTimeOffset dto;
            if (tm.Success && DateTimeOffset.TryParse(tm.Groups[1].Value + " " + tm.Groups[2].Value + ":" + tm.Groups[3].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) t = dto.LocalDateTime;
            msg = Regex.Replace(msg, @"\s+", " ").Trim();
            if (msg.Length > 600) msg = msg.Substring(0, 600);
            return new Dictionary<string, object> { { "t", Fmt.Ts(t) }, { "prov", "DiagnosticReports" }, { "id", 0 }, { "c", c }, { "msg", msg }, { "file", name } };
        }

        static IEnumerable<string> ReportFiles(DateTime from)
        {
            foreach (var d in ReportDirs)
            {
                string[] files;
                try { files = Directory.GetFiles(d); } catch { continue; }
                foreach (var f in files)
                {
                    if (!ReportExt.IsMatch(f)) continue;
                    DateTime w; try { w = File.GetLastWriteTime(f); } catch { continue; }
                    if (w >= from) yield return f;
                }
            }
        }

        public List<Dictionary<string, object>> ReadEvents(DateTime from)
        {
            var list = ReportFiles(from).Select(ParseReport).Where(e => e != null).ToList();
            lock (thermLock) list.AddRange(thermEvents.Where(e => Fmt.ParseTs(e["t"]) >= from));
            return list.OrderBy(e => (string)e["t"]).ToList();
        }

        static readonly object thermLock = new object();
        static readonly List<Dictionary<string, object>> thermEvents = new List<Dictionary<string, object>>();

        class ReportWatch : IDisposable
        {
            volatile bool stop;
            public ReportWatch(Action<Dictionary<string, object>> onEvent)
            {
                new Thread(() =>
                {
                    var seen = new HashSet<string>(ReportFiles(DateTime.Now.AddDays(-1)));
                    int lastLimit = 100; int tick = 0;
                    while (!stop)
                    {
                        Thread.Sleep(15000);
                        try
                        {
                            foreach (var f in ReportFiles(DateTime.Now.AddMinutes(-10)))
                                if (seen.Add(f)) { var e = ParseReport(f); if (e != null) onEvent(e); }
                            if (++tick % 4 == 0)   // a cada 1 min: limite de velocidade da CPU por temperatura
                            {
                                string o; Proc.Run("/usr/bin/pmset", new[] { "-g", "therm" }, out o, 10000);
                                var m = Regex.Match(o, @"CPU_Speed_Limit\s*=\s*(\d+)");
                                int lim = m.Success ? int.Parse(m.Groups[1].Value) : 100;
                                if (lim < 100 && lastLimit >= 100)
                                {
                                    var e = new Dictionary<string, object> { { "t", Fmt.Ts(DateTime.Now) }, { "prov", "pmset" }, { "id", 0 }, { "c", "crit" }, { "msg", "Limitação térmica: CPU limitada a " + lim + "% da velocidade" } };
                                    lock (thermLock) thermEvents.Add(e);
                                    onEvent(e);
                                }
                                lastLimit = lim;
                            }
                        }
                        catch { }
                    }
                }) { IsBackground = true, Name = "reports" }.Start();
            }
            public void Dispose() { stop = true; }
        }

        public IDisposable WatchEvents(Action<Dictionary<string, object>> onEvent) { return new ReportWatch(onEvent); }

        // --- informacoes ---
        public string Model() { return MacNative.Str("hw.model"); }
        public string CpuName() { return MacNative.Str("machdep.cpu.brand_string"); }
        public string OsVersion() { return "macOS " + MacNative.Str("kern.osproductversion") + " (" + MacNative.Str("kern.osversion") + ")"; }
        public DateTime? BootTime()
        {
            var b = new byte[16];
            return MacNative.Bytes("kern.boottime", b) ? DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(b, 0)).LocalDateTime : (DateTime?)null;
        }

        // --- instalacao: .app em /Applications (arrastado do .dmg) ou ~/Applications, e um LaunchAgent abre no login ---
        static readonly string UserApps = Path.Combine(Paths.Home, "Applications");
        static bool InAppsDir(string bundle)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(bundle).TrimEnd('/'));
            return parent == "/Applications" || parent == UserApps;
        }
        public string InstallTarget
        {
            get { var b = SelfBundle(); return b != null && InAppsDir(b) ? b : Path.Combine(UserApps, "EvidenceMonitor.app"); }
        }
        static string Plist { get { return Path.Combine(Paths.Home, "Library", "LaunchAgents", "local.evidencemonitor.plist"); } }

        // pasta .app que contem este executavel (ou null se rodando fora de um bundle)
        static string SelfBundle()
        {
            var d = Path.GetDirectoryName(Paths.Self);              // .../X.app/Contents/MacOS
            var app = d == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(d));
            return app != null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
        }

        public bool IsInstalled() { var b = SelfBundle(); return b != null && InAppsDir(b); }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        public void Install()
        {
            var b = SelfBundle();
            if (b != null && !IsInstalled())
            {
                if (Directory.Exists(InstallTarget)) Directory.Delete(InstallTarget, true);
                CopyDir(b, InstallTarget);
            }
            SetStartup(true);
        }

        static string InstalledExe(string bundle) { return Path.Combine(bundle, "Contents", "MacOS", "EvidenceMonitor"); }

        public void LaunchInstalled(string args)
        {
            var a = new List<string> { "-n", InstallTarget };
            if (!string.IsNullOrEmpty(args)) { a.Add("--args"); a.Add(args); }
            UnixCommon.Start("/usr/bin/open", a.ToArray());
        }

        public bool StartupEnabled() { return File.Exists(Plist); }

        public void SetStartup(bool on)
        {
            if (!on) { try { File.Delete(Plist); } catch { } return; }
            string exe = Directory.Exists(InstallTarget) ? InstalledExe(InstallTarget) : Paths.Self;
            string x = System.Security.SecurityElement.Escape(exe);
            Directory.CreateDirectory(Path.GetDirectoryName(Plist));
            File.WriteAllText(Plist,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\"><dict>\n  <key>Label</key><string>local.evidencemonitor</string>\n" +
                "  <key>ProgramArguments</key><array><string>" + x + "</string></array>\n" +
                "  <key>RunAtLoad</key><true/>\n  <key>ProcessType</key><string>Interactive</string>\n</dict></plist>\n");
        }

        public bool IsElevated() { return Environment.IsPrivilegedProcess; }

        // --- acoes ---
        const string Open = "/usr/bin/open";
        public string Downloads() { return Path.Combine(Paths.Home, "Downloads"); }
        public void OpenFolder(string dir) { UnixCommon.Start(Open, dir); }
        public void Reveal(string file) { UnixCommon.Start(Open, "-R", file); }
        public void OpenFile(string file) { UnixCommon.Start(Open, file); }

        public bool RebootScheduled(out string error)
        {
            // pede ao loginwindow um reinicio sem confirmacao (nao precisa de admin)
            return UnixCommon.ScheduleReboot(() =>
            {
                string o, r = Proc.Run("/usr/bin/osascript", new[] { "-e", "tell application \"loginwindow\" to «event aevtrrst»" }, out o);
                return r == "ok" ? r : Proc.Run("/usr/bin/osascript", new[] { "-e", "tell application \"System Events\" to restart" }, out o);
            }, out error);
        }
        public bool CancelReboot() { return UnixCommon.CancelReboot(); }

        public string KillServiceCommand(int pid) { return null; }
        public string ProcessStartStamp(int pid) { return UnixCommon.StartStamp(pid); }

        public bool OpenAdminTerminal(out string error)
        {
            error = null;
            if (UnixCommon.Start(Open, "-a", "Terminal")) return true;
            error = "Não foi possível abrir o Terminal."; return false;
        }

        public string RestartDocker(out string elevated)
        {
            elevated = null; int killed = 0;
            foreach (var n in new[] { "Docker Desktop", "com.docker.backend", "Docker" })
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(n))
                {
                    try { p.Kill(); killed++; } catch { }
                    finally { p.Dispose(); }
                }
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(5000); UnixCommon.Start(Open, "-a", "Docker"); });
            return "processos encerrados: " + killed;
        }
    }
}
