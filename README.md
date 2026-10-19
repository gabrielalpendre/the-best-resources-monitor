# Monitor de Evidências

App de bandeja que coleta evidências de lentidão e travamento no Windows, macOS e Linux, com a mesma interface nos três. Não precisa de admin.

## Instalar

Baixe o arquivo do seu sistema em [Releases](https://github.com/gabrielalpendre/the-best-resources-monitor/releases/latest).

- **Windows:** abra o `EvidenceMonitor.exe` e responda **Sim**.
- **macOS:** descompacte o `.zip` e rode `bash install-mac.sh`.
- **Linux:** `chmod +x EvidenceMonitor-linux-x64 && ./EvidenceMonitor-linux-x64`

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
