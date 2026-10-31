// Ficha do computador (processador, cache, memoria, placa-mae, numero de serie, discos), lida uma vez e guardada.
// Cada sistema preenche a lista em IPlatform.Hardware(); aqui ficam o cache, a limpeza e a formatacao comuns.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EvidenceMonitor
{
    // Rotulos usados por todos os sistemas (o painel mostra os principais na faixa de cima)
    static class Hw
    {
        public const string Cpu = "Processador", Cores = "Núcleos", Cache = "Cache", Mem = "Memória", Modules = "Pentes de memória",
                            Board = "Placa-mãe", Computer = "Computador", Serial = "Número de série", BoardSerial = "Série da placa-mãe",
                            Bios = "BIOS / firmware", Gpu = "Placa de vídeo", Disks = "Discos", Os = "Sistema", Host = "Nome do computador";

        public static readonly string[] Summary = { Cpu, Cores, Cache, Mem, Gpu, Board, Computer, Serial };

        static List<KeyValuePair<string, string>> cache;
        static readonly object Lock = new object();

        public static List<KeyValuePair<string, string>> Get()
        {
            lock (Lock)
            {
                if (cache != null) return cache;
                var l = new List<KeyValuePair<string, string>>();
                try { l = Plat.I.Hardware() ?? l; } catch { }
                var seen = new HashSet<string>();
                cache = l.Where(kv => !Junk(kv.Value) && seen.Add(kv.Key))
                         .Select(kv => new KeyValuePair<string, string>(kv.Key, Regex.Replace(kv.Value.Trim(), @"\s+", " "))).ToList();
                cache.Add(new KeyValuePair<string, string>(Host, Environment.MachineName));
                try { cache.Add(new KeyValuePair<string, string>(Os, Plat.I.OsVersion())); } catch { }
                return cache;
            }
        }

        public static string Text()
        {
            var sb = new StringBuilder();
            foreach (var kv in Get()) sb.AppendLine(string.Format("{0,-22} {1}", kv.Key + ":", kv.Value));
            return sb.ToString();
        }

        // valores de fabrica que nao dizem nada
        static readonly Regex JunkRx = new Regex(@"^(to be filled by o\.?e\.?m\.?|default string|system (serial number|product name|manufacturer|version)|not (specified|applicable|available)|none|n/?a|unknown|o\.?e\.?m\.?|x\.x|0+|1234567890|9876543210|-|chassis serial number|base board serial number)$", RegexOptions.IgnoreCase);
        public static bool Junk(string v) { return string.IsNullOrWhiteSpace(v) || JunkRx.IsMatch(v.Trim()); }

        // "Gigabyte Technology Co., Ltd." + "B460 AORUS PRO AC", sem repetir a marca
        public static string Join(params string[] parts)
        {
            var l = new List<string>();
            foreach (var p in parts)
            {
                if (Junk(p)) continue;
                string t = p.Trim();
                if (l.Any(x => x.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                l.Add(t);
            }
            return string.Join(" ", l);
        }

        // bytes -> "512 KB", "1,25 MB", "16 GB" (potencias de 1024, como o sistema mostra RAM e cache)
        public static string Bin(double b)
        {
            if (b <= 0) return "";
            string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return Fmt.N(b, b >= 100 || Math.Abs(b - Math.Round(b)) < 0.05 ? 0 : b >= 10 ? 1 : 2) + " " + u[i];
        }

        // bytes -> "500 GB", "1 TB" (potencias de 1000, como o fabricante vende o disco)
        public static string Dec(double b)
        {
            if (b <= 0) return "";
            return b >= 1e12 ? Fmt.N(b / 1e12, b >= 1e13 || Math.Abs(b / 1e12 - Math.Round(b / 1e12)) < 0.05 ? 0 : 1) + " TB" : Fmt.N(b / 1e9, 0) + " GB";
        }

        public static string Ghz(double mhz) { return mhz > 0 ? Fmt.N(mhz / 1000, 1) + " GHz" : ""; }

        public static string CoresText(int cores, int threads, double maxMhz, int sockets = 1)
        {
            var l = new List<string>();
            if (sockets > 1) l.Add(sockets + " processadores");
            if (cores > 0) l.Add(cores + (cores == 1 ? " núcleo" : " núcleos"));
            if (threads > 0 && threads != cores) l.Add(threads + " threads");
            if (maxMhz > 0) l.Add("até " + Ghz(maxMhz));
            return string.Join(" · ", l);
        }

        public static string CacheText(double l2, double l3)
        {
            var l = new List<string>();
            if (l2 > 0) l.Add("L2 " + Bin(l2));
            if (l3 > 0) l.Add("L3 " + Bin(l3));
            return string.Join(" · ", l);
        }

        // "16 GB DDR4 2666 MT/s · 2 de 4 slots ocupados"
        public static string MemText(double totalBytes, IList<MemModule> mods, int slots)
        {
            var ok = mods.Where(m => m.Size > 0).ToList();
            double inst = ok.Sum(m => m.Size);
            var l = new List<string>();
            l.Add(Bin(inst > 0 ? inst : totalBytes) + (inst > 0 && totalBytes > 0 && totalBytes < inst * 0.97 ? " (" + Bin(totalBytes) + " utilizáveis)" : ""));
            string type = ok.Select(m => m.Type).FirstOrDefault(t => !Junk(t));
            if (type != null) l[0] += " " + type;
            int spd = ok.Select(m => m.Speed).Where(s => s > 0).DefaultIfEmpty(0).Min();
            if (spd > 0) l[0] += " " + spd + " MT/s";
            if (ok.Count > 0) l.Add(slots >= ok.Count ? ok.Count + " de " + slots + " slots ocupados" : ok.Count + (ok.Count == 1 ? " pente" : " pentes"));
            return string.Join(" · ", l);
        }

        // "DIMM1: 8 GB DDR4 2666 MT/s Kingston KF3600C16D4/8GX + ..."
        public static string ModulesText(IList<MemModule> mods)
        {
            return string.Join(" + ", mods.Where(m => m.Size > 0).Select(m =>
                Join((m.Slot ?? "") + ":", Bin(m.Size), m.Type, m.Speed > 0 ? m.Speed + " MT/s" : "", m.Maker, m.Part)));
        }

        // codigo SMBIOS do tipo de memoria (Windows)
        public static string SmbiosType(int t)
        {
            switch (t)
            {
                case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4";
                case 27: return "LPDDR"; case 28: return "LPDDR2"; case 29: return "LPDDR3"; case 30: return "LPDDR4";
                case 34: return "DDR5"; case 35: return "LPDDR5";
            }
            return "";
        }
    }

    class MemModule { public string Slot, Type, Maker, Part; public double Size; public int Speed; }
}
