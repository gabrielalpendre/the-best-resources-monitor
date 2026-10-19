// Icone na bandeja/barra de menus, amostragem periodica, alertas e acoes (encerrar, reiniciar, relatorios).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace EvidenceMonitor
{
    class TrayApp
    {
        readonly TrayIcon tray = new TrayIcon();
        readonly NativeMenu menu = new NativeMenu();
        readonly NativeMenuItem startup, cancelReboot;
        readonly Sampler sampler = new Sampler();
        readonly SpikeProbe probe = new SpikeProbe();
        readonly IPlatform P = Plat.I;
        PanelWindow panel;
        readonly Timer timer;
        IDisposable watcher;
        Dictionary<string, object> lastRow;
        List<ProcInfo> lastProcs = new List<ProcInfo>();
        string level = "";
        DateTime lastCommitAlert = DateTime.MinValue, lastStored = DateTime.MinValue;
        readonly Dictionary<string, DateTime> lastLeakAlert = new Dictionary<string, DateTime>();
        int busy = 0;
        readonly Action quit;

        static void Log(string kind, string what, Dictionary<string, object> extra) { ActionLog.Log(kind, what, extra); }

        static NativeMenuItem Item(string text, Action a) { var m = new NativeMenuItem(text); m.Click += (s, e) => a(); return m; }

        public TrayApp(bool openPanel, Action quit)
        {
            this.quit = quit;
            menu.Items.Add(Item("Abrir painel", ShowPanel));
            menu.Items.Add(Item("Gerar relatório (24h)", () => MakeReport(24)));
            menu.Items.Add(Item("Gerar relatório (7 dias)", () => MakeReport(168)));
            menu.Items.Add(Item("Abrir pasta de evidências", OpenFolder));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Encerrar processo que mais usa memória", KillTop));
            if (P.HasWsl) menu.Items.Add(Item("Desligar WSL (wsl --shutdown)", WslShutdown));
            menu.Items.Add(Item("Reiniciar Docker Desktop", RestartDocker));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Reiniciar " + P.RebootWord + " (forçado)...", () => RebootNow("menu")));
            cancelReboot = Item("Cancelar reinício", CancelReboot);
            menu.Items.Add(new NativeMenuItemSeparator());
            startup = new NativeMenuItem("Iniciar com o login") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = P.StartupEnabled() };
            startup.Click += (s, e) => ToggleStartup();
            menu.Items.Add(startup);
            menu.Items.Add(Item("Sair", Quit));

            tray.Menu = menu;
            tray.ToolTipText = Dialog.AppTitle;
            SetIcon("ok");
            tray.Clicked += (s, e) => ShowPanel();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });
            tray.IsVisible = true;

            Store.Cleanup(30);
            watcher = P.WatchEvents(OnEvent);
            probe.OnSpike = row => Dispatcher.UIThread.Post(() => panel?.AddSpike(row));
            probe.Start();
            timer = new Timer(_ => Tick(), null, 2000, Program.IntervalSec * 1000);
            ThreadPool.QueueUserWorkItem(_ => { try { History.LoadInitial(); } catch { } });
            Log("app", "iniciado", new Dictionary<string, object> { { "sistema", P.OsName } });
            if (openPanel) Dispatcher.UIThread.Post(ShowPanel);
        }

        void Tick()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                List<ProcInfo> procs;
                var row = sampler.Sample(out procs);
                if ((DateTime.Now - lastStored).TotalSeconds >= Program.IntervalSec - 1)
                {
                    Store.Append("metricas", row);
                    lastStored = DateTime.Now;
                }
                History.Add(row, procs);
                lastRow = row; lastProcs = procs;
                Dispatcher.UIThread.Post(() => OnSample(row, procs));
            }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        void OnSample(Dictionary<string, object> row, List<ProcInfo> procs)
        {
            double commit = Fmt.D(row, "commit"), avail = Fmt.D(row, "mem_avail");
            double ramGb = Fmt.D(row, "ram_gb"); if (ramGb <= 0) ramGb = 16;
            string lv = commit >= 90 || (avail >= 0 && avail < 500) ? "crit" : commit >= 80 || (avail >= 0 && avail < 1500) ? "warn" : "ok";
            SetIcon(lv);
            tray.ToolTipText = Program.Summary(row);
            panel?.UpdateData(row, procs);

            var top = procs.OrderByDescending(p => p.Priv).FirstOrDefault();
            double leakGb = Math.Max(8, ramGb * 0.25);
            if (top != null && top.Priv / 1073741824.0 >= leakGb)
            {
                DateTime last;
                if (!lastLeakAlert.TryGetValue(top.Name, out last) || DateTime.Now - last > TimeSpan.FromMinutes(30))
                {
                    lastLeakAlert[top.Name] = DateTime.Now;
                    int pid = top.Pid; string name = top.Name;
                    Toast.Show(top.Name + " está usando " + Fmt.N(top.Priv / 1073741824.0, 1) + " GB",
                        "Memória privada acima de " + Fmt.N(leakGb, 0) + " GB. Clique aqui para encerrar o processo.", false, () => ConfirmKill(pid, name));
                    Log("alerta", "processo acima do limite", new Dictionary<string, object> { { "processo", top.Name }, { "pid", top.Pid }, { "gb", Math.Round(top.Priv / 1073741824.0, 2) } });
                    return;
                }
            }
            if (lv == "crit" && DateTime.Now - lastCommitAlert > TimeSpan.FromMinutes(10))
            {
                lastCommitAlert = DateTime.Now;
                int pid = top != null ? top.Pid : -1; string name = top?.Name;
                Toast.Show("Memória quase esgotada (" + Fmt.N(commit, 0) + "% comprometida)",
                    top != null ? "Maior consumidor: " + top.Name + " (" + Fmt.N(top.Priv / 1073741824.0, 1) + " GB). Clique para encerrar." : "", true,
                    () => { if (pid > 0) ConfirmKill(pid, name); else ShowPanel(); });
                Log("alerta", "memoria comprometida alta", new Dictionary<string, object> { { "commit", commit }, { "ram_livre_mb", avail } });
            }
        }

        void OnEvent(Dictionary<string, object> ev)
        {
            List<ProcInfo> procs = lastProcs;
            ev["ts"] = ev["t"];
            ev["procs"] = procs.OrderByDescending(p => p.Priv).Take(15).Select(p => new object[] { p.Name, p.Pid, Math.Round(p.Priv / 1073741824.0, 2), Math.Round(p.Ws / 1073741824.0, 2), Math.Round(p.Cpu, 0) }).ToArray();
            ev["metricas"] = lastRow;
            try { Store.Append("incidentes", ev); } catch { }
            string c = Fmt.S(ev, "c");
            if (History.IsMark(c)) History.AddMark(DateTime.Now);
            Dispatcher.UIThread.Post(() => panel?.AddEvent(ev));
            string title = c == "mem" || c == "dwm" || c == "crit" ? P.CatLabels[c] : null;
            if (title != null) Toast.Show(title, "Evidência salva com snapshot dos processos. " + Fmt.S(ev, "msg"), true, ShowPanel);
        }

        // icone: circulo verde/amarelo/vermelho com um "pulso" branco
        void SetIcon(string lv)
        {
            if (lv == level) return;
            level = lv;
            var col = lv == "crit" ? Pal.Crit : lv == "warn" ? Pal.Warn : Pal.Good;
            var bmp = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var g = bmp.CreateDrawingContext())
            {
                var white = new Pen(Brushes.White, 6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                g.DrawEllipse(new SolidColorBrush(col), white, new Point(32, 32), 26, 26);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(16, 36), false);
                    foreach (var p in new[] { new Point(26, 36), new Point(32, 20), new Point(38, 44), new Point(44, 32), new Point(50, 32) }) ctx.LineTo(p);
                    ctx.EndFigure(false);
                }
                g.DrawGeometry(null, white, geo);
            }
            var ms = new MemoryStream(); bmp.Save(ms); ms.Position = 0;
            tray.Icon = new WindowIcon(ms);
        }

        // O painel so existe enquanto esta aberto: fechar destroi a janela e volta a amostragem para 10 s.
        void ShowPanel()
        {
            if (panel == null)
            {
                panel = new PanelWindow
                {
                    KillRequested = (pid, name) => ConfirmKill(pid, name),
                    ReportRequested = h => MakeReport(h),
                    FolderRequested = OpenFolder,
                    WslRequested = WslShutdown,
                    DockerRequested = RestartDocker,
                    RebootRequested = () => RebootNow("painel"),
                    KillAllRequested = name => ConfirmKillAll(name)
                };
                panel.Closed += (s, e) =>
                {
                    panel = null;
                    timer.Change(Program.IntervalSec * 1000, Program.IntervalSec * 1000);
                    GC.Collect();
                };
                timer.Change(0, Program.PanelMs);
            }
            panel.Show(); panel.WindowState = WindowState.Normal; panel.Activate();
            if (lastRow != null) panel.UpdateData(lastRow, lastProcs);
        }

        void KillTop()
        {
            var top = lastProcs.OrderByDescending(p => p.Priv).FirstOrDefault();
            if (top != null) ConfirmKill(top.Pid, top.Name);
        }

        static DateTime? StartOf(Process p) { try { return p.StartTime; } catch { return null; } }
        // Nome do processo pelo .NET pode vir truncado no Linux/macOS (15/32 caracteres)
        static bool SameName(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || Math.Min(a.Length, b.Length) < 15) return false;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }
        static bool Denied(System.ComponentModel.Win32Exception ex) { return OperatingSystem.IsWindows() ? ex.NativeErrorCode == 5 : ex.NativeErrorCode == 1; }

        async void ConfirmKill(int pid, string name)
        {
            if (pid == Environment.ProcessId) return;
            var info = lastProcs.FirstOrDefault(x => x.Pid == pid) ?? new ProcInfo { Name = name, Pid = pid };
            if (await Blocked(info)) return;
            Process p;
            try { p = Process.GetProcessById(pid); } catch { await Dialog.Info("O processo " + name + " (PID " + pid + ") não existe mais.", panel); return; }
            if (!SameName(p.ProcessName, name)) { await Dialog.Info("O PID " + pid + " agora pertence a outro processo. Nada foi encerrado.", panel); return; }
            DateTime? started = StartOf(p);
            double gb = info.Priv / 1073741824.0;
            if (!await Dialog.Confirm("Encerrar " + name + " (PID " + pid + ", " + Fmt.N(gb, 1) + " GB de memória privada)?\n\nTrabalho não salvo nesse programa será perdido.", panel)) return;
            string result; bool denied = false;
            try
            {
                // o PID pode ter sido reutilizado enquanto o dialogo estava aberto: confere nome e horario de inicio de novo
                using (var q = Process.GetProcessById(pid))
                {
                    if (!SameName(q.ProcessName, name) || StartOf(q) != started)
                    { await Dialog.Info("O processo mudou enquanto a confirmação estava aberta. Nada foi encerrado.", panel); return; }
                    q.Kill(); result = "encerrado";
                }
            }
            catch (System.ComponentModel.Win32Exception ex) { result = "falhou: " + ex.Message; denied = Denied(ex); }
            catch (Exception ex) { result = "falhou: " + ex.Message; }
            Log("acao", "encerrar processo", new Dictionary<string, object> { { "processo", name }, { "pid", pid }, { "gb", Math.Round(gb, 2) }, { "resultado", result } });
            if (result != "encerrado")
            {
                if (denied) ShowElevated(new List<KillTarget> { KillTarget.Of(pid, started) }, name);
                else await Dialog.Info("Não foi possível encerrar: " + result, panel);
                return;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(10000);
                // ainda existe o mesmo processo segurando memoria? (preso no kernel)
                var still = new ProcSnap(false).Take(DateTime.Now).FirstOrDefault(x => x.Pid == pid && SameName(x.Name, name));
                double left = still == null ? 0 : still.Priv / 1073741824.0;
                if (left > 0.5)
                {
                    Log("acao", "processo preso apos encerrar", new Dictionary<string, object> { { "processo", name }, { "pid", pid }, { "gb", Math.Round(left, 2) } });
                    Toast.Show(name + " ficou preso no encerramento", "Ainda segura " + Fmt.N(left, 1) + " GB. Só reiniciar " + P.RebootWord + " libera. Clique aqui para reiniciar.", true, () => RebootNow("processo preso"));
                }
                else if (still == null) Toast.Show(name + " encerrado", "Memória liberada.");
            });
        }

        async void ConfirmKillAll(string name)
        {
            if (Agents.IsCriticalName(name)) { await Blocked(new ProcInfo { Name = name }); return; }
            var agent = lastProcs.FirstOrDefault(x => SameName(x.Name, name) && x.Agent != null);
            if (agent != null) { await Blocked(agent); return; }
            var mine = lastProcs.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) { await Dialog.Info("Nenhum processo " + name + " rodando.", panel); return; }
            double gb = mine.Sum(p => p.Priv) / 1073741824.0;
            if (!await Dialog.Confirm("Encerrar todas as " + mine.Count + " instâncias de " + name + " (" + Fmt.N(gb, 1) + " GB de memória privada no total)?\n\nTrabalho não salvo nesse programa será perdido.", panel)) return;
            int ok = 0, fail = 0; string lastErr = "";
            var deniedList = new List<KillTarget>();
            foreach (var pi in mine)
            {
                if (pi.Pid == Environment.ProcessId) continue;
                try
                {
                    using (var p = Process.GetProcessById(pi.Pid))
                    {
                        if (!SameName(p.ProcessName, name)) continue;
                        try { p.Kill(); ok++; }
                        catch (System.ComponentModel.Win32Exception ex) { fail++; lastErr = ex.Message; if (Denied(ex)) deniedList.Add(KillTarget.Of(pi.Pid, StartOf(p))); }
                    }
                }
                catch (Exception ex) { fail++; lastErr = ex.Message; }
            }
            Log("acao", "encerrar todas as instancias", new Dictionary<string, object> { { "processo", name }, { "instancias", mine.Count }, { "gb", Math.Round(gb, 2) }, { "encerrados", ok }, { "falhas", fail } });
            Toast.Show(name + ": " + ok + " encerrado(s)", fail == 0 ? "Memória liberada." : fail + " falharam: " + lastErr, fail > 0);
            if (deniedList.Count > 0) ShowElevated(deniedList, name);
        }

        // Ultimo recurso: reinicio forcado. Nao precisa de admin. Antes, salva o relatorio de 24 h em Downloads
        // para a evidencia nao se perder; da 30 s para cancelar.
        async void RebootNow(string reason)
        {
            if (!await Dialog.Confirm("Reiniciar " + P.RebootWord + " agora?\n\n" +
                "• Programas abertos serão fechados SEM salvar (reinício forçado).\n" +
                "• Antes, o relatório das últimas 24 h é salvo em Downloads.\n" +
                "• O reinício começa em 30 s e pode ser cancelado pelo menu do ícone.", panel)) return;
            Toast.Show("Salvando evidências...", "Gerando o relatório de 24 h antes de reiniciar.");
            string rep = "", err = null; bool ok = false;
            await Task.Run(() =>
            {
                try { rep = Report.Build(24); } catch (Exception ex) { rep = "falhou: " + ex.Message; }
                ok = P.RebootScheduled(out err);
                Log("acao", "reiniciar", new Dictionary<string, object> { { "motivo", reason }, { "relatorio", rep }, { "resultado", ok ? "agendado" : err } });
            });
            if (ok)
            {
                if (!menu.Items.Contains(cancelReboot)) menu.Items.Insert(menu.Items.IndexOf(menu.Items.OfType<NativeMenuItem>().First(m => m.Header.StartsWith("Reiniciar " + P.RebootWord))) + 1, cancelReboot);
                Toast.Show(char.ToUpper(P.RebootWord[0]) + P.RebootWord.Substring(1) + " vai reiniciar em 30 s", "Relatório salvo em Downloads. Para cancelar: menu do ícone > Cancelar reinício.", true, CancelReboot);
            }
            else await Dialog.Info("Não foi possível agendar o reinício (" + err + ").\nReinicie pelo menu do sistema.", panel);
        }

        void CancelReboot()
        {
            bool ok = P.CancelReboot();
            Log("acao", "cancelar reinicio", new Dictionary<string, object> { { "resultado", ok ? "ok" : "nada para cancelar" } });
            menu.Items.Remove(cancelReboot);
            Toast.Show(ok ? "Reinício cancelado" : "Nada para cancelar", "");
        }

        async void WslShutdown()
        {
            if (!await Dialog.Confirm("Desligar todas as distros do WSL (inclui o motor do Docker)?", panel, false)) return;
            string res = await Task.Run(() => P.WslShutdown());
            Log("acao", "wsl --shutdown", new Dictionary<string, object> { { "resultado", res } });
            Toast.Show("WSL desligado", res);
        }

        async void RestartDocker()
        {
            if (!await Dialog.Confirm("Reiniciar o Docker Desktop? Containers em execução serão parados.", panel, false)) return;
            string elevated = null;
            string res = await Task.Run(() => P.RestartDocker(out elevated));
            Log("acao", "reiniciar docker", new Dictionary<string, object> { { "resultado", res } });
            Toast.Show("Reiniciando Docker", res);
            if (elevated != null) new ElevatedCmdWindow("Docker", elevated, () => RebootNow("sem permissao: docker")).Show();
        }

        // Agentes corporativos e processos criticos do sistema nunca sao encerrados pelo monitor.
        async Task<bool> Blocked(ProcInfo p)
        {
            if (p.Agent != null)
            {
                await Dialog.Info(p.Name + " (PID " + p.Pid + ") é parte do " + p.Agent + ", um agente corporativo de segurança/monitoramento.\n\n" +
                    "O monitor não encerra esses processos: isso pode violar a política de TI. Se ele estiver pesando, leve as evidências (relatório) para a TI.", panel);
                return true;
            }
            if (Agents.IsCriticalName(p.Name))
            {
                await Dialog.Info(p.Name + " é um processo crítico do " + P.OsName + ". Encerrá-lo derruba a sessão ou o sistema.\n\nSe ele estiver preso, use \"Reiniciar " + P.RebootWord + "\".", panel);
                return true;
            }
            return false;
        }

        void ShowElevated(List<KillTarget> targets, string name)
        {
            string cmd = ElevatedCommand.Build(targets, name);
            Log("acao", "sem permissao: comando para admin gerado", new Dictionary<string, object> { { "processo", name }, { "pids", string.Join(",", targets.Select(t => t.Pid)) } });
            new ElevatedCmdWindow(name, cmd, () => RebootNow("sem permissao: " + name)).Show();
        }

        void MakeReport(int hours)
        {
            Toast.Show("Gerando relatório...", "Lendo métricas e eventos das últimas " + (hours >= 48 ? (hours / 24) + " dias" : hours + " horas") + ".");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { string f = Report.Build(hours); Dispatcher.UIThread.Post(() => ReportDone.Show(f)); }
                catch (Exception ex) { Dispatcher.UIThread.Post(() => Dialog.Info("Falha ao gerar relatório: " + ex.Message, panel)); }
            });
        }

        void OpenFolder() { Directory.CreateDirectory(Paths.Root); P.OpenFolder(Paths.Root); }

        void ToggleStartup()
        {
            try { P.SetStartup(!P.StartupEnabled()); } catch { }
            startup.IsChecked = P.StartupEnabled();
        }

        void Quit()
        {
            Log("app", "encerrado pelo usuário", null);
            probe.Stop();
            timer.Dispose();
            try { watcher?.Dispose(); } catch { }
            tray.IsVisible = false; tray.Dispose();
            quit();
        }
    }
}
