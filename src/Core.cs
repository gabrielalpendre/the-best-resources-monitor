// Monitor de Evidencias - nucleo comum aos tres sistemas: armazenamento, amostragem, agentes, picos e relatorio.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace EvidenceMonitor
{
    class ProcInfo { public string Name; public int Pid, Parent; public long StartKey; public long Priv; public long Ws; public double Cpu; public double HardFaults; public string Agent, Service; }

    // JSON minimo, sem reflexao: objetos viram Dictionary<string, object>, listas List<object>, numeros double.
    static class Json
    {
        public static string Serialize(object o) { var sb = new StringBuilder(); Write(sb, o); return sb.ToString(); }

        static void Write(StringBuilder sb, object o)
        {
            switch (o)
            {
                case null: sb.Append("null"); break;
                case string s: Str(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: Num(sb, d); break;
                case float f: Num(sb, f); break;
                case int or long or short or byte or uint or ulong: sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary dict:
                    sb.Append('{'); bool first = true;
                    foreach (DictionaryEntry kv in dict) { if (!first) sb.Append(','); first = false; Str(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture)); sb.Append(':'); Write(sb, kv.Value); }
                    sb.Append('}'); break;
                case IEnumerable list:
                    sb.Append('['); bool f1 = true;
                    foreach (var x in list) { if (!f1) sb.Append(','); f1 = false; Write(sb, x); }
                    sb.Append(']'); break;
                default: Str(sb, Convert.ToString(o, CultureInfo.InvariantCulture)); break;
            }
        }

        static void Num(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break;   // evita "</script>" dentro do relatorio
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        public static object Parse(string s)
        {
            using (var doc = JsonDocument.Parse(s)) return Conv(doc.RootElement);
        }

        public static Dictionary<string, object> ParseObj(string s) { return Parse(s) as Dictionary<string, object>; }

        static object Conv(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: var d = new Dictionary<string, object>(); foreach (var p in e.EnumerateObject()) d[p.Name] = Conv(p.Value); return d;
                case JsonValueKind.Array: var l = new List<object>(); foreach (var x in e.EnumerateArray()) l.Add(Conv(x)); return l;
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number: return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }

    static class Paths
    {
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "EvidenceMonitor");
        public static readonly string Data = Path.Combine(Root, "data");
        public static readonly string Reports = Path.Combine(Root, "relatorios");
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static string Day(string kind, DateTime d) { return Path.Combine(Data, kind + "-" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"); }
        public static string Self { get { return Environment.ProcessPath; } }
    }

    static class Store
    {
        static readonly object Lock = new object();

        public static void Append(string kind, object row)
        {
            string line = Json.Serialize(row) + "\n";
            lock (Lock)
            {
                Directory.CreateDirectory(Paths.Data);
                File.AppendAllText(Paths.Day(kind, DateTime.Now), line, new UTF8Encoding(false));
            }
        }

        public static List<Dictionary<string, object>> Read(string kind, DateTime from, DateTime to)
        {
            var list = new List<Dictionary<string, object>>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                string f = Paths.Day(kind, d);
                if (!File.Exists(f)) continue;
                string[] lines;
                lock (Lock) { using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n'); }
                foreach (var l in lines)
                {
                    if (l.Trim().Length == 0) continue;
                    try
                    {
                        var row = Json.ParseObj(l);
                        DateTime ts;
                        if (row != null && DateTime.TryParse(Fmt.S(row, "ts"), CultureInfo.InvariantCulture, DateTimeStyles.None, out ts) && ts >= from && ts <= to) list.Add(row);
                    }
                    catch { }
                }
            }
            return list;
        }

        public static void Cleanup(int keepDays)
        {
            try
            {
                if (!Directory.Exists(Paths.Data)) return;
                foreach (var f in Directory.GetFiles(Paths.Data, "*.jsonl"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
                if (Directory.Exists(Paths.Reports))
                    foreach (var f in Directory.GetFiles(Paths.Reports, "*.html"))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
            }
            catch { }
        }
    }

    static class Fmt
    {
        public static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
        public static string Ts(DateTime t) { return t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); }
        public static DateTime ParseTs(object s) { return DateTime.Parse(Convert.ToString(s, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture); }
        public static string S(Dictionary<string, object> e, string k) { object v; return e != null && e.TryGetValue(k, out v) && v != null ? Convert.ToString(v, Br) : ""; }
        public static double D(Dictionary<string, object> r, string k)
        {
            object v;
            if (r == null || !r.TryGetValue(k, out v) || v == null) return -1;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return -1; }
        }
        public static double D(object v) { if (v == null) return -1; try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return -1; } }
        public static string N(double v, int d) { return v.ToString("N" + d, Br); }
        public static string Dur(double sec)
        {
            if (sec < 60) return N(sec, 0) + " s";
            if (sec < 3600) return Math.Floor(sec / 60).ToString("N0", Br) + " min " + (sec % 60).ToString("00", Br) + " s";
            return Math.Floor(sec / 3600).ToString("N0", Br) + " h " + Math.Floor(sec % 3600 / 60).ToString("00", Br) + " min";
        }
    }

    // Identifica agentes corporativos (seguranca/monitoramento) e o servico que hospeda cada processo.
    // Servicos lidos a cada 5 min (barato); descendentes de um agente herdam o rotulo pela arvore de processos.
    static class Agents
    {
        static readonly object Lock = new object();
        static Dictionary<int, string> svcByPid = new Dictionary<int, string>();
        static Dictionary<int, string> agentRoots = new Dictionary<int, string>();
        static DateTime lastRefresh = DateTime.MinValue;
        // nomes nos tres sistemas (Windows / macOS / Linux)
        static readonly string[][] ByName = {
            new[] { "Microsoft Defender", @"^(MsMpEng|MsSense|NisSrv|SenseNdr|SenseCncProxy|SenseIR|SenseTracer|MpDefenderCoreService|SecurityHealthService|wdavdaemon.*|mdatp.*|Microsoft Defender.*|com\.microsoft\.wdav.*)$" },
            new[] { "CrowdStrike", @"^(CSFalcon|falcond|falcon-sensor|falcon-sensor-bpf|com\.crowdstrike|Falcon$)" },
            new[] { "Fortinet", @"^(Forti|fct)" },
            new[] { "Wazuh", @"^wazuh" },
            new[] { "Teramind (monitoramento)", @"teramind" },
        };
        static readonly Regex[] ByNameRx = ByName.Select(r => new Regex(r[1], RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

        public static bool IsCriticalName(string name) { return Plat.I.Critical.IsMatch(name ?? ""); }

        static void Refresh()
        {
            Dictionary<int, string> roots = new Dictionary<int, string>(), svc = new Dictionary<int, string>();
            try { svc = Plat.I.Services(out roots); } catch { }
            lock (Lock) { svcByPid = svc; agentRoots = roots; lastRefresh = DateTime.Now; }
        }

        public static void Annotate(List<ProcInfo> procs)
        {
            if ((DateTime.Now - lastRefresh).TotalMinutes >= 5) Refresh();
            Dictionary<int, string> svc, roots;
            lock (Lock) { svc = svcByPid; roots = agentRoots; }
            var byPid = new Dictionary<int, ProcInfo>();
            foreach (var p in procs) byPid[p.Pid] = p;
            foreach (var p in procs)
            {
                string sv; if (svc.TryGetValue(p.Pid, out sv)) p.Service = "serviço " + sv;
                else
                {
                    ProcInfo par;
                    if (p.Parent > 0 && byPid.TryGetValue(p.Parent, out par))
                    {
                        string psv; svc.TryGetValue(par.Pid, out psv);
                        p.Service = "filho de " + par.Name + " (PID " + par.Pid + ")" + (psv != null ? " · serviço " + psv : "");
                    }
                }
                for (int i = 0; i < ByName.Length; i++) if (ByNameRx[i].IsMatch(p.Name)) { p.Agent = ByName[i][0]; break; }
                if (p.Agent != null) continue;
                // sobe ate 6 niveis na arvore procurando um agente raiz
                var cur = p;
                for (int i = 0; i < 6 && cur != null; i++)
                {
                    string lab; if (roots.TryGetValue(cur.Pid, out lab)) { p.Agent = lab; break; }
                    ProcInfo par; cur = cur.Parent > 0 && cur.Parent != cur.Pid && byPid.TryGetValue(cur.Parent, out par) ? par : null;
                }
            }
        }
    }

    // Foto de todos os processos com CPU/faltas de pagina por delta desde a foto anterior desta instancia.
    class ProcSnap
    {
        Dictionary<int, long[]> prevCpu = new Dictionary<int, long[]>();
        DateTime prevAt = DateTime.MinValue;
        readonly int nproc = Environment.ProcessorCount;
        readonly bool publish;
        public ProcSnap(bool publish) { this.publish = publish; }
        // pid -> {StartKey, cpu, faltas} da ultima amostra; usado para conferir o processo antes de encerrar
        public static volatile Dictionary<int, long[]> LastSeen = new Dictionary<int, long[]>();

        public List<ProcInfo> Take(DateTime now)
        {
            var list = new List<ProcInfo>();
            var cur = new Dictionary<int, long[]>();
            double elapsed = prevAt == DateTime.MinValue ? 0 : (now - prevAt).TotalMilliseconds;
            List<RawProc> raw;
            try { raw = Plat.I.ReadProcs(); } catch { return list; }
            foreach (var r in raw)
            {
                var info = new ProcInfo { Name = r.Name, Pid = r.Pid, Parent = r.Parent, StartKey = r.StartKey, Ws = r.Ws, Priv = r.Priv };
                long[] old;
                if (elapsed > 0 && prevCpu.TryGetValue(r.Pid, out old) && old[0] == r.StartKey)
                {
                    info.Cpu = Math.Max(0, Math.Min(100, (r.CpuTime100ns - old[1]) / 10000.0 / elapsed / nproc * 100.0));
                    info.HardFaults = Math.Max(0, (r.Faults - old[2]) * 1000.0 / elapsed);
                }
                cur[r.Pid] = new[] { r.StartKey, r.CpuTime100ns, r.Faults };
                list.Add(info);
            }
            prevCpu = cur; prevAt = now; if (publish) LastSeen = cur;
            return list;
        }
    }

    class Sampler
    {
        readonly ProcSnap snap = new ProcSnap(true);
        readonly Dictionary<string, long[]> prevNet = new Dictionary<string, long[]>();
        DateTime prevNetAt = DateTime.MinValue;

        public Sampler() { try { var r = new Dictionary<string, object>(); Plat.I.ReadSystem(r); Net(); } catch { } }

        // "servico X (Nome), Y" -> "X"; "filho de ..." -> ""
        static string FirstService(string s) { if (s == null || !s.StartsWith("serviço ")) return ""; s = s.Substring(8); int i = s.IndexOfAny(new[] { ' ', ',' }); return i > 0 ? s.Substring(0, i) : s; }

        // Soma o trafego das interfaces fisicas (ignora loopback, tuneis e adaptadores virtuais de VM/containers,
        // para nao contar o mesmo trafego duas vezes). Devolve Mbit/s.
        double[] Net()
        {
            var now = DateTime.Now; double rx = 0, tx = 0; bool any = false;
            double el = prevNetAt == DateTime.MinValue ? 0 : (now - prevNetAt).TotalSeconds;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (Plat.I.VirtualNic.IsMatch(ni.Name) || Plat.I.VirtualNic.IsMatch(ni.Description ?? "")) continue;
                    long r, t;
                    try { var st = ni.GetIPStatistics(); r = st.BytesReceived; t = st.BytesSent; } catch { continue; }
                    long[] old;
                    if (el > 0 && prevNet.TryGetValue(ni.Id, out old))
                    {
                        // contador de 32 bits no macOS pode dar a volta: ignora deltas negativos
                        if (r >= old[0]) rx += (r - old[0]) / el;
                        if (t >= old[1]) tx += (t - old[1]) / el;
                        any = true;
                    }
                    prevNet[ni.Id] = new[] { r, t };
                }
            }
            catch { }
            prevNetAt = now;
            return any ? new[] { rx * 8 / 1e6, tx * 8 / 1e6 } : new[] { -1.0, -1.0 };
        }

        public Dictionary<string, object> Sample(out List<ProcInfo> procs)
        {
            var now = DateTime.Now;
            var row = new Dictionary<string, object>();
            row["ts"] = Fmt.Ts(now);
            foreach (var k in new[] { "cpu", "freq_base", "freq", "mem_avail", "commit", "pages", "disk_busy", "disk_mbs", "disk_q" }) row[k] = -1.0;
            try { Plat.I.ReadSystem(row); } catch { }
            var net = Net();
            row["net_rx"] = net[0] >= 0 ? R(net[0], 2) : -1;
            row["net_tx"] = net[1] >= 0 ? R(net[1], 2) : -1;

            procs = snap.Take(now);
            Agents.Annotate(procs);
            row["agents"] = procs.Where(p => p.Agent != null).GroupBy(p => p.Agent)
                .Select(g => new object[] { g.Key, R(g.Sum(p => p.Cpu), 1), R(g.Sum(p => p.Priv) / 1073741824.0, 2) }).ToArray();
            row["mem_top"] = procs.OrderByDescending(p => p.Priv).Take(5).Select(p => new object[] { p.Name, p.Pid, R(p.Priv / 1073741824.0, 2) }).ToArray();
            row["cpu_top"] = procs.OrderByDescending(p => p.Cpu).Take(5).Select(p => new object[] { p.Name, p.Pid, R(p.Cpu, 1), p.Agent ?? FirstService(p.Service) }).ToArray();
            row["hf_top"] = procs.Where(p => p.HardFaults >= 1).OrderByDescending(p => p.HardFaults).Take(3).Select(p => new object[] { p.Name, p.Pid, R(p.HardFaults, 0) }).ToArray();
            return row;
        }

        public static double Clamp(double v, double lo, double hi) { return v < 0 ? v : Math.Max(lo, Math.Min(hi, v)); }
        public static double R(double v, int d) { return Math.Round(v, d); }
    }

    // Vigia de picos: a cada 1 s le a CPU total (uma chamada barata). Se passar de 70%, tira a foto dos processos
    // naquele segundo e segue de segundo em segundo ate cair; grava o episodio em "picos-*.jsonl" com duracao,
    // pico, media e os responsaveis. Fora de pico so renova a foto-base a cada 5 s.
    class SpikeProbe
    {
        public const double Threshold = 70, Critical = 90;
        readonly ProcSnap snap = new ProcSnap(false);
        Thread thread; volatile bool stop;
        public Action<Dictionary<string, object>> OnSpike;

        class Acc { public string Name, Origin; public double Sum, Max; public int Seen; public HashSet<int> Pids = new HashSet<int>(); }

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "spike-probe", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }
        public void Stop() { stop = true; }

        void Loop()
        {
            long pi, pt; Plat.I.CpuTimes(out pi, out pt);
            snap.Take(DateTime.Now); DateTime lastSnap = DateTime.Now;
            bool inSpike = false; DateTime start = DateTime.MinValue, end = DateTime.MinValue;
            var samples = new List<object[]>(); var acc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
            double peak = 0, sum = 0; int below = 0;
            while (!stop)
            {
                Thread.Sleep(1000);
                try
                {
                    long i, t; if (!Plat.I.CpuTimes(out i, out t)) continue;
                    long di = i - pi, total = t - pt; pi = i; pt = t;
                    if (total <= 0) continue;
                    double cpu = Math.Max(0, Math.Min(100, 100.0 * (total - di) / total));
                    var now = DateTime.Now;
                    if (cpu >= Threshold || (inSpike && below < 1))
                    {
                        // queda de 1 s abaixo do limite nao encerra o pico (histerese de 2 s)
                        if (cpu < Threshold) below++; else below = 0;
                        if (!inSpike) { inSpike = true; start = now.AddSeconds(-1); samples.Clear(); acc.Clear(); peak = 0; sum = 0; }
                        if (cpu >= Threshold) end = now;
                        peak = Math.Max(peak, cpu); sum += cpu;
                        samples.Add(new object[] { now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Math.Round(cpu, 0) });
                        var procs = snap.Take(now); lastSnap = now;
                        Agents.Annotate(procs);
                        // soma as instancias de cada app neste segundo e guarda os 5 maiores
                        foreach (var g in procs.GroupBy(x => x.Name + "|" + (x.Agent ?? ""), StringComparer.OrdinalIgnoreCase)
                                               .Select(g => new { Key = g.Key, Cpu = g.Sum(x => x.Cpu), Items = g.ToList() })
                                               .OrderByDescending(g => g.Cpu).Take(5))
                        {
                            if (g.Cpu < 0.5) continue;
                            Acc a;
                            if (!acc.TryGetValue(g.Key, out a)) { a = new Acc { Name = g.Items[0].Name, Origin = g.Items[0].Agent ?? "" }; acc[g.Key] = a; }
                            a.Sum += g.Cpu; a.Max = Math.Max(a.Max, g.Cpu); a.Seen++;
                            foreach (var p in g.Items) if (p.Cpu >= 0.5) a.Pids.Add(p.Pid);
                        }
                    }
                    else
                    {
                        if (inSpike)
                        {
                            inSpike = false; below = 0;
                            int n = samples.Count;
                            var row = new Dictionary<string, object>();
                            row["ts"] = Fmt.Ts(start);
                            row["fim"] = Fmt.Ts(end);
                            row["dur_s"] = Math.Round((end - start).TotalSeconds, 0);
                            row["pico"] = Math.Round(peak, 0);
                            row["media"] = Math.Round(sum / n, 0);
                            row["nivel"] = peak >= Critical ? "critico" : "atencao";
                            row["amostras"] = samples.ToArray();
                            row["responsaveis"] = acc.Values.OrderByDescending(a => a.Sum).Take(8)
                                .Select(a => new object[] { a.Name, a.Origin, Math.Round(a.Sum / n, 1), Math.Round(a.Max, 1), Math.Round(100.0 * a.Seen / n, 0), string.Join(",", a.Pids) }).ToArray();
                            try { Store.Append("picos", row); } catch { }
                            if (OnSpike != null) try { OnSpike(row); } catch { }
                        }
                        if ((now - lastSnap).TotalSeconds >= 5) { snap.Take(now); lastSnap = now; }
                    }
                }
                catch { }
            }
        }
    }

    static class Report
    {
        public static string Build(int hours)
        {
            DateTime to = DateTime.Now, from = to.AddHours(-hours);
            var raw = Store.Read("metricas", from, to);
            int bucket = hours > 36 ? 120 : 0;
            var rows = new List<Dictionary<string, object>>();
            var procPeak = new Dictionary<string, double>();
            var procByRow = new List<Dictionary<string, double>>();

            Dictionary<string, object> acc = null; Dictionary<string, double> accProc = null; long accKey = -1; int accN = 0;
            Action flush = () =>
            {
                if (acc == null) return;
                if (accN > 1) acc["freq"] = Math.Round(Fmt.D(acc, "freq") / accN);
                rows.Add(acc); procByRow.Add(accProc);
            };
            foreach (var r in raw)
            {
                DateTime ts = Fmt.ParseTs(r["ts"]);
                var pm = new Dictionary<string, double>();
                object mt;
                if (r.TryGetValue("mem_top", out mt) && mt is List<object>)
                    foreach (List<object> e in (List<object>)mt)
                    {
                        string n = Convert.ToString(e[0]); double gb = Fmt.D(e[2]);
                        double cur; pm[n] = pm.TryGetValue(n, out cur) ? Math.Max(cur, gb) : gb;
                        procPeak[n] = procPeak.TryGetValue(n, out cur) ? Math.Max(cur, gb) : gb;
                    }
                string cpuTop = "";
                object ct;
                if (r.TryGetValue("cpu_top", out ct) && ct is List<object>)
                    cpuTop = string.Join(", ", ((List<object>)ct).Cast<List<object>>().Select(e => e[0] + "=" + Fmt.D(e[2]).ToString(CultureInfo.InvariantCulture) + "%"));

                long key = bucket > 0 ? (long)(ts - from).TotalSeconds / bucket : accKey + 1;
                if (bucket == 0 || key != accKey)
                {
                    flush();
                    acc = new Dictionary<string, object>();
                    acc["ts"] = r["ts"];
                    foreach (var k in new[] { "cpu", "freq", "freq_base", "mem_avail", "commit", "commit_gb", "limit_gb", "ram_gb", "pages", "disk_busy", "disk_mbs", "net_rx", "net_tx" }) acc[k] = Fmt.D(r, k);
                    acc["top"] = cpuTop;
                    accProc = pm; accKey = key; accN = 1;
                }
                else
                {
                    accN++;
                    foreach (var k in new[] { "cpu", "commit", "commit_gb", "pages", "disk_busy", "disk_mbs", "net_rx", "net_tx" }) acc[k] = Math.Max(Fmt.D(acc, k), Fmt.D(r, k));
                    acc["mem_avail"] = Math.Min(Fmt.D(acc, "mem_avail"), Fmt.D(r, "mem_avail"));
                    acc["freq"] = Fmt.D(acc, "freq") + Fmt.D(r, "freq");
                    if (Fmt.D(r, "cpu") >= Fmt.D(acc, "cpu")) acc["top"] = cpuTop;
                    foreach (var kv in pm) { double cur; accProc[kv.Key] = accProc.TryGetValue(kv.Key, out cur) ? Math.Max(cur, kv.Value) : kv.Value; }
                }
            }
            flush();

            var series = procPeak.OrderByDescending(kv => kv.Value).Take(5).Select(kv => new Dictionary<string, object> {
                { "name", kv.Key }, { "peak", kv.Value },
                { "v", procByRow.Select(pm => pm.ContainsKey(kv.Key) ? (object)pm[kv.Key] : null).ToArray() } }).ToList();

            var P = Plat.I;
            var info = new Dictionary<string, object>();
            info["host"] = Environment.MachineName;
            info["model"] = Safe(P.Model);
            info["cpu"] = Safe(P.CpuName);
            info["os"] = Safe(P.OsVersion);
            DateTime? boot = null; try { boot = P.BootTime(); } catch { }
            info["boot"] = boot.HasValue ? Fmt.Ts(boot.Value) : "";
            info["from"] = Fmt.Ts(from);
            info["to"] = Fmt.Ts(to);
            info["hours"] = hours;
            info["bucket"] = bucket;
            info["interval"] = Program.IntervalSec;
            info["platform"] = P.Os;
            info["osname"] = P.OsName;
            info["cats"] = P.CatLabels;

            var data = new Dictionary<string, object>();
            data["info"] = info;
            data["rows"] = rows;
            data["procSeries"] = series;
            List<Dictionary<string, object>> evs; try { evs = P.ReadEvents(from); } catch { evs = new List<Dictionary<string, object>>(); }
            data["events"] = evs;
            data["incidents"] = Store.Read("incidentes", from, to);
            data["actions"] = Store.Read("acoes", from, to);
            data["picos"] = Store.Read("picos", from, to);

            string tpl;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("EvidenceMonitor.report.html"))
            using (var sr = new StreamReader(s, Encoding.UTF8)) tpl = sr.ReadToEnd();
            string json = Json.Serialize(data);
            string dir = P.Downloads();
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "evidencias-" + Environment.MachineName + "-" + to.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + hours + "h.html");
            File.WriteAllText(file, tpl.Replace("/*__DATA__*/null", json), new UTF8Encoding(false));
            return file;
        }

        static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    }

    // Alvo de um encerramento que falhou por permissao. So guarda numeros e nomes validados:
    // nada vindo do nome do processo entra no comando (evita injecao num terminal de administrador).
    class KillTarget
    {
        public int Pid; public long StartTicks; public string Service; public string Stamp;

        public static KillTarget Of(int pid, DateTime? start)
        {
            var t = new KillTarget { Pid = pid, StartTicks = start.HasValue ? start.Value.Ticks : 0 };
            try { t.Service = Plat.I.KillServiceCommand(pid); } catch { }
            if (!OperatingSystem.IsWindows()) try { t.Stamp = Plat.I.ProcessStartStamp(pid); } catch { }
            return t;
        }
    }

    static class ElevatedCommand
    {
        public static string Safe(string s) { return Regex.Replace(s ?? "", @"[^A-Za-z0-9 ._\-]", ""); }

        public static string Build(List<KillTarget> targets, string name)
        {
            var sb = new StringBuilder();
            if (OperatingSystem.IsWindows() && Regex.IsMatch(name ?? "", @"^vmmem", RegexOptions.IgnoreCase))
            {
                sb.AppendLine("wsl --shutdown");
                return sb.ToString();
            }
            var services = targets.Where(t => t.Service != null).Select(t => t.Service).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var sv in services) sb.AppendLine(sv);
            if (services.Count > 0) return sb.ToString();
            foreach (var t in targets)
            {
                string pid = t.Pid.ToString(CultureInfo.InvariantCulture);
                if (OperatingSystem.IsWindows())
                {
                    if (t.StartTicks > 0)
                        sb.AppendLine("$p = Get-Process -Id " + pid + " -EA 0; if ($p.StartTime.Ticks -eq " + t.StartTicks.ToString(CultureInfo.InvariantCulture) + ") { Stop-Process -Id " + pid + " -Force; 'encerrado' } else { 'PID " + pid + " mudou ou terminou; nada feito' }");
                    else
                        sb.AppendLine("Stop-Process -Id " + pid + " -Force");
                }
                else
                {
                    // confere o horario de inicio (mesma saida do ps) antes de matar: o PID pode ter sido reutilizado
                    if (t.Stamp != null)
                        sb.AppendLine("[ \"$(LC_ALL=C ps -o lstart= -p " + pid + ")\" = \"" + t.Stamp + "\" ] && sudo kill -9 " + pid + " && echo encerrado || echo 'PID " + pid + " mudou ou terminou; nada feito'");
                    else
                        sb.AppendLine("sudo kill -9 " + pid);
                }
            }
            return sb.ToString();
        }
    }

    static class ActionLog
    {
        public static void Log(string kind, string what, Dictionary<string, object> extra)
        {
            var row = new Dictionary<string, object> { { "ts", Fmt.Ts(DateTime.Now) }, { "tipo", kind }, { "o_que", what } };
            if (extra != null) foreach (var kv in extra) row[kv.Key] = kv.Value;
            try { Store.Append("acoes", row); } catch { }
        }
    }
}
