// macOS: ajustes de otimizacao consagrados e reversiveis (Modo de Pouca Energia, transparencia, agentes de terceiros no login).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EvidenceMonitor
{
    static class MacOptimize
    {
        static string Out(string exe, params string[] a) { string o; Proc.Run(exe, a, out o, 15000); return (o ?? "").Trim(); }

        public static List<OptItem> Items()
        {
            var l = new List<OptItem>();
            bool laptop = Out("/usr/bin/pmset", "-g", "batt").Contains("InternalBattery");

            // --- CPU e energia ---
            var lp = Regex.Match(Out("/usr/bin/pmset", "-g"), @"lowpowermode\s+(\d)");
            if (lp.Success)
            {
                bool on = lp.Groups[1].Value == "1";
                l.Add(new OptItem
                {
                    Group = "CPU e energia", Title = "Modo de Pouca Energia desligado", State = on ? "Modo de Pouca Energia ligado" : "desligado",
                    Why = "O Modo de Pouca Energia limita a frequência da CPU e da GPU para economizar. Desligado, o Mac roda com desempenho total.",
                    Note = laptop ? "Num notebook a bateria dura menos; vale na tomada." : null,
                    Done = !on, Recommended = !laptop,
                    ApplyAdmin = "sudo pmset -a lowpowermode 0", UndoAdmin = "sudo pmset -a lowpowermode 1"
                });
            }

            // --- interface (o macOS protege essas preferencias: o app abre o painel certo de Acessibilidade) ---
            bool rt = Out("/usr/bin/defaults", "read", "com.apple.universalaccess", "reduceTransparency") == "1";
            Func<string> openDisplay = () => Proc.Run("/usr/bin/open", new[] { "x-apple.systempreferences:com.apple.preference.universalaccess?Seeing_Display" }, out _, 10000) == "ok" ? null : "não consegui abrir os Ajustes do Sistema";
            l.Add(new OptItem
            {
                Group = "Interface", Title = "Reduzir transparência", State = rt ? "ligado" : "desligado",
                Why = "Sem o efeito de vidro desfocado nos menus e janelas a GPU trabalha menos. Ajuda em Macs mais antigos e com monitor externo de alta resolução.",
                Note = "Abre Ajustes do Sistema > Acessibilidade > Tela, onde fica a opção.",
                Done = rt, Recommended = false, Apply = openDisplay, Undo = openDisplay
            });

            l.AddRange(Agents());
            return l;
        }

        // --- agentes de terceiros que abrem no login (LaunchAgents); "launchctl disable" vale a partir do proximo login ---
        static readonly Regex SafeLabel = new Regex(@"^[\w.\-]{3,120}$");

        static IEnumerable<OptItem> Agents()
        {
            var l = new List<OptItem>();
            string uid = Out("/usr/bin/id", "-u");
            if (!Regex.IsMatch(uid, @"^\d+$")) return l;
            string disabled = Out("/bin/launchctl", "print-disabled", "gui/" + uid);
            var seen = new HashSet<string>();
            foreach (var dir in new[] { Path.Combine(Paths.Home, "Library", "LaunchAgents"), "/Library/LaunchAgents" })
            {
                string[] files; try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.plist") : new string[0]; } catch { continue; }
                foreach (var f in files.OrderBy(x => x))
                {
                    string text = UnixCommon.Read(f) ?? "";
                    var lm = Regex.Match(text, @"<key>Label</key>\s*<string>([^<]+)</string>");
                    string label = lm.Success ? lm.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(f);
                    if (label.StartsWith("com.apple.") || label.IndexOf("evidence", StringComparison.OrdinalIgnoreCase) >= 0 || !SafeLabel.IsMatch(label) || !seen.Add(label)) continue;
                    bool off = Regex.IsMatch(disabled, "\"" + Regex.Escape(label) + "\"\\s*=>\\s*(disabled|true)");
                    string target = "gui/" + uid + "/" + label;
                    l.Add(new OptItem
                    {
                        Group = "Apps no login", Title = label, State = off ? "não abre no login" : "abre no login",
                        Why = "Agente de um app de terceiros que roda em segundo plano desde o login (atualizadores, ajudantes de sincronização). Desligar libera memória e CPU; o app continua abrindo normalmente.",
                        Note = "Vale a partir do próximo login. Se o app reclamar de algo, use Desfazer.",
                        Done = off, Recommended = false,
                        Apply = () => Proc.Run("/bin/launchctl", new[] { "disable", target }, out _, 10000) == "ok" ? null : "o launchctl recusou",
                        Undo = () => Proc.Run("/bin/launchctl", new[] { "enable", target }, out _, 10000) == "ok" ? null : "o launchctl recusou"
                    });
                }
            }
            return l;
        }
    }
}
