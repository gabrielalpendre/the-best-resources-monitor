// Partes comuns a Linux e macOS: reinicio com contagem regressiva, carimbo de inicio do processo, terminal.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace EvidenceMonitor
{
    class Noop : IDisposable { public void Dispose() { } }

    static class UnixCommon
    {
        static Timer rebootTimer;
        static readonly object rebootLock = new object();

        // O reinicio fica agendado no proprio monitor (30 s) e pode ser cancelado pelo menu, como no Windows.
        public static bool ScheduleReboot(Func<string> reboot, out string error)
        {
            error = null;
            lock (rebootLock)
            {
                if (rebootTimer != null) rebootTimer.Dispose();
                rebootTimer = new Timer(_ =>
                {
                    lock (rebootLock) { if (rebootTimer == null) return; rebootTimer.Dispose(); rebootTimer = null; }
                    string r = reboot();
                    ActionLog.Log("acao", "reinicio executado", new System.Collections.Generic.Dictionary<string, object> { { "resultado", r } });
                }, null, 30000, Timeout.Infinite);
            }
            return true;
        }

        public static bool CancelReboot()
        {
            lock (rebootLock)
            {
                if (rebootTimer == null) return false;
                rebootTimer.Dispose(); rebootTimer = null; return true;
            }
        }

        static readonly string Ps = Proc.Find("/bin/ps", "/usr/bin/ps");
        static readonly Regex SafeStamp = new Regex(@"^[A-Za-z0-9 :]{10,40}$");

        // Saida de "ps -o lstart=" (com LC_ALL=C). O comando de admin compara com a mesma saida antes de matar.
        public static string StartStamp(int pid)
        {
            if (Ps == null) return null;
            try
            {
                var psi = new ProcessStartInfo(Ps) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("lstart="); psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(pid.ToString());
                psi.Environment["LC_ALL"] = "C";
                using (var p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(5000);
                    s = Regex.Replace(s, @"\s+", " ");
                    return SafeStamp.IsMatch(s) ? s : null;
                }
            }
            catch { return null; }
        }

        public static bool Start(string exe, params string[] args)
        {
            if (exe == null) return false;
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using (Process.Start(psi)) { }
                return true;
            }
            catch { return false; }
        }

        public static string Read(string path) { try { return File.ReadAllText(path); } catch { return null; } }
        public static string ReadLine(string path) { var s = Read(path); return s == null ? null : s.Split('\n')[0].Trim(); }
    }
}
