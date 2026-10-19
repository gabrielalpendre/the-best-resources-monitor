#!/bin/bash
set -euo pipefail
if [ "$(id -u)" = "0" ]; then echo "Rode sem sudo."; exit 1; fi
cd "$(dirname "$0")"
APP="EvidenceMonitor.app"
[ -d "$APP" ] || { echo "Nao achei $APP ao lado deste script."; exit 1; }

DEST="$HOME/Applications/$APP"
mkdir -p "$HOME/Applications"
rm -rf "$DEST"
cp -R "$APP" "$DEST"
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
chmod +x "$DEST/Contents/MacOS/EvidenceMonitor"
codesign --force --deep --sign - "$DEST"
"$DEST/Contents/MacOS/EvidenceMonitor" --install
echo "Instalado em $DEST. O icone aparece na barra de menus."
