// Linux: ajustes de otimizacao consagrados e reversiveis (systemd, tuned/power-profiles, zram, GameMode, autostart do XDG).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EvidenceMonitor
{
    static class LinuxOptimize
    {
        static readonly string Systemctl = Proc.Find("/usr/bin/systemctl", "/bin/systemctl");
        static readonly string Tuned = Proc.Find("/usr/sbin/tuned-adm", "/usr/bin/tuned-adm");
        static readonly string Ppd = Proc.Find("/usr/bin/powerprofilesctl");
        static readonly string Lpstat = Proc.Find("/usr/bin/lpstat");
        static readonly string Gsettings = Proc.Find("/usr/bin/gsettings");

        static string Out(string exe, params string[] a)
        {
            if (exe == null) return "";
            string o; Proc.Run(exe, a, out o, 15000);
            return (o ?? "").Trim();
        }
        // "enabled", "disabled", "masked", "static"... ou "" se a unidade nao existe
        static string UnitState(string unit) { return Out(Systemctl, "is-enabled", unit); }
        static bool UnitOn(string unit) { var s = UnitState(unit); return s == "enabled" || s == "enabled-runtime"; }

        // bateria do proprio PC (mouse, headset e controle sem fio aparecem com scope=Device e nao contam)
        static bool HasBattery()
        {
            try { return Directory.GetDirectories("/sys/class/power_supply").Any(d => UnixCommon.ReadLine(Path.Combine(d, "type")) == "Battery" && UnixCommon.ReadLine(Path.Combine(d, "scope")) != "Device"); }
            catch { return false; }
        }

        static string Distro()
        {
            string os = UnixCommon.Read("/etc/os-release") ?? "";
            var id = Regex.Match(os, "^ID=\"?([^\"\\n]+)", RegexOptions.Multiline).Groups[1].Value + " " + Regex.Match(os, "^ID_LIKE=\"?([^\"\\n]+)", RegexOptions.Multiline).Groups[1].Value;
            return Regex.IsMatch(id, @"\b(fedora|rhel|centos)\b") ? "fedora" : Regex.IsMatch(id, @"\b(debian|ubuntu)\b") ? "debian" : Regex.IsMatch(id, @"\barch\b") ? "arch" : Regex.IsMatch(id, @"\bsuse\b") ? "suse" : "";
        }
        static string Install(string pkg, string d) { return d == "fedora" ? "sudo dnf install -y " + pkg : d == "debian" ? "sudo apt install -y " + pkg : d == "arch" ? "sudo pacman -S --needed " + pkg : d == "suse" ? "sudo zypper install " + pkg : null; }
        static string Remove(string pkg, string d) { return d == "fedora" ? "sudo dnf remove " + pkg : d == "debian" ? "sudo apt remove " + pkg : d == "arch" ? "sudo pacman -R " + pkg : d == "suse" ? "sudo zypper remove " + pkg : null; }

        public static List<OptItem> Items()
        {
            var l = new List<OptItem>();
            bool laptop = HasBattery();
            string distro = Distro();

            // --- CPU e energia ---
            if (Ppd != null)
            {
                string cur = Out(Ppd, "get"); bool hasPerf = Out(Ppd, "list").Contains("performance:");
                if (hasPerf)
                    l.Add(new OptItem
                    {
                        Group = "CPU e energia", Title = "Perfil de energia \"desempenho\"", State = "perfil atual: " + cur,
                        Why = "A CPU sobe de frequência mais rápido e fica menos tempo em economia: o sistema responde melhor em jogos, compilação e apps pesados.",
                        Note = laptop ? "Num notebook gasta mais bateria e esquenta mais; vale na tomada." : "Consome um pouco mais de energia.",
                        Done = cur == "performance", Recommended = !laptop,
                        Apply = () => Proc.Run(Ppd, new[] { "set", "performance" }, out _, 15000) == "ok" ? null : "o power-profiles-daemon recusou a mudança",
                        Undo = () => Proc.Run(Ppd, new[] { "set", "balanced" }, out _, 15000) == "ok" ? null : "o power-profiles-daemon recusou a mudança"
                    });
            }
            else if (Tuned != null)
            {
                var m = Regex.Match(Out(Tuned, "active"), @":\s*(\S+)");
                string cur = m.Success ? m.Groups[1].Value : "?";
                if (Out(Tuned, "list").Contains("throughput-performance"))
                    l.Add(new OptItem
                    {
                        Group = "CPU e energia", Title = "Perfil do tuned \"throughput-performance\"", State = "perfil atual: " + cur,
                        Why = "Perfil oficial do tuned para desempenho: CPU no governador de desempenho e menos economia agressiva. O sistema responde melhor em jogos e apps pesados.",
                        Note = laptop ? "Num notebook gasta mais bateria e esquenta mais; vale na tomada." : "Consome um pouco mais de energia.",
                        Done = cur == "throughput-performance" || cur == "latency-performance", Recommended = !laptop,
                        ApplyAdmin = "sudo tuned-adm profile throughput-performance", UndoAdmin = "sudo tuned-adm profile " + (cur == "throughput-performance" || cur == "?" ? "balanced" : cur)
                    });
            }

            // --- graficos e jogos ---
            bool gamemode = Proc.Find("/usr/bin/gamemoded", "/usr/games/gamemoded") != null;
            bool steam = Proc.Find("/usr/bin/steam", "/usr/games/steam") != null || Directory.Exists(Path.Combine(Paths.Home, ".var/app/com.valvesoftware.Steam")) || Directory.Exists(Path.Combine(Paths.Home, ".steam"));
            if (gamemode || Install("gamemode", distro) != null)
                l.Add(new OptItem
                {
                    Group = "Gráficos e jogos", Title = "GameMode (Feral Interactive)", State = gamemode ? "instalado" : "não instalado",
                    Why = "Enquanto um jogo roda, o GameMode põe a CPU em desempenho máximo, dá prioridade ao processo do jogo e pede o modo de desempenho à GPU. Fora do jogo, nada muda.",
                    Note = "Na Steam, use a opção de inicialização: gamemoderun %command%. Muitos jogos e o Lutris ativam sozinhos.",
                    Done = gamemode, Recommended = steam,
                    ApplyAdmin = Install("gamemode", distro), UndoAdmin = Remove("gamemode", distro)
                });

            // --- disco e memoria ---
            bool ssd = false;
            try { ssd = Directory.GetDirectories("/sys/block").Any(d => !Regex.IsMatch(Path.GetFileName(d), "^(loop|ram|zram|dm-|sr)") && UnixCommon.ReadLine(Path.Combine(d, "queue", "rotational")) == "0"); } catch { }
            if (ssd && UnitState("fstrim.timer") != "")
                l.Add(new OptItem
                {
                    Group = "Disco", Title = "TRIM semanal do SSD (fstrim.timer)", State = UnitOn("fstrim.timer") ? "ativado" : "desativado",
                    Why = "Avisa o SSD uma vez por semana quais blocos estão livres. Mantém a velocidade de escrita e a vida útil do SSD.",
                    Done = UnitOn("fstrim.timer"), Recommended = true,
                    ApplyAdmin = "sudo systemctl enable --now fstrim.timer", UndoAdmin = "sudo systemctl disable --now fstrim.timer"
                });

            bool zram = (UnixCommon.Read("/proc/swaps") ?? "").Contains("/zram");
            string zpkg = distro == "fedora" ? "zram-generator-defaults" : distro == "debian" ? "systemd-zram-generator" : null;
            if (zram || zpkg != null)
                l.Add(new OptItem
                {
                    Group = "Memória", Title = "Swap comprimido na RAM (zram)", State = zram ? "ativo" : "não configurado",
                    Why = "Quando a RAM aperta, o sistema comprime páginas na própria memória em vez de jogar no disco. Bem mais rápido que swap em disco e evita travamentos por falta de memória.",
                    Note = "Passa a valer depois de reiniciar. É o padrão do Fedora desde a versão 33.",
                    Done = zram, Recommended = true,
                    ApplyAdmin = zpkg != null ? Install(zpkg, distro) : null, UndoAdmin = zpkg != null ? Remove(zpkg, distro) : null
                });

            // --- servicos que nao precisam rodar ---
            bool modem = false;
            try { modem = Directory.GetFiles("/dev", "cdc-wdm*").Length > 0 || Directory.GetDirectories("/sys/class/net").Any(d => Path.GetFileName(d).StartsWith("ww")); } catch { }
            if (!modem && UnitOn("ModemManager.service"))
                l.Add(new OptItem
                {
                    Group = "Serviços sem uso", Title = "ModemManager (modem 3G/4G)", State = "ativado · nenhum modem encontrado",
                    Why = "Serviço que só serve para modems de celular (3G/4G/5G). Sem modem, ele fica rodando e sondando portas seriais à toa.",
                    Note = "Se um dia usar um modem USB ou chip de celular no PC, use Desfazer.",
                    Recommended = true,
                    ApplyAdmin = "sudo systemctl disable --now ModemManager.service", UndoAdmin = "sudo systemctl enable --now ModemManager.service"
                });

            bool noPrinter = Lpstat != null && !Regex.IsMatch(Out(Lpstat, "-v"), @"device for|dispositivo para", RegexOptions.IgnoreCase);
            if (noPrinter && (UnitOn("cups.socket") || UnitOn("cups.service")))
                l.Add(new OptItem
                {
                    Group = "Serviços sem uso", Title = "Serviço de impressão (CUPS)", State = "ativado · nenhuma impressora configurada",
                    Why = "Sem impressora, o CUPS só fica procurando impressoras na rede e acordando no fundo.",
                    Note = "Se for instalar uma impressora, use Desfazer antes. Salvar como PDF pelo navegador continua funcionando.",
                    Recommended = false,
                    ApplyAdmin = "sudo systemctl disable --now cups.socket cups.path cups.service", UndoAdmin = "sudo systemctl enable --now cups.socket cups.path cups.service"
                });

            bool bt = false; try { bt = Directory.Exists("/sys/class/bluetooth") && Directory.GetFileSystemEntries("/sys/class/bluetooth").Length > 0; } catch { }
            if (!bt && UnitOn("bluetooth.service"))
                l.Add(new OptItem
                {
                    Group = "Serviços sem uso", Title = "Bluetooth sem adaptador", State = "ativado · nenhum adaptador Bluetooth",
                    Why = "O computador não tem Bluetooth: o serviço roda sem ter o que fazer.",
                    Note = "Se instalar um adaptador Bluetooth USB, use Desfazer.",
                    Recommended = true,
                    ApplyAdmin = "sudo systemctl disable --now bluetooth.service", UndoAdmin = "sudo systemctl enable --now bluetooth.service"
                });

            if (UnitState("NetworkManager-wait-online.service") != "")
                l.Add(new OptItem
                {
                    Group = "Inicialização", Title = "Esperar a rede para terminar o boot", State = UnitOn("NetworkManager-wait-online.service") ? "ativado" : "desativado",
                    Why = "Por padrão o boot espera a rede conectar antes de seguir. Num desktop isso só atrasa o boot alguns segundos.",
                    Note = "Deixe ativado se o PC monta pastas de rede (NFS/SMB) no boot pelo /etc/fstab ou roda servidores que precisam da rede ao iniciar.",
                    Done = !UnitOn("NetworkManager-wait-online.service"), Recommended = false,
                    ApplyAdmin = "sudo systemctl disable NetworkManager-wait-online.service", UndoAdmin = "sudo systemctl enable NetworkManager-wait-online.service"
                });

            // --- interface ---
            string desk = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToUpperInvariant();
            if (Gsettings != null && desk.Contains("GNOME"))
            {
                bool anim = Out(Gsettings, "get", "org.gnome.desktop.interface", "enable-animations") != "false";
                l.Add(new OptItem
                {
                    Group = "Interface", Title = "Animações do GNOME", State = anim ? "ligadas" : "desligadas",
                    Why = "Sem animações, janelas e a visão geral abrem na hora e a GPU trabalha menos. Ajuda em PCs mais fracos.",
                    Done = !anim, Recommended = false,
                    Apply = () => Proc.Run(Gsettings, new[] { "set", "org.gnome.desktop.interface", "enable-animations", "false" }, out _, 10000) == "ok" ? null : "o gsettings falhou",
                    Undo = () => Proc.Run(Gsettings, new[] { "set", "org.gnome.desktop.interface", "enable-animations", "true" }, out _, 10000) == "ok" ? null : "o gsettings falhou"
                });
            }

            l.AddRange(Autostart(desk));
            return l;
        }

        // --- apps que abrem no login (padrao XDG: um .desktop com Hidden=true em ~/.config/autostart desliga o do sistema) ---
        const string Marker = "X-EvidenceMonitor-Override=true";
        static string UserAutostart { get { var x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"); return Path.Combine(string.IsNullOrEmpty(x) ? Path.Combine(Paths.Home, ".config") : x, "autostart"); } }

        // do sistema, so os que sao opcionais com certeza (o resto do /etc/xdg/autostart e parte da sessao)
        static readonly Dictionary<string, string> OptionalSystem = new Dictionary<string, string> {
            { "org.gnome.Evolution-alarm-notify.desktop", "Lembretes de agenda do Evolution: inútil se você não usa o calendário do Evolution." },
            { "org.kde.kdeconnect.daemon.desktop", "KDE Connect (integração com o celular): só serve se você pareou um celular." },
            { "org.kde.discover.notifier.desktop", "Avisos de atualização do Discover: o sistema continua atualizável pela loja ou terminal." },
            { "geoclue-demo-agent.desktop", "Agente de localização: só serve para apps que pedem sua localização (mapas, clima)." },
            { "org.gnome.Software.desktop", "GNOME Software em segundo plano: verifica e baixa atualizações sozinho e ocupa memória. A loja continua abrindo normalmente." },
            { "org.kde.kalendarac.desktop", "Lembretes de agenda do KDE: inútil se você não usa o calendário do KDE." },
        };

        static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>();
            bool inEntry = false;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[")) { inEntry = line == "[Desktop Entry]"; continue; }
                int i = line.IndexOf('='); if (!inEntry || i <= 0) continue;
                var k = line.Substring(0, i).Trim(); if (!d.ContainsKey(k)) d[k] = line.Substring(i + 1).Trim();
            }
            return d;
        }

        static bool ShowsIn(Dictionary<string, string> d, string desk)
        {
            var mine = desk.Split(':').Where(x => x.Length > 0).ToList();
            string v;
            if (d.TryGetValue("OnlyShowIn", out v) && !v.ToUpperInvariant().Split(';').Any(x => mine.Contains(x))) return false;
            if (d.TryGetValue("NotShowIn", out v) && v.ToUpperInvariant().Split(';').Any(x => mine.Contains(x))) return false;
            return true;
        }

        static bool Disabled(Dictionary<string, string> d)
        {
            string v;
            return (d.TryGetValue("Hidden", out v) && v.Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                   (d.TryGetValue("X-GNOME-Autostart-enabled", out v) && v.Equals("false", StringComparison.OrdinalIgnoreCase));
        }

        static IEnumerable<OptItem> Autostart(string desk)
        {
            var l = new List<OptItem>();
            var names = new HashSet<string>();
            try { if (Directory.Exists(UserAutostart)) foreach (var f in Directory.GetFiles(UserAutostart, "*.desktop")) names.Add(Path.GetFileName(f)); } catch { }
            foreach (var n in OptionalSystem.Keys) if (File.Exists(Path.Combine("/etc/xdg/autostart", n))) names.Add(n);

            foreach (var name in names.OrderBy(x => x))
            {
                if (name.StartsWith("evidence-monitor")) continue;
                string user = Path.Combine(UserAutostart, name), sys = Path.Combine("/etc/xdg/autostart", name);
                string userText = File.Exists(user) ? UnixCommon.Read(user) : null, sysText = File.Exists(sys) ? UnixCommon.Read(sys) : null;
                // o arquivo do usuario que so tem o nosso Hidden=true nao e um app proprio do usuario
                bool ownUserApp = userText != null && !userText.Contains(Marker) && sysText == null;
                if (!ownUserApp && !OptionalSystem.ContainsKey(name)) continue;
                var d = Parse(userText ?? sysText);
                var dsys = sysText != null ? Parse(sysText) : d;
                if (!ShowsIn(dsys, desk)) continue;
                if (sysText != null && Disabled(dsys) && userText == null) continue;   // o proprio sistema ja deixa desligado
                string app; if (!d.TryGetValue("Name", out app)) app = name;
                bool off = Disabled(d);
                string why = OptionalSystem.ContainsKey(name) ? OptionalSystem[name] : "App que abre sozinho quando você entra na sessão. Desligar acelera o login e libera memória; o app continua instalado e abre normalmente pelo menu.";
                string n2 = name;
                l.Add(new OptItem
                {
                    Group = "Apps no login", Title = app, State = off ? "não abre no login" : "abre no login",
                    Why = why, Note = "Vale a partir do próximo login.",
                    Done = off, Recommended = false,
                    Apply = () => SetHidden(n2, true), Undo = () => SetHidden(n2, false)
                });
            }
            return l;
        }

        static string SetHidden(string name, bool hide)
        {
            string user = Path.Combine(UserAutostart, name), sys = Path.Combine("/etc/xdg/autostart", name);
            Directory.CreateDirectory(UserAutostart);
            string text = File.Exists(user) ? File.ReadAllText(user) : null;
            if (hide)
            {
                if (text == null)
                {
                    // sobrescreve o do sistema so para este usuario
                    text = File.Exists(sys) ? File.ReadAllText(sys) : "[Desktop Entry]\nType=Application\n";
                    text = Regex.Replace(text, @"^(Hidden|X-GNOME-Autostart-enabled)=.*\n?", "", RegexOptions.Multiline);
                    text = text.Replace("[Desktop Entry]", "[Desktop Entry]\nHidden=true\n" + Marker);
                }
                else
                {
                    text = Regex.Replace(text, @"^(Hidden|X-GNOME-Autostart-enabled)=.*\n?", "", RegexOptions.Multiline);
                    text = text.Replace("[Desktop Entry]", "[Desktop Entry]\nHidden=true");
                }
                File.WriteAllText(user, text);
            }
            else if (text != null)
            {
                if (text.Contains(Marker)) File.Delete(user);
                else File.WriteAllText(user, Regex.Replace(text, @"^(Hidden=true|X-GNOME-Autostart-enabled=false)\s*\n?", "", RegexOptions.Multiline | RegexOptions.IgnoreCase));
            }
            return null;
        }
    }
}
