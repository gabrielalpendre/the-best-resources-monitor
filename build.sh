#!/bin/bash
# Compila no macOS ou Linux (requer .NET 10 SDK). Mesma saida do build.ps1, em dist/.
# No macOS o .app ja sai assinado ad-hoc (codesign), pronto para rodar.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
TARGETS=${*:-"win-x64 linux-x64 osx-arm64 osx-x64"}
mkdir -p dist
for rid in $TARGETS; do
  echo "== $rid"
  rm -rf "out/$rid"
  dotnet publish src/EvidenceMonitor.csproj -c Release -r "$rid" -o "out/$rid" -v q
  case "$rid" in
    win-*) cp "out/$rid/EvidenceMonitor.exe" dist/EvidenceMonitor.exe ;;
    linux-*) cp "out/$rid/EvidenceMonitor" "dist/EvidenceMonitor-$rid"; chmod +x "dist/EvidenceMonitor-$rid" ;;
    osx-*)
      arch=${rid#osx-}; stage="out/mac-$arch"; app="$stage/EvidenceMonitor.app"
      rm -rf "$stage"; mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      cp -R "out/$rid/." "$app/Contents/MacOS/"
      cp packaging/Info.plist "$app/Contents/Info.plist"
      cp packaging/install-mac.sh "$stage/"; chmod +x "$stage/install-mac.sh" "$app/Contents/MacOS/EvidenceMonitor"
      if command -v codesign >/dev/null; then codesign --force --deep --sign - "$app"; fi
      rm -f "dist/EvidenceMonitor-macos-$arch.zip"
      (cd "$stage" && zip -qry "../../dist/EvidenceMonitor-macos-$arch.zip" .)
      ;;
  esac
done
ls -lh dist
