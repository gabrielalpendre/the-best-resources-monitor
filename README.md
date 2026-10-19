# Monitor de Evidências

App de bandeja que coleta evidências de lentidão e travamento no Windows, macOS e Linux, com a mesma interface nos três. Não precisa de admin.

## Instalar

Baixe o arquivo do seu sistema em [Releases](https://github.com/gabrielalpendre/the-best-resources-monitor/releases/latest).

- **Windows:** abra o `EvidenceMonitor.exe` (sem "Executar como administrador") e responda **Sim**.
- **macOS:** abra o `.dmg` (`arm64` = Apple Silicon, `x64` = Intel) e arraste o app para Aplicativos. O app não tem assinatura da Apple, então antes de abrir pela primeira vez rode:
  ```
  xattr -dr com.apple.quarantine /Applications/EvidenceMonitor.app
  ```
  Ele abre no login e fica na barra de menus.
- **Linux (Debian/Ubuntu):**
  ```
  sudo apt install ./evidence-monitor_*_amd64.deb
  ```
  Ele abre no login (ou pelo menu, "Monitor de Evidências"). No GNOME, o ícone precisa da extensão AppIndicator. Para ver os eventos do kernel, rode `sudo usermod -aG systemd-journal $USER` e entre de novo na sessão.

## O que faz

- Gráficos ao vivo de CPU, memória, disco, rede e do processo selecionado. Ao passar o mouse num gráfico, a mesma hora aparece marcada em todos.
- Lista de processos colorida por consumo, com encerrar processo e o comando pronto para admin quando falta permissão.
- Picos de CPU medidos a cada 1 s, eventos de crash, travamento e falta de memória, e relatório HTML de 24 h ou 7 dias.

## Publicar versão

```
git tag v2.0.1 && git push origin v2.0.1
```

O GitHub Actions compila os três sistemas e cria a release.

## Licença

Proprietária: © 2026 Gabriel Alpendre. Veja [LICENSE](LICENSE).
