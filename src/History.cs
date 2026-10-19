// Historico em memoria para os graficos do painel (24 h de metricas, 6 h por processo) e limites de cor.
using System;
using System.Collections.Generic;
using System.Linq;

namespace EvidenceMonitor
{
    // Limites que definem amarelo/vermelho
    static class Limits
    {
        public const double ProcMemWarn = 10, ProcMemCrit = 25;   // % da RAM fisica
        public const double ProcCpuWarn = 25, ProcCpuCrit = 50;   // % da CPU total
        public static int Level(double v, double warn, double crit, bool lowerIsWorse)
        {
            if (double.IsNaN(warn) || v < 0) return 0;
            if (lowerIsWorse) return v <= crit ? 2 : v <= warn ? 1 : 0;
            return v >= crit ? 2 : v >= warn ? 1 : 0;
        }
    }

    class Pt { public DateTime T; public double Cpu, Freq, Avail, Commit, Pages, Disk, DiskMbs, NetRx, NetTx; }

    static class History
    {
        public static readonly object Lock = new object();
        public static readonly List<Pt> Pts = new List<Pt>();
        public static readonly Dictionary<string, List<double[]>> Procs = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<DateTime> Marks = new List<DateTime>();
        static DateTime lastProcAdd = DateTime.MinValue;

        static Pt ToPt(Dictionary<string, object> r)
        {
            return new Pt
            {
                T = Fmt.ParseTs(r["ts"]),
                Cpu = Fmt.D(r, "cpu"), Freq = Fmt.D(r, "freq"), Avail = Fmt.D(r, "mem_avail"), Commit = Fmt.D(r, "commit"),
                Pages = Fmt.D(r, "pages"), Disk = Fmt.D(r, "disk_busy"), DiskMbs = Fmt.D(r, "disk_mbs"), NetRx = Fmt.D(r, "net_rx"), NetTx = Fmt.D(r, "net_tx")
            };
        }

        static void AddProc(string name, DateTime t, double gb, double cpu)
        {
            List<double[]> l;
            if (!Procs.TryGetValue(name, out l)) { l = new List<double[]>(); Procs[name] = l; }
            if (l.Count > 0 && l[l.Count - 1][0] == t.Ticks) { l[l.Count - 1][1] += gb; l[l.Count - 1][2] += cpu; }
            else l.Add(new[] { (double)t.Ticks, gb, cpu });
        }

        public static void Add(Dictionary<string, object> row, List<ProcInfo> procs)
        {
            var p = ToPt(row);
            lock (Lock)
            {
                Pts.Add(p);
                var cut = p.T.AddHours(-24);
                int n = 0; while (n < Pts.Count && Pts[n].T < cut) n++;
                if (n > 0) Pts.RemoveRange(0, n);

                if ((p.T - lastProcAdd).TotalSeconds >= Program.IntervalSec - 1)
                {
                    lastProcAdd = p.T;
                    foreach (var g in procs.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        double gb = g.Sum(x => x.Priv) / 1073741824.0, cpu = g.Sum(x => x.Cpu);
                        if (gb >= 0.15 || cpu >= 1 || Procs.ContainsKey(g.Key)) AddProc(g.Key, p.T, gb, cpu);
                    }
                    var pcut = p.T.AddHours(-6).Ticks;
                    foreach (var k in Procs.Keys.ToList())
                    {
                        var l = Procs[k]; int m = 0; while (m < l.Count && l[m][0] < pcut) m++;
                        if (m > 0) l.RemoveRange(0, m);
                        if (l.Count == 0) Procs.Remove(k);
                    }
                }
            }
        }

        public static void AddMark(DateTime t) { lock (Lock) Marks.Add(t); }

        public static bool IsMark(string c) { return c == "mem" || c == "dwm" || c == "crit"; }

        public static void LoadInitial()
        {
            var now = DateTime.Now;
            var rows = Store.Read("metricas", now.AddHours(-24), now);
            List<Dictionary<string, object>> events;
            try { events = Plat.I.ReadEvents(now.AddHours(-24)); } catch { events = new List<Dictionary<string, object>>(); }
            lock (Lock)
            {
                // corte calculado antes: o filtro nao pode olhar Pts[0] enquanto a lista recebe os itens
                DateTime first = Pts.Count > 0 ? Pts[0].T : DateTime.MaxValue;
                Pts.InsertRange(0, rows.Select(ToPt).Where(x => x.T < first).OrderBy(x => x.T).ToList());
                // so o que e anterior ao primeiro ponto ao vivo de cada processo (calculado antes de inserir)
                var firstLive = Procs.ToDictionary(kv => kv.Key, kv => kv.Value.Count > 0 ? kv.Value[0][0] : double.MaxValue, StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    DateTime t = Fmt.ParseTs(r["ts"]);
                    if (t < now.AddHours(-6)) continue;
                    object mt;
                    if (r.TryGetValue("mem_top", out mt) && mt is List<object>)
                        foreach (List<object> e in (List<object>)mt)
                        {
                            string name = Convert.ToString(e[0]);
                            double fl; if (firstLive.TryGetValue(name, out fl) && fl <= t.Ticks) continue;
                            AddProc(name, t, Fmt.D(e[2]), -1);
                        }
                }
                foreach (var l in Procs.Values) l.Sort((a, b) => a[0].CompareTo(b[0]));
                foreach (var e in events) if (IsMark(Fmt.S(e, "c"))) try { Marks.Add(Fmt.ParseTs(e["t"])); } catch { }
            }
        }
    }
}
