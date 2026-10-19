// Abas "Eventos" e "Picos de CPU" do painel.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EvidenceMonitor
{
    // Falta de memoria, quedas da interface grafica, crashes, travamentos, servicos e criticos dos ultimos 7 dias,
    // com o snapshot de processos quando o monitor capturou o incidente.
    class EventsView : DockPanel
    {
        static readonly Dictionary<string, Color> CatColor = new Dictionary<string, Color> {
            { "mem", Color.FromRgb(0x2a, 0x78, 0xd6) }, { "dwm", Color.FromRgb(0xeb, 0x68, 0x34) }, { "crash", Color.FromRgb(0x1b, 0xaf, 0x7a) },
            { "hang", Color.FromRgb(0xed, 0xa1, 0x00) }, { "svc", Color.FromRgb(0xe8, 0x7b, 0xa4) }, { "crit", Color.FromRgb(0x00, 0x83, 0x00) } };
        static Dictionary<string, string> CatLabel { get { return Plat.I.CatLabels; } }

        readonly Table list = new Table();
        readonly ComboBox type = new ComboBox { Width = 260, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBox filter = new TextBox { Width = 180, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock counts = Pal.Label("");
        readonly List<Dictionary<string, object>> all = new List<Dictionary<string, object>>();
        List<Dictionary<string, object>> incidents = new List<Dictionary<string, object>>();
        bool loaded, loading;

        public EventsView()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6) };
            bar.Children.Add(Pal.Label("Tipo:"));
            type.Items.Add("Todos"); foreach (var c in Cat.All) type.Items.Add(CatLabel[c]); type.SelectedIndex = 0;
            type.SelectionChanged += (s, e) => Fill();
            bar.Children.Add(type);
            bar.Children.Add(new Border { Width = 12 });
            bar.Children.Add(Pal.Label("Buscar:"));
            filter.TextChanged += (s, e) => Fill();
            bar.Children.Add(filter);
            bar.Children.Add(Pal.Btn("Atualizar", () => { loaded = false; EnsureLoaded(); }));
            counts.Margin = new Thickness(10, 0, 0, 0);
            bar.Children.Add(counts);
            DockPanel.SetDock(bar, Dock.Top);

            list.Columns.Add(new TableColumn("Data/hora", 130));
            list.Columns.Add(new TableColumn("Tipo", 230));
            list.Columns.Add(new TableColumn("Aplicativo / origem", 220));
            list.Columns.Add(new TableColumn("Snapshot", 80));
            list.Columns.Add(new TableColumn("Detalhe", 600));
            list.CellOf = (o, c) =>
            {
                var e = o as Dictionary<string, object>;
                if (e == null) return c == 1 ? Cell.Of((string)((object[])o)[0], Pal.BMuted) : null;
                string cat = Fmt.S(e, "c"), ts = Fmt.S(e, "t");
                switch (c)
                {
                    case 0: return Cell.Of(ts.Length >= 19 ? ts.Substring(8, 2) + "/" + ts.Substring(5, 2) + " " + ts.Substring(11, 8) : ts, Pal.BInk);
                    case 1: return new Cell { Text = CatLabel.ContainsKey(cat) ? CatLabel[cat] : cat, Fg = Pal.BInk, Dot = CatColor.ContainsKey(cat) ? CatColor[cat] : (Color?)null };
                    case 2: return Cell.Of(AppOf(e), Pal.BInk);
                    case 3: return Cell.Of(SnapshotFor(e) != null ? "sim" : "");
                    case 4: return Cell.Of(Fmt.S(e, "msg"));
                }
                return null;
            };
            list.Activated += ShowDetail;
            var ctx = new ContextMenu();
            var m1 = new MenuItem { Header = "Ver detalhes" }; m1.Click += (s, e) => ShowDetail();
            var m2 = new MenuItem { Header = "Copiar" }; m2.Click += async (s, e) => { var ev = Sel(); if (ev != null) try { await TopLevel.GetTopLevel(this).Clipboard.SetTextAsync(DetailText(ev)); } catch { } };
            ctx.Items.Add(m1); ctx.Items.Add(m2);
            list.ContextMenu = ctx;

            Children.Add(bar);
            Children.Add(new Border { Child = list, BorderBrush = Pal.BGrid, BorderThickness = new Thickness(0, 1, 0, 0) });
        }

        public void EnsureLoaded()
        {
            if (loaded || loading) return;
            loading = true;
            list.SetRows(new object[] { new object[] { "Carregando eventos dos últimos 7 dias..." } });
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var from = DateTime.Now.AddDays(-7);
                List<Dictionary<string, object>> evs = null, inc = null; string hint = null;
                try { evs = Plat.I.ReadEvents(from); inc = Store.Read("incidentes", from, DateTime.Now); hint = Plat.I.EventsHint; } catch { }
                Dispatcher.UIThread.Post(() =>
                {
                    all.Clear(); if (evs != null) all.AddRange(evs);
                    incidents = inc ?? new List<Dictionary<string, object>>();
                    this.hint = hint;
                    loaded = true; loading = false; Fill();
                });
            });
        }
        string hint;

        public void AddLive(Dictionary<string, object> ev)
        {
            if (!loaded) return;
            all.Add(ev);
            if (ev.ContainsKey("procs")) incidents.Add(ev);
            Fill();
        }

        // Aplicativo citado na mensagem (formato varia por sistema)
        static string AppOf(Dictionary<string, object> e)
        {
            string msg = Fmt.S(e, "msg"), c = Fmt.S(e, "c");
            Match m;
            if (c == "dwm" && Plat.I.Os == "windows") return "dwm.exe (interface gráfica)";
            if (c == "mem" && (m = Regex.Match(msg, @"([\w.\-]+\.exe) \(\d+\)")).Success) return m.Groups[1].Value + " (maior consumidor)";
            if ((m = Regex.Match(msg, @"Killed process \d+ \(([^)]+)\)")).Success) return m.Groups[1].Value + " (encerrado por falta de memória)";
            if ((m = Regex.Match(msg, @"maior processo (.+)$")).Success) return m.Groups[1].Value + " (maior consumidor)";
            if ((m = Regex.Match(msg, @"Process \d+ \((.+?)\) of user")).Success) return m.Groups[1].Value;
            if ((m = Regex.Match(msg, @"^(\S+?)\[\d+\]: segfault")).Success) return m.Groups[1].Value;
            if ((m = Regex.Match(msg, @"^(.+?\.service):")).Success) return m.Groups[1].Value;
            if ((m = Regex.Match(msg, @"^(.+?) (falhou|ficou sem resposta)")).Success) return m.Groups[1].Value;
            if ((m = Regex.Match(msg, @"(?:aplicativo com falha|Faulting application name|O programa|The program)[:\s]+([^\s,]+)", RegexOptions.IgnoreCase)).Success) return m.Groups[1].Value;
            if ((m = Regex.Match(msg, @"(?:O servi[cç]o|The) (.+?) (?:foi|terminou|service)", RegexOptions.IgnoreCase)).Success) return m.Groups[1].Value;
            string id = Fmt.S(e, "id");
            return Fmt.S(e, "prov") + (id == "0" || id == "" ? "" : " " + id);
        }

        Dictionary<string, object> SnapshotFor(Dictionary<string, object> e)
        {
            if (e.ContainsKey("procs")) return e;
            DateTime t;
            if (!DateTime.TryParse(Fmt.S(e, "t"), CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            foreach (var i in incidents)
            {
                DateTime ti;
                if (Fmt.S(i, "id") == Fmt.S(e, "id") && Fmt.S(i, "prov") == Fmt.S(e, "prov") && DateTime.TryParse(Fmt.S(i, "t"), CultureInfo.InvariantCulture, DateTimeStyles.None, out ti) && Math.Abs((ti - t).TotalSeconds) <= 15) return i;
            }
            return null;
        }

        void Fill()
        {
            if (!loaded) return;
            string cat = type.SelectedIndex <= 0 ? null : Cat.All[type.SelectedIndex - 1];
            string f = (filter.Text ?? "").Trim();
            var cnt = Cat.All.ToDictionary(c => c, c => 0);
            foreach (var e in all) { string c = Fmt.S(e, "c"); if (cnt.ContainsKey(c)) cnt[c]++; }
            counts.Text = "7 dias: " + string.Join(" · ", Cat.All.Where(c => cnt[c] > 0).Select(c => cnt[c] + " " + CatLabel[c].ToLowerInvariant()));
            if (cnt.Values.Sum() == 0) counts.Text = "Nenhum evento crítico nos últimos 7 dias.";
            if (hint != null) counts.Text += "  ·  ⚠ " + hint;
            counts.Foreground = hint != null ? Pal.BWarnText : Pal.BInk2;

            var rows = all.Where(e => cat == null || Fmt.S(e, "c") == cat)
                          .Where(e => f.Length == 0 || Fmt.S(e, "msg").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || AppOf(e).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                          .OrderByDescending(e => Fmt.S(e, "t")).Take(500).Cast<object>().ToList();
            list.SetRows(rows);
        }

        Dictionary<string, object> Sel() { return list.Selected as Dictionary<string, object>; }

        string DetailText(Dictionary<string, object> e)
        {
            var sb = new StringBuilder();
            string c = Fmt.S(e, "c"), id = Fmt.S(e, "id");
            sb.AppendLine(Fmt.S(e, "t").Replace("T", " ") + "  ·  " + (CatLabel.ContainsKey(c) ? CatLabel[c] : c) + "  ·  " + Fmt.S(e, "prov") + (id == "0" ? "" : " " + id));
            if (Fmt.S(e, "file").Length > 0) sb.AppendLine("Arquivo: " + Fmt.S(e, "file"));
            sb.AppendLine();
            sb.AppendLine(Fmt.S(e, "msg"));
            var snap = SnapshotFor(e);
            if (snap != null)
            {
                sb.AppendLine();
                object mo; var met = snap.TryGetValue("metricas", out mo) ? mo as Dictionary<string, object> : null;
                sb.AppendLine("Snapshot naquele instante" + (met != null ? ": commit " + Fmt.S(met, "commit") + "% · RAM livre " + Fmt.S(met, "mem_avail") + " MB · CPU " + Fmt.S(met, "cpu") + "%" : ""));
                sb.AppendLine(string.Format("{0,-32} {1,8} {2,12} {3,10} {4,7}", "Processo", "PID", "Privada GB", "RAM GB", "CPU %"));
                object po;
                if (snap.TryGetValue("procs", out po) && po is System.Collections.IEnumerable)
                    foreach (var p in (System.Collections.IEnumerable)po)
                    {
                        var a = ((System.Collections.IEnumerable)p).Cast<object>().ToArray();
                        if (a.Length >= 5) sb.AppendLine(string.Format(Fmt.Br, "{0,-32} {1,8} {2,12:N2} {3,10:N2} {4,7:N0}", a[0], a[1], Fmt.D(a[2]), Fmt.D(a[3]), Fmt.D(a[4])));
                    }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("Sem snapshot de processos (evento anterior à instalação do monitor ou de quando ele estava fechado).");
            }
            return sb.ToString();
        }

        void ShowDetail()
        {
            var e = Sel(); if (e == null) return;
            string c = Fmt.S(e, "c");
            Dialog.Text("Evento: " + (CatLabel.ContainsKey(c) ? CatLabel[c] : c), DetailText(e), TopLevel.GetTopLevel(this) as Window, 820, 520);
        }
    }

    // Episodios em que a CPU da maquina passou do limite e os processos que puxaram.
    class SpikesView : DockPanel
    {
        class Culprit { public string Name, Origin; public double Sum, Max; public int Seen; public HashSet<string> Pids = new HashSet<string>(); }
        class Episode { public DateTime Start, End; public double Peak, Sum, DurS; public int N; public bool Precise; public List<Culprit> Culprits; public List<KeyValuePair<DateTime, double>> Samples = new List<KeyValuePair<DateTime, double>>(); }

        readonly Table list = new Table();
        readonly ComboBox thr = new ComboBox { Width = 170, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        readonly ComboBox period = new ComboBox { Width = 120, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock info = Pal.Label("");
        List<Episode> episodes = new List<Episode>();
        bool loading, loadedOnce;

        public SpikesView()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6) };
            bar.Children.Add(Pal.Label("Limite:"));
            thr.Items.Add("≥ 90% (crítico)"); thr.Items.Add("≥ 70% (atenção)"); thr.SelectedIndex = 0;
            thr.SelectionChanged += (s, e) => Reload();
            bar.Children.Add(thr);
            bar.Children.Add(new Border { Width = 12 });
            bar.Children.Add(Pal.Label("Período:"));
            period.Items.Add("24 horas"); period.Items.Add("7 dias"); period.SelectedIndex = 0;
            period.SelectionChanged += (s, e) => Reload();
            bar.Children.Add(period);
            bar.Children.Add(Pal.Btn("Atualizar", Reload));
            info.Margin = new Thickness(10, 0, 0, 0);
            bar.Children.Add(info);
            DockPanel.SetDock(bar, Dock.Top);

            list.Columns.Add(new TableColumn("Início", 130));
            list.Columns.Add(new TableColumn("Duração", 90, true));
            list.Columns.Add(new TableColumn("Pico", 70, true));
            list.Columns.Add(new TableColumn("Média", 70, true));
            list.Columns.Add(new TableColumn("Responsáveis (média da CPU da máquina durante o pico · máximo)", 800));
            list.CellOf = (o, c) =>
            {
                var ep = o as Episode;
                if (ep == null) return c == 0 ? Cell.Of((string)((object[])o)[0], Pal.BMuted) : null;
                var fg = ep.Peak >= 90 ? Pal.BCrit : Pal.BInk;
                switch (c)
                {
                    case 0: return Cell.Of(ep.Start.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture), fg);
                    case 1: return Cell.Of((ep.Precise ? "" : "~") + Fmt.Dur(ep.DurS), fg);
                    case 2: return Cell.Of(Fmt.N(ep.Peak, 0) + "%", fg, ep.Peak >= 90);
                    case 3: return Cell.Of(Fmt.N(ep.Sum / ep.N, 0) + "%", fg);
                    case 4:
                        string who = string.Join("  ·  ", MainCulprits(ep).Select(x => Who(x) + " " + Fmt.N(x.Sum / ep.N, 0) + "% (máx " + Fmt.N(x.Max, 0) + "%)"));
                        return Cell.Of(who.Length == 0 ? "sem detalhe de processos nesta amostra" : who, fg);
                }
                return null;
            };
            list.Activated += ShowDetail;
            var ctx = new ContextMenu();
            var m1 = new MenuItem { Header = "Ver detalhes" }; m1.Click += (s, e) => ShowDetail();
            var m2 = new MenuItem { Header = "Copiar" }; m2.Click += async (s, e) => { var ep = list.Selected as Episode; if (ep != null) try { await TopLevel.GetTopLevel(this).Clipboard.SetTextAsync(Detail(ep)); } catch { } };
            ctx.Items.Add(m1); ctx.Items.Add(m2);
            list.ContextMenu = ctx;
            Children.Add(bar);
            Children.Add(new Border { Child = list, BorderBrush = Pal.BGrid, BorderThickness = new Thickness(0, 1, 0, 0) });
        }

        double Limit { get { return thr.SelectedIndex == 1 ? 70 : 90; } }
        int Hours { get { return period.SelectedIndex == 1 ? 168 : 24; } }

        public void Reload()
        {
            if (loading) return;
            loading = true;
            double limit = Limit; int hours = Hours;
            list.SetRows(new object[] { new object[] { "Analisando métricas..." } });
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var eps = new List<Episode>();
                try
                {
                    var from = DateTime.Now.AddHours(-hours);
                    var precise = Store.Read("picos", from, DateTime.Now).Select(FromProbe).Where(x => x.Peak >= limit).ToList();
                    // metricas de 10 s so para o que nao foi medido pelo vigia de 1 s (dados antigos)
                    var legacy = Find(Store.Read("metricas", from, DateTime.Now), limit)
                        .Where(x => !precise.Any(p => p.Start <= x.End.AddSeconds(Program.IntervalSec) && x.Start <= p.End.AddSeconds(Program.IntervalSec))).ToList();
                    eps = precise.Concat(legacy).ToList();
                }
                catch { }
                Dispatcher.UIThread.Post(() => { episodes = eps; loading = false; loadedOnce = true; Fill(limit, hours); });
            });
        }

        public void AddLive(Dictionary<string, object> row)
        {
            if (loading || !loadedOnce) return;
            var ep = FromProbe(row);
            if (ep.Peak < Limit) return;
            episodes.Add(ep); Fill(Limit, Hours);
        }

        // registro do vigia de 1 s -> Episode
        static Episode FromProbe(Dictionary<string, object> r)
        {
            var ep = new Episode { Precise = true, Culprits = new List<Culprit>() };
            ep.Start = Fmt.ParseTs(r["ts"]);
            ep.End = Fmt.ParseTs(r["fim"]);
            ep.DurS = Math.Max(1, Fmt.D(r, "dur_s")); ep.Peak = Fmt.D(r, "pico");
            object am; var samples = r.TryGetValue("amostras", out am) ? am as System.Collections.IList : null;
            ep.N = samples != null && samples.Count > 0 ? samples.Count : 1;
            ep.Sum = Fmt.D(r, "media") * ep.N;
            if (samples != null)
                foreach (System.Collections.IList s in samples)
                {
                    DateTime t; DateTime.TryParse(ep.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + Convert.ToString(s[0]), CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
                    ep.Samples.Add(new KeyValuePair<DateTime, double>(t, Fmt.D(s[1])));
                }
            object ro;
            if (r.TryGetValue("responsaveis", out ro) && ro is System.Collections.IList)
                foreach (System.Collections.IList c in (System.Collections.IList)ro)
                {
                    var cu = new Culprit { Name = Convert.ToString(c[0]), Origin = Convert.ToString(c[1]), Sum = Fmt.D(c[2]) * ep.N, Max = Fmt.D(c[3]), Seen = (int)Math.Round(Fmt.D(c[4]) * ep.N / 100) };
                    foreach (var p in Convert.ToString(c[5]).Split(',')) if (p.Length > 0) cu.Pids.Add(p);
                    ep.Culprits.Add(cu);
                }
            return ep;
        }

        static List<Episode> Find(List<Dictionary<string, object>> rows, double limit)
        {
            var eps = new List<Episode>(); Episode cur = null; Dictionary<string, Culprit> cul = null;
            double gapSec = Program.IntervalSec * 3.5;
            Action close = () =>
            {
                if (cur == null) return;
                cur.Culprits = cul.Values.OrderByDescending(c => c.Sum).ToList();
                eps.Add(cur); cur = null;
            };
            foreach (var r in rows)
            {
                double cpu = Fmt.D(r, "cpu"); if (cpu < 0) continue;
                DateTime ts = Fmt.ParseTs(r["ts"]);
                if (cpu < limit) { if (cur != null && (ts - cur.End).TotalSeconds > gapSec) close(); continue; }
                if (cur != null && (ts - cur.End).TotalSeconds > gapSec) close();
                if (cur == null) { cur = new Episode { Start = ts }; cul = new Dictionary<string, Culprit>(StringComparer.OrdinalIgnoreCase); }
                cur.End = ts; cur.DurS = (cur.End - cur.Start).TotalSeconds + Program.IntervalSec; cur.N++; cur.Sum += cpu; cur.Peak = Math.Max(cur.Peak, cpu); cur.Samples.Add(new KeyValuePair<DateTime, double>(ts, cpu));
                object ct;
                if (r.TryGetValue("cpu_top", out ct) && ct is System.Collections.IList)
                    foreach (System.Collections.IList e in (System.Collections.IList)ct)
                    {
                        string name = Convert.ToString(e[0]); string origin = e.Count > 3 ? Convert.ToString(e[3]) : "";
                        double pct = Fmt.D(e[2]); if (pct <= 0) continue;
                        Culprit c; string key = name + "|" + origin;
                        if (!cul.TryGetValue(key, out c)) { c = new Culprit { Name = name, Origin = origin }; cul[key] = c; }
                        c.Sum += pct; c.Max = Math.Max(c.Max, pct); c.Seen++; c.Pids.Add(Convert.ToString(e[1], CultureInfo.InvariantCulture));
                    }
            }
            close();
            return eps;
        }

        static string Who(Culprit c) { return c.Name + (string.IsNullOrEmpty(c.Origin) ? "" : " [" + c.Origin + "]"); }

        static List<Culprit> MainCulprits(Episode ep)
        {
            // responsaveis: quem teve media >= 5% da maquina no episodio; no minimo o maior
            var l = ep.Culprits.Where(c => c.Sum / ep.N >= 5).ToList();
            if (l.Count == 0 && ep.Culprits.Count > 0) l.Add(ep.Culprits[0]);
            return l.Take(5).ToList();
        }

        void Fill(double limit, int hours)
        {
            var rows = episodes.OrderByDescending(x => x.Start).Cast<object>().ToList();
            if (rows.Count == 0) rows.Add(new object[] { "Nenhum pico de CPU " + (limit >= 90 ? "≥ 90%" : "≥ 70%") + " no período." });
            list.SetRows(rows);
            double total = episodes.Sum(x => x.DurS);
            var top = episodes.SelectMany(x => MainCulprits(x).Select(c => new { W = Who(c), T = x.DurS }))
                              .GroupBy(x => x.W).OrderByDescending(g => g.Sum(x => x.T)).FirstOrDefault();
            info.Text = episodes.Count + " pico(s) em " + (hours >= 48 ? "7 dias" : "24 h") + " · " + Fmt.Dur(total) + " acima do limite" +
                        (top != null ? " · quem mais apareceu: " + top.Key + " (" + Fmt.Dur(top.Sum(x => x.T)) + ")" : "");
        }

        static string Detail(Episode ep)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Pico de CPU de " + ep.Start.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " a " + ep.End.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · " + Fmt.Dur(ep.DurS) + (ep.Precise ? " · medido a cada 1 s" : " · estimado das métricas de " + Program.IntervalSec + " s"));
            sb.AppendLine("CPU da máquina: pico " + Fmt.N(ep.Peak, 0) + "% · média " + Fmt.N(ep.Sum / ep.N, 0) + "% · " + ep.N + " amostra(s)");
            sb.AppendLine();
            sb.AppendLine(string.Format("{0,-34} {1,-30} {2,9} {3,9} {4,10}  {5}", "Processo", "Origem", "Média %", "Máx %", "Presente", "PIDs"));
            foreach (var c in ep.Culprits)
                sb.AppendLine(string.Format(Fmt.Br, "{0,-34} {1,-30} {2,9:N1} {3,9:N1} {4,9:N0}%  {5}",
                    c.Name.Length > 34 ? c.Name.Substring(0, 33) + "…" : c.Name, (c.Origin ?? "").Length > 30 ? c.Origin.Substring(0, 29) + "…" : c.Origin,
                    c.Sum / ep.N, c.Max, 100.0 * c.Seen / ep.N, string.Join(",", c.Pids)));
            sb.AppendLine();
            sb.AppendLine("% = parcela da CPU total da máquina. \"Presente\" = em quantas amostras do pico o processo estava entre os 5 que mais usavam CPU.");
            sb.AppendLine();
            sb.AppendLine("Amostras:");
            foreach (var s in ep.Samples) sb.AppendLine("  " + s.Key.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Fmt.N(s.Value, 0) + "%");
            return sb.ToString();
        }

        void ShowDetail()
        {
            var ep = list.Selected as Episode; if (ep == null) return;
            Dialog.Text("Pico de CPU · " + ep.Start.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture), Detail(ep), TopLevel.GetTopLevel(this) as Window, 900, 540);
        }
    }
}
