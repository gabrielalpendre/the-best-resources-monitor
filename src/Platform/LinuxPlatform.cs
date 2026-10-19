// Linux: /proc e /sys para metricas e processos, journald para eventos, systemd para servicos.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EvidenceMonitor
{
    class LinuxPlatform : IPlatform
    {
        public string Os { get { return "linux"; } }
        public string OsName { get { return "Linux"; } }
        public string RebootWord { get { return "o computador"; } }
        public string AdminTerminalLabel { get { return "Abrir terminal"; } }
        public bool HasWsl { get { return false; } }
        public string WslShutdown() { return "não disponível no Linux"; }

        static readonly Dictionary<string, string> Labels = new Dictionary<string, string> {
            { "mem", "Memória esgotada (OOM)" }, { "dwm", "Tela travou (servidor gráfico/GPU)" }, { "crash", "App falhou (core dump)" },
            { "hang", "Travamento (tarefa bloqueada)" }, { "svc", "Serviço caiu (systemd)" }, { "crit", "Crítico (hardware/térmico/kernel)" } };
        public Dictionary<string, string> CatLabels { get { return Labels; } }

        static readonly Regex CriticalRx = new Regex(@"^(systemd|systemd-.*|init|kthreadd|Xorg|Xwayland|gnome-shell|gnome-session.*|kwin_wayland|kwin_x11|plasmashell|ksmserver|mutter|dbus-daemon|dbus-broker.*|gdm.*|sddm.*|lightdm|login|sshd|polkitd|NetworkManager)$", RegexOptions.IgnoreCase);
        public Regex Critical { get { return CriticalRx; } }
        static readonly Regex Vnic = new Regex(@"^(lo|docker\d*|br-.*|veth.*|virbr.*|vnet\d*|vmnet\d*|vboxnet\d*|cni.*|flannel.*|cali.*|tun\d*|tap\d*|wg\d*|tailscale\d*|zt.*|lxcbr\d*|lxdbr\d*|podman\d*|sit\d*|ip6tnl.*)$", RegexOptions.IgnoreCase);
        public Regex VirtualNic { get { return Vnic; } }

        const double TickTo100ns = 1e7 / 100;   // USER_HZ = 100 nas arquiteturas comuns
        static readonly long PageSize = Environment.SystemPageSize;

        // --- metricas ---
        long[] prevCpu; long prevPages = -1; Dictionary<string, long[]> prevDisk; DateTime prevAt = DateTime.MinValue;

        static Dictionary<string, long> KeyVals(string path, char sep)
        {
            var d = new Dictionary<string, long>();
            var s = UnixCommon.Read(path); if (s == null) return d;
            foreach (var line in s.Split('\n'))
            {
                int i = sep == ':' ? line.IndexOf(':') : line.IndexOf(' ');
                if (i <= 0) continue;
                var v = line.Substring(i + 1).Trim().Split(' ')[0];
                long n; if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) d[line.Substring(0, i).Trim()] = n;
            }
            return d;
        }

        static long[] CpuLine()
        {
            var s = UnixCommon.ReadLine("/proc/stat");
            if (s == null || !s.StartsWith("cpu ")) return null;
            return s.Substring(4).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        public bool CpuTimes(out long idle, out long total)
        {
            idle = total = 0;
            var c = CpuLine(); if (c == null || c.Length < 4) return false;
            // user nice system idle iowait irq softirq steal (guest ja esta contado em user)
            idle = c[3] + (c.Length > 4 ? c[4] : 0);
            total = c.Take(Math.Min(8, c.Length)).Sum();
            return true;
        }

        static double Mhz()
        {
            try
            {
                var files = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*").Select(d => Path.Combine(d, "cpufreq", "scaling_cur_freq")).Where(File.Exists).ToList();
                if (files.Count > 0) return files.Select(f => double.Parse(UnixCommon.ReadLine(f), CultureInfo.InvariantCulture)).Average() / 1000.0;
            }
            catch { }
            var info = UnixCommon.Read("/proc/cpuinfo");
            if (info != null)
            {
                var m = Regex.Matches(info, @"^cpu MHz\s*:\s*([\d.]+)", RegexOptions.Multiline).Cast<Match>().Select(x => double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                if (m.Count > 0) return m.Average();
            }
            return -1;
        }

        static double baseMhz = double.NaN;
        static double BaseMhz()
        {
            if (!double.IsNaN(baseMhz)) return baseMhz;
            foreach (var f in new[] { "/sys/devices/system/cpu/cpu0/cpufreq/base_frequency", "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq" })
            {
                var s = UnixCommon.ReadLine(f); double v;
                if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return baseMhz = v / 1000.0;
            }
            return baseMhz = -1;
        }

        // discos fisicos: os de /sys/block que tem "device" (exclui loop, ram, zram e LVM/dm, que contariam em dobro)
        static HashSet<string> physDisks; static DateTime physAt = DateTime.MinValue;
        static HashSet<string> PhysDisks()
        {
            if (physDisks != null && (DateTime.Now - physAt).TotalMinutes < 5) return physDisks;
            var h = new HashSet<string>();
            try { foreach (var d in Directory.GetDirectories("/sys/block")) if (Directory.Exists(Path.Combine(d, "device"))) h.Add(Path.GetFileName(d)); } catch { }
            physAt = DateTime.Now;
            return physDisks = h;
        }

        public void ReadSystem(Dictionary<string, object> row)
        {
            var now = DateTime.Now;
            double el = prevAt == DateTime.MinValue ? 0 : (now - prevAt).TotalSeconds;

            var c = CpuLine();
            if (c != null && c.Length >= 4)
            {
                long idle = c[3] + (c.Length > 4 ? c[4] : 0), total = c.Take(Math.Min(8, c.Length)).Sum();
                if (prevCpu != null && total > prevCpu[1]) row["cpu"] = Math.Round(100.0 * (1 - (double)(idle - prevCpu[0]) / (total - prevCpu[1])), 1);
                prevCpu = new[] { idle, total };
            }
            row["freq"] = Math.Round(Mhz(), 0);
            row["freq_base"] = Math.Round(BaseMhz(), 0);

            var mi = KeyVals("/proc/meminfo", ':');
            long kb(string k) { long v; return mi.TryGetValue(k, out v) ? v : 0; }
            double totalKb = kb("MemTotal"), availKb = mi.ContainsKey("MemAvailable") ? kb("MemAvailable") : kb("MemFree") + kb("Cached") + kb("Buffers");
            double swapT = kb("SwapTotal"), swapUsed = swapT - kb("SwapFree");
            if (totalKb > 0)
            {
                // "comprometida" = RAM em uso + swap em uso, sobre RAM + swap (mesma regra do macOS)
                double used = totalKb - availKb + swapUsed, limit = totalKb + swapT;
                row["mem_avail"] = Math.Round(availKb / 1024, 0);
                row["commit"] = Math.Round(100 * used / limit, 1);
                row["commit_gb"] = Math.Round(used / 1048576, 1);
                row["limit_gb"] = Math.Round(limit / 1048576, 1);
                row["ram_gb"] = Math.Round(totalKb / 1048576, 1);
            }

            // paginacao = faltas que foram ao disco + paginas trocadas com o swap
            var vm = KeyVals("/proc/vmstat", ' ');
            long pages = (vm.TryGetValue("pgmajfault", out var a) ? a : 0) + (vm.TryGetValue("pswpin", out var b) ? b : 0) + (vm.TryGetValue("pswpout", out var d) ? d : 0);
            if (prevPages >= 0 && el > 0) row["pages"] = Math.Round(Math.Max(0, pages - prevPages) / el, 0);
            prevPages = pages;

            var disks = new Dictionary<string, long[]>(); var phys = PhysDisks();
            var ds = UnixCommon.Read("/proc/diskstats");
            if (ds != null)
                foreach (var line in ds.Split('\n'))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 14 || !phys.Contains(f[2])) continue;
                    long sectors = long.Parse(f[5], CultureInfo.InvariantCulture) + long.Parse(f[9], CultureInfo.InvariantCulture);
                    disks[f[2]] = new[] { sectors, long.Parse(f[12], CultureInfo.InvariantCulture), long.Parse(f[11], CultureInfo.InvariantCulture) };
                }
            if (prevDisk != null && el > 0 && disks.Count > 0)
            {
                double busy = 0, bytes = 0, q = 0;
                foreach (var kv in disks)
                {
                    long[] o; if (!prevDisk.TryGetValue(kv.Key, out o)) continue;
                    bytes += Math.Max(0, kv.Value[0] - o[0]) * 512.0;
                    busy = Math.Max(busy, Math.Max(0, kv.Value[1] - o[1]) / (el * 10.0));   // io_ticks em ms -> % (disco mais ocupado)
                    q += kv.Value[2];
                }
                row["disk_busy"] = Math.Round(Math.Min(100, busy), 1);
                row["disk_mbs"] = Math.Round(bytes / el / 1048576.0, 1);
                row["disk_q"] = q;
            }
            prevDisk = disks; prevAt = now;
        }

        public List<RawProc> ReadProcs()
        {
            var list = new List<RawProc>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                int pid; if (!int.TryParse(Path.GetFileName(dir), out pid)) continue;
                try
                {
                    string stat = File.ReadAllText(dir + "/stat");
                    int l = stat.IndexOf('('), r = stat.LastIndexOf(')');
                    if (l < 0 || r < l) continue;
                    string name = stat.Substring(l + 1, r - l - 1);
                    var f = stat.Substring(r + 2).Split(' ');
                    // f[0]=estado f[1]=ppid f[9]=majflt f[11]=utime f[12]=stime f[19]=starttime
                    long rssAnon = -1, swap = 0, rss = 0;
                    foreach (var line in File.ReadLines(dir + "/status"))
                    {
                        if (line.StartsWith("RssAnon:")) rssAnon = KbOf(line);
                        else if (line.StartsWith("VmSwap:")) swap = KbOf(line);
                        else if (line.StartsWith("VmRSS:")) rss = KbOf(line);
                    }
                    if (rssAnon < 0 && rss == 0) continue;   // thread do kernel: sem memoria propria
                    list.Add(new RawProc
                    {
                        Name = name, Pid = pid, Parent = int.Parse(f[1], CultureInfo.InvariantCulture),
                        StartKey = long.Parse(f[19], CultureInfo.InvariantCulture),
                        CpuTime100ns = (long)((long.Parse(f[11], CultureInfo.InvariantCulture) + long.Parse(f[12], CultureInfo.InvariantCulture)) * TickTo100ns),
                        Faults = long.Parse(f[9], CultureInfo.InvariantCulture),
                        Priv = (Math.Max(0, rssAnon) + swap) * 1024, Ws = rss * 1024
                    });
                }
                catch { }   // processo terminou no meio da leitura
            }
            return list;
        }

        static long KbOf(string line)
        {
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            long v; return p.Length >= 2 && long.TryParse(p[1], out v) ? v : 0;
        }

        static readonly Regex UnitRx = new Regex(@"/(?:system\.slice|user@\d+\.service/[^/\n]*)/(?:[^/\n]+\.slice/)*([A-Za-z0-9_.@\-\\]+\.service)\b");
        static readonly Regex SafeUnit = new Regex(@"^[A-Za-z0-9_.@\-]{1,100}\.service$");
        static readonly Regex AgentUnit = new Regex(@"teramind", RegexOptions.IgnoreCase);

        static string UnitOf(int pid, out bool system)
        {
            system = false;
            var cg = UnixCommon.Read("/proc/" + pid + "/cgroup"); if (cg == null) return null;
            var m = UnitRx.Match(cg); if (!m.Success) return null;
            system = m.Value.StartsWith("/system.slice");
            return m.Groups[1].Value;
        }

        public Dictionary<int, string> Services(out Dictionary<int, string> roots)
        {
            var svc = new Dictionary<int, string>(); roots = new Dictionary<int, string>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                int pid; if (!int.TryParse(Path.GetFileName(dir), out pid)) continue;
                bool sys; var u = UnitOf(pid, out sys); if (u == null) continue;
                svc[pid] = sys ? u : u + " (do usuário)";
                if (AgentUnit.IsMatch(u)) roots[pid] = "Teramind (monitoramento)";
            }
            return svc;
        }

        // --- eventos (journald) ---
        static readonly string Journalctl = Proc.Find("/usr/bin/journalctl", "/bin/journalctl");
        static readonly string[] Matches = { "_TRANSPORT=kernel", "+", "SYSLOG_IDENTIFIER=systemd-coredump", "+", "_PID=1", "+", "SYSLOG_IDENTIFIER=systemd-oomd" };

        static readonly Regex OomRx = new Regex(@"(Out of memory|Memory cgroup out of memory): Killed process \d+ \(([^)]+)\)|Killed .* due to memory pressure");
        static readonly Regex SegvRx = new Regex(@"segfault at|general protection fault|traps: .* trap ");
        static readonly Regex CoreRx = new Regex(@"Process \d+ \((.+?)\) of user \d+ dumped core");
        static readonly Regex GuiRx = new Regex(@"^(gnome-shell|Xorg|Xwayland|kwin_wayland|kwin_x11|plasmashell|mutter|sway|Hyprland|weston)$");
        static readonly Regex GpuRx = new Regex(@"GPU hang|ring \S+ timeout|\*ERROR\*.*(hang|timed out)|NVRM: Xid|amdgpu: .*reset|i915.*GPU HANG", RegexOptions.IgnoreCase);
        static readonly Regex HungRx = new Regex(@"task .+ blocked for more than \d+ seconds");
        static readonly Regex UnitFailRx = new Regex(@"^(.+?\.service): (Failed with result|Main process exited, code=(killed|dumped))");
        static readonly Regex CritRx = new Regex(@"Hardware Error|mce: \[|Machine check|temperature above threshold|cpu clock throttled|critical temperature|Kernel panic|watchdog: BUG: soft lockup", RegexOptions.IgnoreCase);

        static string Str(object v)
        {
            if (v is string s) return s;
            if (v is List<object> bytes) { try { return Encoding.UTF8.GetString(bytes.Select(x => (byte)Fmt.D(x)).ToArray()); } catch { } }
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static Dictionary<string, object> ToEvent(Dictionary<string, object> j)
        {
            string msg = Str(j.GetValueOrDefault("MESSAGE")), ident = Str(j.GetValueOrDefault("SYSLOG_IDENTIFIER"));
            string transport = Str(j.GetValueOrDefault("_TRANSPORT"));
            if (msg.Length == 0) return null;
            string c = null, prov = transport == "kernel" ? "kernel" : ident.Length > 0 ? ident : "systemd";
            Match m;
            if (OomRx.IsMatch(msg)) c = "mem";
            else if ((m = CoreRx.Match(msg)).Success) c = GuiRx.IsMatch(m.Groups[1].Value) ? "dwm" : "crash";
            else if (transport == "kernel" && GpuRx.IsMatch(msg)) c = "dwm";
            else if (transport == "kernel" && SegvRx.IsMatch(msg)) c = "crash";
            else if (transport == "kernel" && HungRx.IsMatch(msg)) c = "hang";
            else if (prov == "systemd" && (m = UnitFailRx.Match(msg)).Success && m.Groups[2].Value == "Failed with result") c = "svc";
            else if (transport == "kernel" && CritRx.IsMatch(msg)) c = "crit";
            if (c == null) return null;
            long us; DateTime t = long.TryParse(Str(j.GetValueOrDefault("__REALTIME_TIMESTAMP")), out us) ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000).LocalDateTime : DateTime.Now;
            msg = Regex.Replace(msg, @"\s+", " ").Trim();
            if (msg.Length > 600) msg = msg.Substring(0, 600);
            return new Dictionary<string, object> { { "t", Fmt.Ts(t) }, { "prov", prov }, { "id", 0 }, { "c", c }, { "msg", msg } };
        }

        public List<Dictionary<string, object>> ReadEvents(DateTime from)
        {
            var list = new List<Dictionary<string, object>>();
            if (Journalctl == null) return list;
            var args = new List<string> { "--since", from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "-o", "json", "--no-pager", "-q" };
            args.AddRange(Matches);
            string outp; Proc.Run(Journalctl, args, out outp, 120000);
            foreach (var line in outp.Split('\n'))
            {
                if (line.Length < 2) continue;
                try { var e = ToEvent(Json.ParseObj(line)); if (e != null) list.Add(e); } catch { }
            }
            return list.OrderBy(e => (string)e["t"]).ToList();
        }

        class JournalFollow : IDisposable
        {
            volatile bool stop; Process proc;
            public JournalFollow(Action<Dictionary<string, object>> onEvent)
            {
                new Thread(() =>
                {
                    while (!stop)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo(Journalctl) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                            foreach (var a in new[] { "-f", "-n", "0", "-o", "json", "-q" }.Concat(Matches)) psi.ArgumentList.Add(a);
                            proc = Process.Start(psi);
                            proc.StandardError.ReadToEndAsync();
                            string line;
                            while (!stop && (line = proc.StandardOutput.ReadLine()) != null)
                            {
                                try { var e = ToEvent(Json.ParseObj(line)); if (e != null) onEvent(e); } catch { }
                            }
                        }
                        catch { }
                        for (int i = 0; i < 60 && !stop; i++) Thread.Sleep(1000);   // journalctl caiu: tenta de novo em 1 min
                    }
                }) { IsBackground = true, Name = "journal" }.Start();
            }
            public void Dispose() { stop = true; try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { } }
        }

        public IDisposable WatchEvents(Action<Dictionary<string, object>> onEvent)
        {
            return Journalctl == null ? (IDisposable)new Noop() : new JournalFollow(onEvent);
        }

        string hint; bool hintDone;
        public string EventsHint
        {
            get
            {
                if (hintDone) return hint;
                hintDone = true;
                if (Journalctl == null) return hint = "journalctl não encontrado: eventos do sistema indisponíveis.";
                string o; Proc.Run(Journalctl, new[] { "-k", "-n", "1", "-q", "--no-pager", "-o", "cat" }, out o, 15000);
                if (string.IsNullOrWhiteSpace(o))
                    hint = "Sem permissão para ler o log do kernel. Rode: sudo usermod -aG systemd-journal $USER e entre de novo na sessão.";
                return hint;
            }
        }

        // --- informacoes ---
        public string Model()
        {
            string v = UnixCommon.ReadLine("/sys/class/dmi/id/sys_vendor"), p = UnixCommon.ReadLine("/sys/class/dmi/id/product_name");
            return ((v ?? "") + " " + (p ?? "")).Trim();
        }
        public string CpuName()
        {
            var m = Regex.Match(UnixCommon.Read("/proc/cpuinfo") ?? "", @"^model name\s*:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        public string OsVersion()
        {
            var m = Regex.Match(UnixCommon.Read("/etc/os-release") ?? "", "^PRETTY_NAME=\"?([^\"\\n]+)", RegexOptions.Multiline);
            return (m.Success ? m.Groups[1].Value : "Linux") + " · kernel " + (UnixCommon.ReadLine("/proc/sys/kernel/osrelease") ?? "");
        }
        public DateTime? BootTime()
        {
            var m = Regex.Match(UnixCommon.Read("/proc/stat") ?? "", @"^btime (\d+)", RegexOptions.Multiline);
            return m.Success ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).LocalDateTime : (DateTime?)null;
        }

        // --- instalacao: binario em ~/.local/share/EvidenceMonitor/app, atalho no menu e autostart do XDG ---
        static string ConfigHome { get { var x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"); return string.IsNullOrEmpty(x) ? Path.Combine(Paths.Home, ".config") : x; } }
        static string DataHome { get { var x = Environment.GetEnvironmentVariable("XDG_DATA_HOME"); return string.IsNullOrEmpty(x) ? Path.Combine(Paths.Home, ".local", "share") : x; } }
        // pacote .deb: binario em /opt e autostart do sistema em /etc/xdg/autostart
        const string DebExe = "/opt/evidence-monitor/evidence-monitor", SystemAutostart = "/etc/xdg/autostart/evidence-monitor.desktop";
        static bool FromDeb { get { return Path.GetFullPath(Paths.Self) == DebExe; } }
        public string InstallTarget { get { return FromDeb ? DebExe : Path.Combine(Paths.Root, "app", "evidence-monitor"); } }
        static string AutostartFile { get { return Path.Combine(ConfigHome, "autostart", "evidence-monitor.desktop"); } }
        static string MenuFile { get { return Path.Combine(DataHome, "applications", "evidence-monitor.desktop"); } }

        public bool IsInstalled() { return Path.GetFullPath(Paths.Self) == Path.GetFullPath(InstallTarget); }

        static string Desktop(string exe, bool autostart)
        {
            string q = "\"" + exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
            return "[Desktop Entry]\nType=Application\nName=Monitor de Evidências\nComment=Coleta evidências de lentidão e travamento\nExec=" + q +
                   "\nTerminal=false\nCategories=System;Monitor;\n" + (autostart ? "X-GNOME-Autostart-enabled=true\n" : "");
        }

        public void Install()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstallTarget));
            if (!IsInstalled())
            {
                File.Copy(Paths.Self, InstallTarget, true);
                File.SetUnixFileMode(InstallTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            if (!FromDeb) try { Directory.CreateDirectory(Path.GetDirectoryName(MenuFile)); File.WriteAllText(MenuFile, Desktop(InstallTarget, false)); } catch { }
            SetStartup(true);
        }

        public void LaunchInstalled(string args) { UnixCommon.Start(InstallTarget, string.IsNullOrEmpty(args) ? new string[0] : new[] { args }); }

        // o arquivo do usuario tem prioridade sobre o do sistema; "Hidden=true" nele desliga o autostart do pacote
        public bool StartupEnabled()
        {
            if (File.Exists(AutostartFile)) return !(UnixCommon.Read(AutostartFile) ?? "").Contains("Hidden=true");
            return File.Exists(SystemAutostart);
        }

        public void SetStartup(bool on)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile));
            if (File.Exists(SystemAutostart))
            {
                if (on) { try { File.Delete(AutostartFile); } catch { } }
                else File.WriteAllText(AutostartFile, "[Desktop Entry]\nType=Application\nName=Monitor de Evidências\nHidden=true\n");
                return;
            }
            if (!on) { try { File.Delete(AutostartFile); } catch { } return; }
            File.WriteAllText(AutostartFile, Desktop(File.Exists(InstallTarget) ? InstallTarget : Paths.Self, true));
        }
        public bool IsElevated() { return Environment.IsPrivilegedProcess; }

        // --- acoes ---
        static readonly string XdgOpen = Proc.Find("/usr/bin/xdg-open", "/bin/xdg-open");

        public string Downloads()
        {
            var dirs = UnixCommon.Read(Path.Combine(ConfigHome, "user-dirs.dirs"));
            if (dirs != null)
            {
                var m = Regex.Match(dirs, "^XDG_DOWNLOAD_DIR=\"([^\"]+)\"", RegexOptions.Multiline);
                if (m.Success)
                {
                    string d = m.Groups[1].Value.Replace("$HOME", Paths.Home);
                    if (Path.IsPathRooted(d) && d.TrimEnd('/') != Paths.Home.TrimEnd('/')) return d;
                }
            }
            return Path.Combine(Paths.Home, "Downloads");
        }

        public void OpenFolder(string dir) { UnixCommon.Start(XdgOpen, dir); }
        public void Reveal(string file) { UnixCommon.Start(XdgOpen, Path.GetDirectoryName(file)); }
        public void OpenFile(string file) { UnixCommon.Start(XdgOpen, file); }

        static readonly string Systemctl = Proc.Find("/usr/bin/systemctl", "/bin/systemctl");
        public bool RebootScheduled(out string error)
        {
            if (Systemctl == null) { error = "systemctl não encontrado"; return false; }
            // reinicio forcado via logind (sem senha para o usuario da sessao ativa); ignora inibidores
            return UnixCommon.ScheduleReboot(() => Proc.Run(Systemctl, "reboot -i"), out error);
        }
        public bool CancelReboot() { return UnixCommon.CancelReboot(); }

        public string KillServiceCommand(int pid)
        {
            bool sys; var u = UnitOf(pid, out sys);
            return sys && u != null && SafeUnit.IsMatch(u) ? "sudo systemctl restart " + u : null;
        }
        public string ProcessStartStamp(int pid) { return UnixCommon.StartStamp(pid); }

        public bool OpenAdminTerminal(out string error)
        {
            error = null;
            var t = Proc.Find("/usr/bin/x-terminal-emulator", "/usr/bin/gnome-terminal", "/usr/bin/konsole", "/usr/bin/xfce4-terminal", "/usr/bin/tilix", "/usr/bin/kitty", "/usr/bin/alacritty", "/usr/bin/xterm");
            if (t == null || !UnixCommon.Start(t)) { error = "Nenhum terminal encontrado. Abra um terminal e cole o comando."; return false; }
            return true;
        }

        public string RestartDocker(out string elevated)
        {
            elevated = null;
            if (Systemctl != null && Proc.Run(Systemctl, "--user cat docker-desktop", 10000) == "ok")
                return "Docker Desktop: " + Proc.Run(Systemctl, "--user restart docker-desktop");
            elevated = "sudo systemctl restart docker";
            return "o Docker roda como serviço do sistema";
        }
    }
}
