#!/bin/bash
# Instala o Monitor de Evidencias no macOS, so para o seu usuario (sem sudo).
# Uso: na pasta onde esta o EvidenceMonitor.app, rode:  bash install-mac.sh
set -euo pipefail
if [ "$(id -u)" = "0" ]; then echo "Rode sem sudo: o monitor nao roda como root."; exit 1; fi
cd "$(dirname "$0")"
APP="EvidenceMonitor.app"
[ -d "$APP" ] || { echo "Nao achei $APP ao lado deste script."; exit 1; }

DEST="$HOME/Applications/$APP"
mkdir -p "$HOME/Applications"
rm -rf "$DEST"
cp -R "$APP" "$DEST"

# O app nao tem assinatura da Apple: tira a quarentena do download e assina localmente (ad-hoc),
# o que o macOS exige para rodar binarios Apple Silicon.
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
chmod +x "$DEST/Contents/MacOS/EvidenceMonitor"
codesign --force --deep --sign - "$DEST"

# grava o LaunchAgent (abrir no login) e abre o monitor
"$DEST/Contents/MacOS/EvidenceMonitor" --install
echo "Instalado em $DEST. O icone aparece na barra de menus (canto superior direito)."
