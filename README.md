# Monitor de Evidências

App de bandeja que coleta evidências de lentidão e travamento no **Windows, macOS e Linux**.
A interface e os sensores são os mesmos nos três: um executável autocontido por sistema, sem dependências e sem admin.

## Instalar

| Sistema | Arquivo em `dist/` | Como |
|---|---|---|
| Windows 10/11 x64 | `EvidenceMonitor.exe` | Duplo-clique, **sem** "Executar como administrador", e responda **Sim**. Instala em `%LOCALAPPDATA%\Programs\EvidenceMonitor` e inicia com o login (`HKCU\...\Run`). |
| macOS 12+ (Apple Silicon) | `EvidenceMonitor-macos-arm64.zip` | Descompacte e rode `bash install-mac.sh` no Terminal (sem sudo). Instala em `~/Applications` e inicia com o login (LaunchAgent). |
| macOS 12+ (Intel) | `EvidenceMonitor-macos-x64.zip` | Igual ao anterior. |
| Linux x64 | `EvidenceMonitor-linux-x64` | `chmod +x EvidenceMonitor-linux-x64 && ./EvidenceMonitor-linux-x64` e responda **Sim**. Instala em `~/.local/share/EvidenceMonitor/app`, cria o atalho no menu e o autostart do XDG. |

O ícone fica na bandeja (Windows/Linux) ou na barra de menus (macOS):
verde = normal, amarelo = memória apertando, vermelho = quase esgotada.

- **macOS:** o app não tem assinatura da Apple. O `install-mac.sh` tira a quarentena e assina localmente (ad-hoc), o que o macOS exige para rodar em Apple Silicon.
- **Linux:** o ícone usa o padrão StatusNotifier, que funciona no KDE, no XFCE e no Ubuntu (GNOME com AppIndicator). Em GNOME puro, instale a extensão "AppIndicator" ou abra com `--panel`.
  Para ver os eventos do kernel, o usuário precisa estar no grupo `systemd-journal` ou `adm`. Sem isso, o painel avisa e mostra o comando.

## Painel (clique no ícone)

- **Gráficos ao vivo:** CPU, clock efetivo, RAM livre, memória comprometida, paginação, disco e rede, mais memória e CPU do processo selecionado.
  - Períodos de 15 min, 1 h, 6 h e 24 h.
  - **Cursor sincronizado:** ao passar o mouse num gráfico, a mesma hora fica marcada em **todos** os gráficos, cada um com o seu valor naquele instante. O mesmo vale no relatório HTML.
  - As linhas vermelhas tracejadas marcam travamento da tela, memória esgotada ou evento crítico.
- **Lista de processos colorida por consumo:**
  - vermelho: ≥ 25% da RAM ou ≥ 50% da CPU;
  - amarelo: ≥ 10% da RAM ou ≥ 25% da CPU.

  Mostra também a origem (serviço, processo pai ou agente). Clique no cabeçalho para ordenar. "Agrupar por aplicativo" soma as instâncias.
- **Agentes corporativos** (Teramind, Microsoft Defender, CrowdStrike, Fortinet, Wazuh) aparecem em roxo nos três sistemas, e o monitor nunca os encerra.
- **Aba Picos de CPU:** um vigia confere a CPU total a cada 1 s e, acima de 70%, registra o pico segundo a segundo com os processos responsáveis.
- **Aba Eventos:** falta de memória, travamentos da tela, crashes, apps sem resposta, serviços que caíram e eventos críticos dos últimos 7 dias, com o snapshot dos processos.
- **Encerrar processo:** pede confirmação e confere nome e horário de início antes de matar.
  Se faltar permissão, abre uma janela com o comando pronto para um terminal de administrador (PowerShell no Windows, `sudo` no Mac/Linux), que também confere o horário de início.
- **Reiniciar (último recurso):** salva o relatório de 24 h em Downloads e reinicia à força em 30 s, sem precisar de admin. Pode ser cancelado pelo menu do ícone.
- **Relatórios 24 h / 7 dias:** são salvos em Downloads.

## Sensores por sistema

| Sensor | Windows | Linux | macOS |
|---|---|---|---|
| CPU | PDH | `/proc/stat` | `host_statistics` |
| Clock efetivo | PDH (% de desempenho) | `scaling_cur_freq` / `cpuinfo` | **não disponível** (exige root) |
| RAM livre | PDH | `MemAvailable` | free + inactive + speculative |
| Memória comprometida | commit / limite (RAM + pagefile) | RAM usada + swap usado / RAM + swap | igual ao Linux |
| Paginação | Pages/sec | faltas maiores + swap in/out | pageins/pageouts + swap |
| Disco | % ocupado, MB/s, fila | `/proc/diskstats` (discos físicos) | MB/s somado dos processos (sem "% ocupado") |
| Rede | interfaces físicas (sem vEthernet) | sem docker/veth/VPN | sem utun/awdl/bridge |
| Processos | `NtQuerySystemInformation` | `/proc/<pid>` | `libproc` (footprint, como o Monitor de Atividade) |
| Serviços | WMI | unidade systemd (cgroup) | `launchctl list` |
| Eventos | Visualizador de Eventos | journald: OOM, coredump, GPU, tarefa bloqueada, unidade falhou, MCE/térmico | DiagnosticReports: crash, hang, jetsam, panic, mais `pmset` térmico |

Os dados ficam em `%LOCALAPPDATA%\EvidenceMonitor` (Windows), `~/.local/share/EvidenceMonitor` (Linux) ou `~/Library/Application Support/EvidenceMonitor` (macOS): um `.jsonl` por dia, com 30 dias de retenção.
O formato é o mesmo da versão 1, então o histórico continua.

## Segurança

- **Nunca roda como administrador/root:** se for aberto elevado, ele recusa e fecha. No Windows o manifesto usa `asInvoker`.
- **Executáveis do sistema por caminho absoluto,** nunca pelo `PATH`. No Windows, a busca de DLL fica restrita ao System32.
- **Encerrar processo:** confere nome e horário de início, porque o PID pode ter sido reutilizado. O comando para admin só contém números e nomes validados, nunca o nome do processo.
- **Superfície mínima:** sem porta de rede, sem IPC e sem serviço. Não coleta linha de comando dos processos.
- **Relatório:** tem uma Content-Security-Policy que bloqueia qualquer acesso à rede, e os textos são escapados.
- **Executáveis sem assinatura digital** (Windows/macOS): o SmartScreen, o Gatekeeper ou o EDR podem perguntar na primeira execução.
- **Windows:** as bibliotecas nativas da interface (Skia) são extraídas na primeira execução para `%TEMP%\.net`.

## Linha de comando

- `--report 168`: gera o relatório de 7 dias em Downloads. Com `--quiet`, roda sem interface e imprime o caminho.
- `--panel`: abre já com o painel.
- `--uninstall`: remove da inicialização e mantém os dados.
- `--selftest [arquivo.json]`: coleta 2 amostras sem interface, para conferir os sensores.

## Limitações

- **Temperatura da CPU:** não é coletada em nenhum sistema, porque exige driver de kernel ou root. Os eventos de limitação térmica aparecem como indício.
- **macOS:**
  - não há clock efetivo nem "% disco ocupado";
  - "Desligar WSL" só existe no Windows.
- **Testes:** Windows e Linux (Ubuntu no WSL) foram testados. O macOS foi compilado, mas ainda não rodou num Mac de verdade.

## Licença

Software proprietário: © 2026 Gabriel Alpendre, todos os direitos reservados. O uso é permitido; copiar, modificar ou redistribuir exige autorização. Os termos completos estão em [LICENSE](LICENSE).

## Compilar

Requer o .NET 10 SDK; dá para instalar sem admin com `dotnet-install.ps1 -Channel 10.0`.

- **Windows:** `powershell -File build.ps1`
- **Mac/Linux:** `./build.sh`. No Mac, o `.app` já sai assinado.

O código fica em `src/`:

- `Core.cs`, `History.cs`: lógica comum aos três sistemas;
- `Platform/`: uma implementação por sistema;
- `Ui/`: interface Avalonia, desenhada à mão e com a fonte Inter embutida para ficar idêntica nos três.
