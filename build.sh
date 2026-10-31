#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
TARGETS=${*:-"win-x64 linux-x64 osx-arm64 osx-x64"}
VER=${VERSION:-2.0.0}
mkdir -p dist
EXTRA=(); [ -n "${VERSION:-}" ] && EXTRA=("-p:Version=$VERSION")

# arvore comum de arquivos do Linux (usada pelo .deb e pelo .rpm)
linux_tree() {
  local rid=$1 root=$2
  rm -rf "$root"; mkdir -p "$root/opt/evidence-monitor" "$root/usr/bin" "$root/usr/share/applications" "$root/etc/xdg/autostart"
  install -m 755 "out/$rid/EvidenceMonitor" "$root/opt/evidence-monitor/evidence-monitor"
  ln -s /opt/evidence-monitor/evidence-monitor "$root/usr/bin/evidence-monitor"
  local desktop="[Desktop Entry]
Type=Application
Name=Monitor de Evidências
Comment=Coleta evidências de lentidão e travamento
Exec=/opt/evidence-monitor/evidence-monitor
Terminal=false
Categories=System;Monitor;"
  echo "$desktop" > "$root/usr/share/applications/evidence-monitor.desktop"
  printf '%s\nX-GNOME-Autostart-enabled=true\n' "$desktop" > "$root/etc/xdg/autostart/evidence-monitor.desktop"
  find "$root" -type d -exec chmod 755 {} +
  find "$root" -type f ! -path "*/opt/*" -exec chmod 644 {} +
}

deb() {
  local rid=$1 arch=amd64 root="out/deb-$1"
  [ "$rid" = "linux-arm64" ] && arch=arm64
  linux_tree "$rid" "$root"
  mkdir -p "$root/DEBIAN"
  cat > "$root/DEBIAN/control" <<EOF
Package: evidence-monitor
Version: $VER
Section: utils
Priority: optional
Architecture: $arch
Maintainer: Gabriel Alpendre
Depends: libc6, libx11-6, libice6, libsm6, libfontconfig1
Installed-Size: $(du -sk "$root/opt" | cut -f1)
Description: Monitor de Evidências
 App de bandeja que coleta evidências de lentidão e travamento.
EOF
  chmod 755 "$root/DEBIAN"; chmod 644 "$root/DEBIAN/control"
  dpkg-deb --root-owner-group --build "$root" "dist/evidence-monitor_${VER}_${arch}.deb"
}

# Fedora/RHEL: empacota a mesma arvore com rpmbuild (o binario ja vem pronto, nada e recompilado)
rpm() {
  local rid=$1 arch=x86_64 top tree
  [ "$rid" = "linux-arm64" ] && arch=aarch64
  top="$PWD/out/rpm-$rid"; tree="$top/tree"
  rm -rf "$top"; mkdir -p "$top/SPECS"
  linux_tree "$rid" "$tree"
  local rver=${VER//-/\~}   # rpm nao aceita '-' na versao (0.0.0-dev -> 0.0.0~dev)
  cat > "$top/SPECS/evidence-monitor.spec" <<EOF
Name:           evidence-monitor
Version:        $rver
Release:        1
Summary:        Monitor de Evidências
License:        Proprietary
BuildArch:      $arch
AutoReqProv:    no
Requires:       glibc, libX11, libICE, libSM, fontconfig, libicu
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

%description
App de bandeja que coleta evidências de lentidão e travamento.

%install
cp -a "$tree/." %{buildroot}/

%files
/opt/evidence-monitor
/usr/bin/evidence-monitor
/usr/share/applications/evidence-monitor.desktop
%config(noreplace) /etc/xdg/autostart/evidence-monitor.desktop
EOF
  rpmbuild -bb --quiet --define "_topdir $top" --target "$arch" "$top/SPECS/evidence-monitor.spec"
  cp "$top"/RPMS/"$arch"/*.rpm dist/
}

dmg() {
  local arch=$1 app=$2 stage="out/dmg-$1"
  rm -rf "$stage"; mkdir -p "$stage"
  cp -R "$app" "$stage/"
  ln -s /Applications "$stage/Applications"
  rm -f "dist/EvidenceMonitor-macos-$arch.dmg"
  hdiutil create -volname "Monitor de Evidências" -srcfolder "$stage" -ov -format UDZO "dist/EvidenceMonitor-macos-$arch.dmg"
}

for rid in $TARGETS; do
  echo "== $rid"
  rm -rf "out/$rid"
  dotnet publish src/EvidenceMonitor.csproj -c Release -r "$rid" -o "out/$rid" -v q ${EXTRA[@]+"${EXTRA[@]}"}
  case "$rid" in
    win-*) cp "out/$rid/EvidenceMonitor.exe" dist/EvidenceMonitor.exe ;;
    linux-*)
      built=0
      if command -v dpkg-deb >/dev/null; then deb "$rid"; built=1; fi
      if command -v rpmbuild >/dev/null; then rpm "$rid"; built=1; fi
      if [ $built = 0 ]; then cp "out/$rid/EvidenceMonitor" "dist/EvidenceMonitor-$rid"; chmod +x "dist/EvidenceMonitor-$rid"; fi
      ;;
    osx-*)
      arch=${rid#osx-}; stage="out/mac-$arch"; app="$stage/EvidenceMonitor.app"
      rm -rf "$stage"; mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      cp -R "out/$rid/." "$app/Contents/MacOS/"
      sed "s|<string>2.0.0</string>|<string>$VER</string>|g" packaging/Info.plist > "$app/Contents/Info.plist"
      chmod +x "$app/Contents/MacOS/EvidenceMonitor"
      if command -v hdiutil >/dev/null; then
        codesign --force --deep --sign - "$app"
        dmg "$arch" "$app"
      else
        cp packaging/install-mac.sh "$stage/"; chmod +x "$stage/install-mac.sh"
        rm -f "dist/EvidenceMonitor-macos-$arch.zip"
        (cd "$stage" && zip -qry "../../dist/EvidenceMonitor-macos-$arch.zip" .)
      fi
      ;;
  esac
done
ls -lh dist
