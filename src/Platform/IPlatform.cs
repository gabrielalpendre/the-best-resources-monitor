// Contrato que cada sistema (Windows, Linux, macOS) implementa: sensores, processos, eventos e acoes.
// A interface grafica e a logica (historico, picos, relatorio) sao as mesmas nos tres.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EvidenceMonitor
{
    // Leitura crua de um processo; contadores acumulados (o ProcSnap calcula as taxas).
    class RawProc
    {
        public string Name; public int Pid, Parent;
        public long StartKey;          // identidade do processo (detecta PID reutilizado)
        public long CpuTime100ns;      // CPU acumulada (usuario + kernel) em unidades de 100 ns
        public long Faults;            // faltas de pagina que foram ao disco, acumuladas
        public long Priv, Ws;          // bytes: memoria privada / residente
    }

    // Categorias de evento (as chaves sao as mesmas em todos os sistemas; os rotulos mudam)
    static class Cat
    {
        public static readonly string[] All = { "mem", "dwm", "crash", "hang", "svc", "crit" };
    }

    interface IPlatform
    {
        string Os { get; }                 // "windows" | "linux" | "macos"
        string OsName { get; }             // "Windows" | "Linux" | "macOS"

        // Metricas do sistema na amostra: cpu, freq, freq_base, mem_avail, commit, pages, disk_busy, disk_mbs, disk_q,
        // commit_gb, limit_gb, ram_gb. Valor -1 = nao disponivel neste sistema.
        void ReadSystem(Dictionary<string, object> row);
        // Tempo ocioso e total de CPU acumulados (qualquer unidade), para o vigia de picos de 1 s
        bool CpuTimes(out long idle, out long total);
        List<RawProc> ReadProcs();
        // pid -> descricao do servico que o hospeda; agentRoots: pid -> agente corporativo cujo servico e raiz
        Dictionary<int, string> Services(out Dictionary<int, string> agentRoots);

        // Eventos de sistema (falta de memoria, quedas da interface grafica, crashes...) no formato
        // { t, prov, id, c, msg }. Watch chama onEvent em outra thread e devolve algo para parar.
        List<Dictionary<string, object>> ReadEvents(DateTime from);
        IDisposable WatchEvents(Action<Dictionary<string, object>> onEvent);
        string EventsHint { get; }         // aviso quando falta permissao para ler os eventos (ou null)

        Dictionary<string, string> CatLabels { get; }
        Regex Critical { get; }            // processos do sistema que o monitor nunca encerra
        Regex VirtualNic { get; }          // interfaces de rede virtuais ignoradas na soma

        // Informacoes para o cabecalho do relatorio
        string Model(); string CpuName(); string OsVersion(); DateTime? BootTime();

        // Instalacao e inicializacao com o login
        string InstallTarget { get; }      // onde o app fica instalado (.exe, binario ou .app)
        bool IsInstalled();
        void Install();
        void LaunchInstalled(string args);
        bool StartupEnabled();
        void SetStartup(bool on);
        bool IsElevated();

        // Acoes
        string Downloads();
        void OpenFolder(string dir);
        void Reveal(string file);          // abre a pasta com o arquivo selecionado
        void OpenFile(string file);
        bool RebootScheduled(out string error);   // reinicio forcado com 30 s para cancelar
        bool CancelReboot();
        string RebootWord { get; }         // "o Windows" | "o Mac" | "o computador"
        string KillServiceCommand(int pid); // comando de admin para reiniciar o servico do processo (ou null)
        string ProcessStartStamp(int pid);  // carimbo de inicio usado no comando de admin (ou null)
        bool OpenAdminTerminal(out string error);
        string AdminTerminalLabel { get; }
        bool HasWsl { get; }
        string WslShutdown();
        string RestartDocker(out string elevated);
    }

    static class Plat
    {
        public static readonly IPlatform I =
            OperatingSystem.IsWindows() ? new WindowsPlatform() :
            OperatingSystem.IsMacOS() ? (IPlatform)new MacPlatform() : new LinuxPlatform();
    }
}
