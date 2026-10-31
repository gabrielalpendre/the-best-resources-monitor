// Windows: ajustes de otimizacao consagrados e reversiveis (plano de energia, agendamento de GPU, Modo de Jogo,
// gravacao em segundo plano, otimizacoes para jogos em janela e apps na inicializacao, do mesmo jeito que o Gerenciador de Tarefas).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EvidenceMonitor
{
    [SupportedOSPlatform("windows")]
    static class WindowsOptimize
    {
        const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e", High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Saver = "a1841308-3541-4fab-bc81-f71556f20b4a";
        static string Sys32(string exe) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe); }

        static object Get(RegistryKey root, string path, string name) { try { using (var k = root.OpenSubKey(path)) return k == null ? null : k.GetValue(name); } catch { return null; } }
        static int? GetInt(RegistryKey root, string path, string name) { var v = Get(root, path, name); return v is int ? (int?)(int)v : null; }
        static string SetInt(string path, string name, int v)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(path)) k.SetValue(name, v, RegistryValueKind.DWord); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        static bool HasBattery()
        {
            try { using (var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Battery")) return s.Get().Count > 0; } catch { return false; }
        }

        public static List<OptItem> Items()
        {
            var l = new List<OptItem>();
            bool laptop = HasBattery();
            int build = Environment.OSVersion.Version.Build;

            // --- CPU e energia ---
            string pc = Sys32("powercfg.exe"), o;
            Proc.Run(pc, new[] { "/getactivescheme" }, out o, 15000);
            var m = Regex.Match(o ?? "", @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
            string cur = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "", curName = m.Success ? m.Groups[2].Value : "?";
            if (m.Success)
                l.Add(new OptItem
                {
                    Group = "CPU e energia", Title = "Plano de energia \"Alto desempenho\"", State = "plano atual: " + curName,
                    Why = "A CPU fica em frequência alta e sai mais rápido do modo de economia. Menos engasgos em jogos e apps pesados.",
                    Note = laptop ? "Num notebook gasta mais bateria e esquenta mais; vale na tomada." : "Consome um pouco mais de energia.",
                    Done = cur != Balanced && cur != Saver, Recommended = !laptop,
                    Apply = () =>
                    {
                        string x;
                        if (Proc.Run(pc, new[] { "/setactive", High }, out x, 15000) == "ok") return null;
                        // em PCs com Modern Standby o plano fica escondido: cria uma copia dele
                        Proc.Run(pc, new[] { "-duplicatescheme", High }, out x, 15000);
                        var g = Regex.Match(x ?? "", @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
                        return g.Success && Proc.Run(pc, new[] { "/setactive", g.Value }, out x, 15000) == "ok" ? null : "o Windows não tem o plano Alto desempenho neste PC";
                    },
                    Undo = () => { string x; return Proc.Run(pc, new[] { "/setactive", cur == High || cur == "" ? Balanced : cur }, out x, 15000) == "ok" ? null : "o powercfg falhou"; }
                });

            // --- graficos e jogos ---
            const string Gd = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
            var hags = GetInt(Registry.LocalMachine, Gd, "HwSchMode");
            if (hags.HasValue)   // so existe quando a GPU e o driver suportam
                l.Add(new OptItem
                {
                    Group = "Gráficos e jogos", Title = "Agendamento de GPU acelerado por hardware", State = hags == 2 ? "ativado" : "desativado",
                    Why = "A própria GPU organiza a fila de trabalho em vez da CPU: menos latência e menos uso de CPU em jogos e vídeo. Recurso oficial do Windows 10 2004+ e 11.",
                    Note = "Passa a valer depois de reiniciar.",
                    Done = hags == 2, Recommended = true,
                    ApplyAdmin = "reg add \"HKLM\\" + Gd + "\" /v HwSchMode /t REG_DWORD /d 2 /f", UndoAdmin = "reg add \"HKLM\\" + Gd + "\" /v HwSchMode /t REG_DWORD /d 1 /f"
                });

            const string Dx = @"Software\Microsoft\DirectX\UserGpuPreferences";
            if (build >= 22000)
            {
                string dx = Get(Registry.CurrentUser, Dx, "DirectXUserGlobalSettings") as string ?? "";
                bool on = dx.Contains("SwapEffectUpgradeEnable=1");
                Func<string, string> setSwap = val =>
                {
                    try
                    {
                        string s = Get(Registry.CurrentUser, Dx, "DirectXUserGlobalSettings") as string ?? "";
                        s = Regex.Replace(s, @"SwapEffectUpgradeEnable=\d;?", "");
                        using (var k = Registry.CurrentUser.CreateSubKey(Dx)) k.SetValue("DirectXUserGlobalSettings", s + "SwapEffectUpgradeEnable=" + val + ";", RegistryValueKind.String);
                        return null;
                    }
                    catch (Exception ex) { return ex.Message; }
                };
                l.Add(new OptItem
                {
                    Group = "Gráficos e jogos", Title = "Otimizações para jogos em janela", State = on ? "ativado" : "desativado",
                    Why = "Jogos DirectX 10/11 em janela ou janela sem borda passam a usar o modelo de apresentação moderno: menos latência e suporte a taxa de atualização variável.",
                    Note = "Opção oficial do Windows 11 (Configurações > Sistema > Tela > Gráficos).",
                    Done = on, Recommended = true,
                    Apply = () => setSwap("1"), Undo = () => setSwap("0")
                });
            }

            const string Gb = @"Software\Microsoft\GameBar";
            var gm = GetInt(Registry.CurrentUser, Gb, "AutoGameModeEnabled");
            l.Add(new OptItem
            {
                Group = "Gráficos e jogos", Title = "Modo de Jogo do Windows", State = gm == 0 ? "desativado" : "ativado",
                Why = "Durante o jogo o Windows dá prioridade a ele e adia atualizações e tarefas de fundo.",
                Done = gm != 0, Recommended = true,
                Apply = () => SetInt(Gb, "AutoGameModeEnabled", 1), Undo = () => SetInt(Gb, "AutoGameModeEnabled", 0)
            });

            const string Gcs = @"System\GameConfigStore", Dvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
            var dvr = GetInt(Registry.CurrentUser, Gcs, "GameDVR_Enabled"); var cap = GetInt(Registry.CurrentUser, Dvr, "AppCaptureEnabled");
            bool recording = dvr != 0 || cap == 1;
            l.Add(new OptItem
            {
                Group = "Gráficos e jogos", Title = "Gravação de jogos em segundo plano (Xbox Game Bar)", State = recording ? "ligada" : "desligada",
                Why = "A gravação em segundo plano fica capturando a tela o tempo todo durante o jogo e custa FPS. A Game Bar continua abrindo com Win+G.",
                Note = "Se você usa \"Gravar o que aconteceu\" (Win+Alt+G), use Desfazer.",
                Done = !recording, Recommended = true,
                Apply = () => SetInt(Gcs, "GameDVR_Enabled", 0) ?? SetInt(Dvr, "AppCaptureEnabled", 0),
                Undo = () => SetInt(Gcs, "GameDVR_Enabled", 1) ?? SetInt(Dvr, "AppCaptureEnabled", 1)
            });

            l.AddRange(Startup());
            return l;
        }

        // --- apps na inicializacao: o mesmo liga/desliga do Gerenciador de Tarefas (StartupApproved; 02 = ligado, 03 = desligado) ---
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run", Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        static readonly Regex SafeName = new Regex(@"^[\w .\-()+&']{1,80}$");
        static readonly Regex Skip = new Regex(@"^(SecurityHealth|EvidenceMonitor|Windows Defender|WindowsDefender)", RegexOptions.IgnoreCase);
        static readonly byte[] On = { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Off = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        static IEnumerable<OptItem> Startup()
        {
            var l = new List<OptItem>();
            // (raiz, chave com os apps ou pasta, subchave de aprovacao, e pasta?)
            var sources = new List<Tuple<RegistryKey, string, string, bool>> {
                Tuple.Create(Registry.CurrentUser, Run, "Run", false),
                Tuple.Create(Registry.LocalMachine, Run, "Run", false),
                Tuple.Create(Registry.LocalMachine, Run32, "Run32", false),
                Tuple.Create(Registry.CurrentUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "StartupFolder", true),
                Tuple.Create(Registry.LocalMachine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "StartupFolder", true),
            };
            foreach (var src in sources)
            {
                var names = new List<string>();
                try
                {
                    if (src.Item4) { if (Directory.Exists(src.Item2)) names.AddRange(Directory.GetFiles(src.Item2).Select(Path.GetFileName).Where(n => !n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))); }
                    else using (var k = src.Item1.OpenSubKey(src.Item2)) if (k != null) names.AddRange(k.GetValueNames().Where(n => n.Length > 0));
                }
                catch { }
                bool user = src.Item1 == Registry.CurrentUser;
                string apKey = Approved + src.Item3;
                foreach (var name in names)
                {
                    if (Skip.IsMatch(name)) continue;
                    var b = Get(src.Item1, apKey, name) as byte[];
                    bool off = b != null && b.Length > 0 && (b[0] & 1) == 1;
                    string label = src.Item4 ? Path.GetFileNameWithoutExtension(name) : name, n = name;
                    var it = new OptItem
                    {
                        Group = "Apps na inicialização", Title = label, State = off ? "não abre na inicialização" : "abre na inicialização",
                        Why = "Abre sozinho quando você entra no Windows. Desligar acelera o login e libera memória; o app continua instalado e abre normalmente pelo menu Iniciar.",
                        Note = "É o mesmo liga/desliga da aba Inicializar do Gerenciador de Tarefas.",
                        Done = off, Recommended = false
                    };
                    if (user)
                    {
                        it.Apply = () => SetApproved(apKey, n, Off);
                        it.Undo = () => SetApproved(apKey, n, On);
                    }
                    else if (SafeName.IsMatch(name))
                    {
                        it.ApplyAdmin = "reg add \"HKLM\\" + apKey + "\" /v \"" + name + "\" /t REG_BINARY /d 030000000000000000000000 /f";
                        it.UndoAdmin = "reg add \"HKLM\\" + apKey + "\" /v \"" + name + "\" /t REG_BINARY /d 020000000000000000000000 /f";
                    }
                    else it.Note += " Este item é do sistema todo: desligue pelo Gerenciador de Tarefas.";
                    l.Add(it);
                }
            }
            return l;
        }

        static string SetApproved(string key, string name, byte[] v)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name, v, RegistryValueKind.Binary); return null; }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
