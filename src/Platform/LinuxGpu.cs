// Linux: placas de video em /sys/class/drm. Uso: AMD pelo amdgpu (gpu_busy_percent, sem root), NVIDIA pelo nvidia-smi.
// Intel nao expoe o uso sem root (precisaria de perf/intel_gpu_top): fica so o modelo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EvidenceMonitor
{
    static class LinuxGpu
    {
        class Dev { public string Dir, Vendor, Name; public double VramTotal; }

        static List<Dev> devs;
        static readonly string NvidiaSmi = Proc.Find("/usr/bin/nvidia-smi", "/bin/nvidia-smi");

        static List<Dev> Devs()
        {
            if (devs != null) return devs;
            var l = new List<Dev>();
            try
            {
                foreach (var card in Directory.GetDirectories("/sys/class/drm").Where(d => Regex.IsMatch(Path.GetFileName(d), @"^card\d+$")).OrderBy(d => d))
                {
                    string dev = Path.Combine(card, "device");
                    string ven = Hex(Path.Combine(dev, "vendor")), id = Hex(Path.Combine(dev, "device"));
                    if (ven == null || id == null) continue;
                    var d = new Dev { Dir = dev, Vendor = ven, Name = PciName(ven, id) };
                    long b; if (long.TryParse(UnixCommon.ReadLine(Path.Combine(dev, "mem_info_vram_total")), out b)) d.VramTotal = b;
                    l.Add(d);
                }
                if (NvidiaSmi != null && l.Any(d => d.Vendor == "10de"))
                {
                    // nome comercial e memoria total direto do driver
                    string o; Proc.Run(NvidiaSmi, new[] { "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" }, out o, 10000);
                    var lines = (o ?? "").Split('\n').Where(x => x.Contains(",")).ToList();
                    int i = 0;
                    foreach (var d in l.Where(x => x.Vendor == "10de"))
                    {
                        if (i >= lines.Count) break;
                        var p = lines[i++].Split(',');
                        d.Name = "NVIDIA " + p[0].Trim().Replace("NVIDIA ", "");
                        double mib; if (double.TryParse(p[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out mib)) d.VramTotal = mib * 1048576;
                    }
                }
            }
            catch { }
            return devs = l;
        }

        static string Hex(string f) { var s = UnixCommon.ReadLine(f); return s != null && s.StartsWith("0x") ? s.Substring(2).ToLowerInvariant() : null; }

        // "Navi 23 [Radeon RX 6600/6600 XT/6600M]" -> "AMD Radeon RX 6600/6600 XT/6600M (Navi 23)"
        static string PciName(string ven, string id)
        {
            string brand = ven == "1002" ? "AMD" : ven == "10de" ? "NVIDIA" : ven == "8086" ? "Intel" : null, name = null, vname = null;
            string file = Proc.Find("/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids");
            if (file != null)
                try
                {
                    bool inVen = false;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.Length < 6 || line[0] == '#') continue;
                        if (line[0] != '\t') { if (inVen) break; inVen = line.StartsWith(ven + " "); if (inVen) vname = line.Substring(4).Trim(); continue; }
                        if (inVen && line[1] != '\t' && line.Substring(1).StartsWith(id + " ")) { name = line.Substring(5).Trim(); break; }
                    }
                }
                catch { }
            if (name == null) return (brand ?? vname ?? "GPU") + " [" + ven + ":" + id + "]";
            var m = Regex.Match(name, @"^(.*?)\s*\[(.+)\]\s*$");
            if (m.Success) name = m.Groups[2].Value + (m.Groups[1].Value.Length > 0 ? " (" + m.Groups[1].Value + ")" : "");
            return (brand ?? vname ?? "") + " " + name;
        }

        public static string Describe()
        {
            return string.Join(" + ", Devs().Select(d => d.Name.Trim() + (d.VramTotal > 0 ? " · " + Hw.Bin(d.VramTotal) : "")));
        }

        // uso da GPU mais ocupada (%) e memoria de video em uso (GB)
        public static void Read(Dictionary<string, object> row)
        {
            double busy = -1, used = -1, total = -1;
            foreach (var d in Devs())
            {
                if (d.Vendor != "1002") continue;
                double v; long b;
                if (double.TryParse(UnixCommon.ReadLine(Path.Combine(d.Dir, "gpu_busy_percent")), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) busy = Math.Max(busy, v);
                if (long.TryParse(UnixCommon.ReadLine(Path.Combine(d.Dir, "mem_info_vram_used")), out b)) used = Math.Max(0, used) + b / 1073741824.0;
                if (d.VramTotal > 0) total = Math.Max(0, total) + d.VramTotal / 1073741824.0;
            }
            if (NvidiaSmi != null && Devs().Any(d => d.Vendor == "10de"))
            {
                string o; Proc.Run(NvidiaSmi, new[] { "--query-gpu=utilization.gpu,memory.used,memory.total", "--format=csv,noheader,nounits" }, out o, 5000);
                foreach (var line in (o ?? "").Split('\n'))
                {
                    var p = line.Split(','); double u, mu, mt;
                    if (p.Length < 3 || !double.TryParse(p[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out u)) continue;
                    busy = Math.Max(busy, u);
                    if (double.TryParse(p[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out mu)) used = Math.Max(0, used) + mu / 1024;
                    if (double.TryParse(p[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out mt)) total = Math.Max(0, total) + mt / 1024;
                }
            }
            row["gpu"] = busy >= 0 ? Math.Round(Math.Min(100, busy), 0) : -1;
            row["gpu_mem"] = used >= 0 ? Math.Round(used, 2) : -1;
            row["gpu_mem_total"] = total > 0 ? Math.Round(total, 1) : -1;
        }

        // texto para o grafico quando nao ha como medir o uso
        public static string Unavailable()
        {
            var l = Devs();
            if (l.Count == 0) return "Nenhuma placa de vídeo encontrada";
            if (l.Any(d => d.Vendor == "10de") && NvidiaSmi == null) return "Instale o driver da NVIDIA (nvidia-smi) para ver o uso";
            return "O driver desta GPU não informa o uso sem root";
        }
    }
}
