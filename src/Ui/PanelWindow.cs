// Painel: graficos ao vivo (com cursor sincronizado), lista de processos com destaque por consumo e acoes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EvidenceMonitor
{
    class ProcRow { public string Key, Name, Agent, Service; public int Pid, Count; public long Priv, Ws; public double Cpu, Hf; }

    class PanelWindow : Window
    {
        readonly TextBlock summary = Pal.Label("");
        readonly Table list = new Table();
        readonly TextBox filter = new TextBox { Width = 170, FontSize = 12.5, Watermark = "nome, serviço, agente", VerticalAlignment = VerticalAlignment.Center };
        readonly CheckBox group = new CheckBox { Content = "Agrupar por aplicativo", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
        readonly TextBlock selInfo = new TextBlock { Foreground = Pal.BSeries, FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        bool userSelected;
        readonly MiniChart cCpu, cFreq, cAvail, cCommit, cPages, cDisk, cNetRx, cNetTx, cProcMem, cProcCpu;
        readonly List<Button> rangeBtns = new List<Button>();
        readonly HoverSync hover = new HoverSync();
        int rangeMin = 60;
        string sortKey = "mem";
        string selName = null; int selPid = -1;
        List<ProcInfo> lastProcs = new List<ProcInfo>();
        double ramBytes = 16.0 * 1073741824;
        readonly TabControl tabs;
        readonly EventsView events;
        readonly SpikesView spikes;

        public Action<int, string> KillRequested;
        public Action<string> KillAllRequested;
        public Action<int> ReportRequested;
        public Action FolderRequested, WslRequested, DockerRequested, RebootRequested;

        public void AddSpike(Dictionary<string, object> row) { spikes.AddLive(row); }
        public void AddEvent(Dictionary<string, object> ev) { events.AddLive(ev); }
        public void SelectTab(int i) { tabs.SelectedIndex = i; }
        public void SetFilter(string f) { filter.Text = f; }
        // usado pelo --snapshot para conferir o cursor sincronizado
        public void SimulateHover(DateTime t) { hover.Set(t, cCpu); }

        public PanelWindow()
        {
            var P = Plat.I;
            Title = Dialog.AppTitle;
            Width = 1240; Height = 880; MinWidth = 860; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Pal.BPage; FontFamily = Pal.Font; FontSize = 12.5;

            // topo: periodo + resumo
            // DockPanel: o ultimo filho (resumo) fica com a largura que sobra e e cortado com "..."
            var top = new DockPanel { Margin = new Thickness(10, 8, 10, 4) };
            top.Children.Add(Docked(Pal.Label("Período:")));
            foreach (var pair in new[] { new object[] { "15 min", 15 }, new object[] { "1 h", 60 }, new object[] { "6 h", 360 }, new object[] { "24 h", 1440 } })
            {
                int mins = (int)pair[1];
                var b = Pal.Btn((string)pair[0], () => { rangeMin = mins; StyleRange(); RefreshCharts(); }); b.Tag = mins;
                rangeBtns.Add(b); top.Children.Add(Docked(b));
            }
            summary.Margin = new Thickness(16, 0, 0, 0); summary.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(summary);
            StyleRange();

            // graficos (5 x 2)
            var charts = new UniformGrid { Columns = 5, Rows = 2, Margin = new Thickness(6, 2, 6, 2) };
            cCpu = Chart("CPU", "%", 100, 70, 90, false, "N0");
            cFreq = Chart("Clock efetivo", "MHz", -1, double.NaN, double.NaN, false, "N0");
            if (P.Os == "macos") cFreq.Unavailable = "O macOS não expõe o clock efetivo sem privilégio de administrador";
            cAvail = Chart("RAM livre", "MB", -1, 1500, 500, true, "N0");
            cCommit = Chart("Memória comprometida", "%", 100, 80, 90, false, "N0");
            cPages = Chart("Paginação", "pág/s", -1, 2000, 10000, false, "N0");
            cDisk = Chart(P.Os == "macos" ? "Disco · leitura+escrita" : "Disco ocupado", P.Os == "macos" ? "MB/s" : "%", P.Os == "macos" ? -1 : 100, P.Os == "macos" ? double.NaN : 70, P.Os == "macos" ? double.NaN : 95, false, P.Os == "macos" ? "N1" : "N0");
            cNetRx = Chart("Rede · download", "Mbit/s", -1, double.NaN, double.NaN, false, "N1");
            cNetTx = Chart("Rede · upload", "Mbit/s", -1, double.NaN, double.NaN, false, "N1");
            cProcMem = Chart("Memória do processo", "GB", -1, double.NaN, double.NaN, false, "N2");
            cProcCpu = Chart("CPU do processo", "%", -1, Limits.ProcCpuWarn, Limits.ProcCpuCrit, false, "N1");
            cProcMem.Accent = true; cProcCpu.Accent = true;
            foreach (var c in new[] { cCpu, cFreq, cAvail, cCommit, cNetRx, cPages, cDisk, cNetTx, cProcMem, cProcCpu }) { c.Margin = new Thickness(4); c.Sync = hover; charts.Children.Add(c); }

            // processos
            var procBar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            procBar.Children.Add(Docked(selInfo));
            procBar.Children.Add(Docked(Pal.Label("Filtrar:")));
            filter.TextChanged += (s, e) => FillList();
            procBar.Children.Add(Docked(filter));
            group.IsCheckedChanged += (s, e) => { selPid = -1; UpdateHeaders(); FillList(); };
            procBar.Children.Add(Docked(group));
            procBar.Children.Add(Docked(Pal.Btn("Encerrar", KillSelected, Pal.BCrit)));
            procBar.Children.Add(Docked(Pal.Btn("Encerrar todas as instâncias", KillAllSelected, Pal.BCrit)));
            procBar.Children.Add(new TextBlock { Text = "Vermelho: ≥ " + Limits.ProcMemCrit + "% da RAM ou ≥ " + Limits.ProcCpuCrit + "% da CPU · Amarelo: ≥ " + Limits.ProcMemWarn + "% / " + Limits.ProcCpuWarn + "%", Foreground = Pal.BMuted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

            list.Columns.Add(new TableColumn("Processo", 220));
            list.Columns.Add(new TableColumn("PID", 70, true));
            list.Columns.Add(new TableColumn("Origem (serviço / processo pai / agente)", 380));
            list.Columns.Add(new TableColumn("Memória privada · % da RAM", 210, true));
            list.Columns.Add(new TableColumn(P.Os == "macos" ? "Residente (GB)" : "Na RAM (GB)", 100, true));
            list.Columns.Add(new TableColumn("CPU", 130, true));
            list.Columns.Add(new TableColumn("Disco (faltas/s)", 110, true));
            list.SameRow = (a, b) => ((ProcRow)a).Key == ((ProcRow)b).Key;
            list.RowBg = r => { int lv = RowLevel((ProcRow)r); return lv == 2 ? Pal.BCritRow : lv == 1 ? Pal.BWarnRow : null; };
            list.CellOf = ProcCell;
            list.HeaderClicked += c => { sortKey = c == 0 ? "name" : c == 5 ? "cpu" : c == 6 ? "hf" : c == 3 ? "mem" : sortKey; UpdateHeaders(); FillList(); };
            list.SelectionChanged += () =>
            {
                var r = list.Selected as ProcRow; if (r == null) return;
                bool changed = !string.Equals(selName, r.Name, StringComparison.OrdinalIgnoreCase) || !userSelected;
                selName = r.Name; selPid = r.Pid; userSelected = true; RefreshProcCharts();
                if (changed) { cProcMem.Flash(); cProcCpu.Flash(); }
            };
            list.DeleteKey += KillSelected;
            var ctx = new ContextMenu();
            ctx.Items.Add(Item("Encerrar processo", KillSelected));
            ctx.Items.Add(Item("Encerrar todas as instâncias", KillAllSelected));
            ctx.Items.Add(new Separator());
            ctx.Items.Add(Item("Copiar detalhes", CopyRow));
            list.ContextMenu = ctx;

            var procPanel = new DockPanel();
            DockPanel.SetDock(procBar, Dock.Top);
            procPanel.Children.Add(procBar); procPanel.Children.Add(new Border { Child = list, BorderBrush = Pal.BGrid, BorderThickness = new Thickness(0, 1, 0, 0) });

            events = new EventsView();
            spikes = new SpikesView();
            tabs = new TabControl { Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(0) };
            tabs.Items.Add(Tab("Processos", procPanel));
            tabs.Items.Add(Tab("Eventos (crash, travamentos, memória)", events));
            tabs.Items.Add(Tab("Picos de CPU", spikes));
            tabs.SelectionChanged += (s, e) => { if (tabs.SelectedIndex == 1) events.EnsureLoaded(); if (tabs.SelectedIndex == 2) spikes.Reload(); };

            // acoes
            var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 8) };
            bar.Children.Add(Docked(Pal.Btn("Relatório 24h", () => ReportRequested?.Invoke(24))));
            bar.Children.Add(Docked(Pal.Btn("Relatório 7 dias", () => ReportRequested?.Invoke(168))));
            bar.Children.Add(Docked(Pal.Btn("Pasta de evidências", () => FolderRequested?.Invoke())));
            if (P.HasWsl) bar.Children.Add(Docked(Pal.Btn("Desligar WSL", () => WslRequested?.Invoke())));
            bar.Children.Add(Docked(Pal.Btn("Reiniciar Docker", () => DockerRequested?.Invoke())));
            bar.Children.Add(Docked(Pal.Btn("Reiniciar " + P.RebootWord, () => RebootRequested?.Invoke(), Pal.BCrit)));
            bar.Children.Add(new TextBlock { Text = "Tracejado vermelho nos gráficos = " + char.ToLower(P.CatLabels["dwm"][0]) + P.CatLabels["dwm"].Substring(1) + ",memória esgotada ou evento crítico. Passe o mouse num gráfico para ver o mesmo instante em todos.", Foreground = Pal.BMuted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap });

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,52*,6,48*,Auto") };
            Grid.SetRow(top, 0); Grid.SetRow(charts, 1);
            var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(splitter, 2); Grid.SetRow(tabs, 3); Grid.SetRow(bar, 4);
            grid.Children.Add(top); grid.Children.Add(charts); grid.Children.Add(splitter); grid.Children.Add(tabs); grid.Children.Add(bar);
            Content = grid;
            UpdateHeaders();
        }

        static Control Docked(Control c) { DockPanel.SetDock(c, Dock.Left); return c; }

        static TabItem Tab(string header, Control content) { return new TabItem { Header = new TextBlock { Text = header, FontSize = 13 }, Content = content }; }
        static MenuItem Item(string h, Action a) { var m = new MenuItem { Header = h }; m.Click += (s, e) => a(); return m; }

        static string ShortAgent(string a) { int i = a.IndexOf(" ("); return i > 0 ? a.Substring(0, i) : a; }

        static MiniChart Chart(string title, string unit, double max, double warn, double crit, bool lower, string fmt)
        {
            return new MiniChart { Title = title, Unit = unit, FixedMax = max, Warn = warn, Crit = crit, LowerIsWorse = lower, Fmt = fmt };
        }

        void StyleRange()
        {
            foreach (var b in rangeBtns)
            {
                bool on = (int)b.Tag == rangeMin;
                b.Background = on ? Pal.BSeries : Pal.BSurface; b.Foreground = on ? Brushes.White : Pal.BInk;
            }
        }

        void UpdateHeaders()
        {
            list.Columns[0].Header = "Processo" + (sortKey == "name" ? "  ▲" : "");
            list.Columns[1].Header = group.IsChecked == true ? "Instâncias" : "PID";
            list.Columns[3].Header = "Memória privada · % da RAM" + (sortKey == "mem" ? "  ▼" : "");
            list.Columns[5].Header = "CPU" + (sortKey == "cpu" ? "  ▼" : "");
            list.Columns[6].Header = "Disco (faltas/s)" + (sortKey == "hf" ? "  ▼" : "");
            list.InvalidateVisual();
        }

        int RowLevel(ProcRow r)
        {
            return Math.Max(Limits.Level(r.Priv / ramBytes * 100, Limits.ProcMemWarn, Limits.ProcMemCrit, false), Limits.Level(r.Cpu, Limits.ProcCpuWarn, Limits.ProcCpuCrit, false));
        }

        Cell ProcCell(object o, int col)
        {
            var r = (ProcRow)o;
            double memPct = r.Priv / ramBytes * 100;
            int memLv = Limits.Level(memPct, Limits.ProcMemWarn, Limits.ProcMemCrit, false), cpuLv = Limits.Level(r.Cpu, Limits.ProcCpuWarn, Limits.ProcCpuCrit, false);
            int rowLv = Math.Max(memLv, cpuLv);
            switch (col)
            {
                case 0: return Cell.Of(r.Name, rowLv > 0 ? Pal.Text(rowLv) : r.Agent != null ? Pal.BAgent : Pal.BInk, rowLv > 0);
                case 1: return Cell.Of(r.Pid < 0 ? r.Count.ToString() : r.Pid.ToString());
                case 2:
                    string origin = r.Agent != null ? ShortAgent(r.Agent) + (string.IsNullOrEmpty(r.Service) ? "" : " · " + r.Service) : r.Service ?? "";
                    return Cell.Of(origin, r.Agent != null ? Pal.BAgent : Pal.BInk2, r.Agent != null);
                case 3: return new Cell { Text = Fmt.N(r.Priv / 1073741824.0, 2) + " GB  ·  " + Fmt.N(memPct, 0) + "%", Fg = Pal.Text(memLv), Bold = memLv > 0, BarPct = memPct, BarColor = Pal.Bar(memLv) };
                case 4: return Cell.Of(Fmt.N(r.Ws / 1073741824.0, 2));
                case 5: return new Cell { Text = Fmt.N(r.Cpu, 1) + "%", Fg = Pal.Text(cpuLv), Bold = cpuLv > 0, BarPct = r.Cpu, BarColor = Pal.Bar(cpuLv) };
                case 6: return Cell.Of(r.Hf >= 0.5 ? Fmt.N(r.Hf, 0) : "");
            }
            return null;
        }

        public void UpdateData(Dictionary<string, object> row, List<ProcInfo> procs)
        {
            if (!IsVisible) return;
            lastProcs = procs;
            double ram = Fmt.D(row, "ram_gb"); if (ram > 0) ramBytes = ram * 1073741824.0;
            var ag = procs.Where(p => p.Agent != null).ToList();
            summary.Text = "Atualizado às " + Fmt.S(row, "ts").Substring(11) + " · " + procs.Count + " processos" +
                (ag.Count > 0 ? " · agentes corporativos: CPU " + Fmt.N(ag.Sum(p => p.Cpu), 1) + "% · " + Fmt.N(ag.Sum(p => p.Priv) / 1048576.0, 0) + " MB (" +
                    string.Join(", ", ag.GroupBy(p => p.Agent).Select(g => ShortAgent(g.Key) + " " + Fmt.N(g.Sum(p => p.Cpu), 1) + "%")) + ")" : "");
            ToolTip.SetTip(summary, summary.Text);
            FillList();
            RefreshCharts();
        }

        void FillList()
        {
            IEnumerable<ProcRow> rows;
            if (group.IsChecked == true)
                rows = lastProcs.GroupBy(p => p.Name + "|" + (p.Agent ?? ""), StringComparer.OrdinalIgnoreCase).Select(g => new ProcRow
                {
                    Key = "g:" + g.Key.ToLowerInvariant(), Name = g.First().Name, Agent = g.First().Agent, Service = string.Join(", ", g.Select(p => p.Service).Where(x => x != null).Distinct().Take(3)),
                    Pid = -1, Count = g.Count(), Priv = g.Sum(p => p.Priv), Ws = g.Sum(p => p.Ws), Cpu = g.Sum(p => p.Cpu), Hf = g.Sum(p => p.HardFaults)
                });
            else
                rows = lastProcs.Select(p => new ProcRow { Key = p.Pid.ToString(), Name = p.Name, Agent = p.Agent, Service = p.Service, Pid = p.Pid, Count = 1, Priv = p.Priv, Ws = p.Ws, Cpu = p.Cpu, Hf = p.HardFaults });
            string f = (filter.Text ?? "").Trim();
            if (f.Length > 0) rows = rows.Where(r => r.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (r.Agent ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (r.Service ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            rows = sortKey == "cpu" ? rows.OrderByDescending(r => r.Cpu) : sortKey == "hf" ? rows.OrderByDescending(r => r.Hf) : sortKey == "name" ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase) : rows.OrderByDescending(r => r.Priv);
            list.SetRows(rows.Take(80));
        }

        void RefreshCharts()
        {
            if (!IsVisible) return;
            var to = DateTime.Now; var from = to.AddMinutes(-rangeMin);
            List<Pt> pts; List<DateTime> marks;
            lock (History.Lock) { pts = History.Pts.Where(p => p.T >= from).ToList(); marks = History.Marks.Where(m => m >= from).ToList(); }
            Func<Func<Pt, double>, List<KeyValuePair<DateTime, double>>> S = sel => pts.Select(p => new KeyValuePair<DateTime, double>(p.T, sel(p))).Where(kv => kv.Value >= 0).ToList();
            bool mac = Plat.I.Os == "macos";
            cCpu.SetData(S(p => p.Cpu), marks, from, to, null);
            cFreq.SetData(S(p => p.Freq), marks, from, to, null);
            cAvail.SetData(S(p => p.Avail), marks, from, to, null);
            cCommit.SetData(S(p => p.Commit), marks, from, to, null);
            cPages.SetData(S(p => p.Pages), marks, from, to, null);
            cDisk.SetData(S(p => mac ? p.DiskMbs : p.Disk), marks, from, to, null);
            cNetRx.SetData(S(p => p.NetRx), marks, from, to, null);
            cNetTx.SetData(S(p => p.NetTx), marks, from, to, null);
            RefreshProcCharts();
        }

        void RefreshProcCharts()
        {
            var to = DateTime.Now; var from = to.AddMinutes(-Math.Min(rangeMin, 360));
            if (selName == null)
            {
                var top = lastProcs.OrderByDescending(p => p.Priv).FirstOrDefault();
                if (top != null) selName = top.Name;
            }
            List<double[]> h = null; List<DateTime> marks;
            lock (History.Lock)
            {
                List<double[]> l;
                if (selName != null && History.Procs.TryGetValue(selName, out l)) h = l.Where(x => x[0] >= from.Ticks).Select(x => (double[])x.Clone()).ToList();
                marks = History.Marks.Where(m => m >= from).ToList();
            }
            string nm = selName ?? "—";
            cProcMem.Title = "Memória · " + nm;
            cProcCpu.Title = "CPU · " + nm;
            string cap = userSelected ? "▶ SELECIONADO NA LISTA (todas as instâncias)" : "▶ MAIOR CONSUMIDOR · selecione na lista";
            cProcMem.Caption = cap; cProcCpu.Caption = cap;
            selInfo.Text = "Gráficos do processo: ▶ " + nm;
            string empty = selName == null ? "Selecione um processo na lista" : "Sem histórico (processo pequeno)";
            var mem = h == null ? new List<KeyValuePair<DateTime, double>>() : h.Select(x => new KeyValuePair<DateTime, double>(new DateTime((long)x[0]), x[1])).ToList();
            var cpu = h == null ? new List<KeyValuePair<DateTime, double>>() : h.Where(x => x[2] >= 0).Select(x => new KeyValuePair<DateTime, double>(new DateTime((long)x[0]), x[2])).ToList();
            double ramGb = ramBytes / 1073741824.0;
            cProcMem.Warn = ramGb * Limits.ProcMemWarn / 100; cProcMem.Crit = ramGb * Limits.ProcMemCrit / 100;
            cProcMem.SetData(mem, marks, from, to, empty);
            cProcCpu.SetData(cpu, marks, from, to, empty);
        }

        ProcRow Selected()
        {
            var r = list.Selected as ProcRow;
            if (r == null) _ = Dialog.Info("Selecione um processo na lista.", this);
            return r;
        }

        void KillSelected()
        {
            var r = Selected(); if (r == null) return;
            if (r.Pid < 0 || r.Count > 1) KillAllRequested?.Invoke(r.Name);
            else KillRequested?.Invoke(r.Pid, r.Name);
        }

        void KillAllSelected()
        {
            var r = Selected(); if (r == null) return;
            KillAllRequested?.Invoke(r.Name);
        }

        async void CopyRow()
        {
            var r = Selected(); if (r == null) return;
            try
            {
                await Clipboard.SetTextAsync(string.Format(Fmt.Br, "{0} (PID {1}) · memória privada {2:N2} GB ({3:N0}% da RAM) · residente {4:N2} GB · CPU {5:N1}% · {6:dd/MM/yyyy HH:mm:ss}",
                    r.Name, r.Pid < 0 ? r.Count + " instâncias" : r.Pid.ToString(), r.Priv / 1073741824.0, r.Priv / ramBytes * 100, r.Ws / 1073741824.0, r.Cpu, DateTime.Now));
            }
            catch { }
        }
    }
}
