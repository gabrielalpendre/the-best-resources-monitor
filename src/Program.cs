// Monitor de Evidencias - ponto de entrada (Windows, macOS e Linux).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace EvidenceMonitor
{
    class App : Application
    {
        public static string[] Args = new string[0];
        public static bool Elevated;
        TrayApp tray;

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Light;
            Resources["SystemAccentColor"] = Pal.Series;
            Resources["SystemAccentColorDark1"] = Pal.Series;
            Resources["SystemAccentColorLight1"] = Pal.Series;
            Name = Dialog.AppTitle;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var life = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
            life.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string arg = Args.Length > 0 ? Args[0].ToLowerInvariant() : "";
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (Elevated)
                    {
                        await Dialog.Info("O Monitor de Evidências não roda como administrador, por segurança.\n\nAbra normalmente, com o seu usuário.");
                        life.Shutdown(2); return;
                    }
                    if (arg == "--snapshot") { Snapshot(life); return; }
                    if (arg == "--report")
                    {
                        int h = 24; if (Args.Length > 1) int.TryParse(Args[1], out h);
                        string f = Report.Build(h);
                        await ReportDone.Show(f); life.Shutdown(); return;
                    }
                    if (arg == "--uninstall")
                    {
                        Program.Uninstall();
                        await Dialog.Info("Monitor removido da inicialização.\nAs evidências coletadas continuam em:\n" + Paths.Root);
                        life.Shutdown(); return;
                    }
                    if (!Program.AcquireSingleInstance())
                    {
                        await Dialog.Info("O Monitor de Evidências já está rodando (ícone na bandeja / barra de menus).");
                        life.Shutdown(); return;
                    }
                    var P = Plat.I;
                    // instalado por pacote (.dmg/.deb): liga o inicio com o login so na primeira vez
                    string firstRun = Path.Combine(Paths.Root, "primeira-execucao");
                    if (P.IsInstalled() && !File.Exists(firstRun))
                    {
                        try { if (!P.StartupEnabled()) P.SetStartup(true); File.WriteAllText(firstRun, Fmt.Ts(DateTime.Now)); } catch { }
                    }
                    if (!P.IsInstalled() && !P.StartupEnabled() && arg != "--panel")
                    {
                        int r = await Dialog.Ask("Instalar o Monitor de Evidências e iniciar junto com o login?\n\nSim: instala em " + P.InstallTarget + " (sem admin)\nNão: só roda agora",
                            new[] { "Sim", "Não", "Cancelar" }, 0);
                        if (r == 2 || r == -1) { life.Shutdown(); return; }
                        if (r == 0)
                        {
                            P.Install();
                            Program.ReleaseSingleInstance();
                            P.LaunchInstalled(null);
                            life.Shutdown(); return;
                        }
                    }
                    tray = new TrayApp(arg == "--panel", () => life.Shutdown());
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(Paths.Root, "erro.txt"), ex.ToString()); } catch { }
                    await Dialog.Info("Falha ao iniciar: " + ex.Message);
                    life.Shutdown(1);
                }
            });
            base.OnFrameworkInitializationCompleted();
        }

        // --snapshot <saida.png> [ciclos] [aba] [filtro] [hover: segundos atras] -> renderiza o painel e salva (para testes)
        static async void Snapshot(IClassicDesktopStyleApplicationLifetime life)
        {
            string outp = Args.Length > 1 ? Args[1] : "panel.png";
            try
            {
                await System.Threading.Tasks.Task.Run(() => History.LoadInitial());
                var smp = new Sampler(); List<ProcInfo> procs = null; Dictionary<string, object> row = null;
                for (int i = 0; i < 3; i++) { row = smp.Sample(out procs); History.Add(row, procs); await System.Threading.Tasks.Task.Delay(2000); }
                var form = new PanelWindow { WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000), ShowInTaskbar = false };
                form.Show();
                int loops = Args.Length > 2 ? int.Parse(Args[2]) : 1;
                for (int i = 0; i < loops; i++) { row = smp.Sample(out procs); History.Add(row, procs); form.UpdateData(row, procs); if (i < loops - 1) await System.Threading.Tasks.Task.Delay(Program.PanelMs); }
                if (Args.Length > 4 && Args[4].Length > 0) form.SetFilter(Args[4]);
                if (Args.Length > 3 && Args[3] == "eventos") { form.SelectTab(1); await System.Threading.Tasks.Task.Delay(6000); }
                if (Args.Length > 3 && Args[3] == "cpu") { form.SelectTab(2); await System.Threading.Tasks.Task.Delay(4000); }
                int ago; if (Args.Length > 5 && int.TryParse(Args[5], out ago)) form.SimulateHover(DateTime.Now.AddSeconds(-ago));
                await System.Threading.Tasks.Task.Delay(500);
                var size = new PixelSize((int)form.Bounds.Width, (int)form.Bounds.Height);
                using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
                {
                    bmp.Render(form);
                    bmp.Save(outp);
                }
                form.Close();
            }
            catch (Exception ex) { File.WriteAllText(outp + ".err.txt", ex.ToString()); }
            life.Shutdown();
        }
    }

    static class Program
    {
        public const int IntervalSec = 10;
        public const int PanelMs = 3000;

        public static string Summary(Dictionary<string, object> r)
        {
            Func<string, double> n = k => Fmt.D(r, k);
            string s = "CPU " + Fmt.N(n("cpu"), 0) + "%" + (n("freq") > 0 ? " · " + Fmt.N(n("freq"), 0) + " MHz" : "") + " · RAM livre " + Fmt.N(n("mem_avail") / 1024, 1) + " GB · commit " + Fmt.N(n("commit"), 0) + "%";
            if (n("disk_busy") >= 0) s += " · disco " + Fmt.N(n("disk_busy"), 0) + "%";
            return s;
        }

        static FileStream instanceLock;
        public static bool AcquireSingleInstance()
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                instanceLock = new FileStream(Path.Combine(Paths.Root, "instancia.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch { return false; }
        }
        public static void ReleaseSingleInstance() { try { instanceLock?.Dispose(); instanceLock = null; } catch { } }

        public static void Uninstall()
        {
            Plat.I.SetStartup(false);
            string self = Path.GetFileNameWithoutExtension(Paths.Self);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(self))
                if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            // Windows: carregamento de DLL restrito ao System32 (evita DLL plantada ao lado do exe)
            if (OperatingSystem.IsWindows()) WindowsPlatform.HardenDllSearch();
            App.Args = args;
            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            // Nunca roda como administrador/root: instala e roda so com as permissoes de quem executou
            if (Plat.I.IsElevated() && arg != "--selftest")
            {
                Console.Error.WriteLine("O Monitor de Evidências não roda como administrador/root, por segurança. Abra normalmente.");
                App.Elevated = true;
                BuildApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
                return 2;
            }

            if (arg == "--selftest")
            {
                // coleta 2 amostras sem interface e grava em JSON (para conferir os sensores em cada sistema)
                var s = new Sampler(); List<ProcInfo> procs;
                s.Sample(out procs); Thread.Sleep(2000);
                var row = s.Sample(out procs);
                long idle, total; Plat.I.CpuTimes(out idle, out total);
                row["_procs"] = procs.Count;
                row["_top"] = procs.OrderByDescending(p => p.Priv).Take(8).Select(p => p.Name + " pid=" + p.Pid + " priv=" + Fmt.N(p.Priv / 1048576.0, 0) + "MB cpu=" + Fmt.N(p.Cpu, 1) + "% " + (p.Agent ?? p.Service ?? "")).ToArray();
                row["_events_24h"] = Plat.I.ReadEvents(DateTime.Now.AddHours(-24)).Count;
                row["_events_hint"] = Plat.I.EventsHint;
                row["_info"] = Plat.I.Model() + " | " + Plat.I.CpuName() + " | " + Plat.I.OsVersion() + " | boot " + Plat.I.BootTime();
                string json = Json.Serialize(row);
                if (args.Length > 1) File.WriteAllText(args[1], json); else Console.WriteLine(json);
                return 0;
            }

            if (arg == "--install") { Plat.I.Install(); Plat.I.LaunchInstalled(null); return 0; }
            if (arg == "--report" && args.Contains("--quiet"))
            {
                // sem interface: so gera e imprime o caminho (para scripts e testes)
                int h = 24; if (args.Length > 1) int.TryParse(args[1], out h);
                Console.WriteLine(Report.Build(h));
                return 0;
            }

            BuildApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            return 0;
        }

        public static AppBuilder BuildApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .LogToTrace();
        }
    }
}
