# Compila o Monitor de Evidencias para Windows, macOS (Apple Silicon e Intel) e Linux.
# Requer o .NET 10 SDK (instalacao sem admin: https://dot.net/v1/dotnet-install.ps1 -Channel 10.0).
# Saida em dist\:
#   EvidenceMonitor.exe                     Windows x64
#   EvidenceMonitor-linux-x64               Linux x64 (dar chmod +x)
#   EvidenceMonitor-macos-arm64.zip         macOS Apple Silicon (.app + install-mac.sh)
#   EvidenceMonitor-macos-x64.zip           macOS Intel
param([string[]]$Targets = @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet }
if (-not $dotnet) { throw ".NET SDK nao encontrado" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$dist = "$root\dist"; $obj = "$root\out"
# versao vinda da tag (CI) ou a do .csproj
$extra = @(); if ($env:VERSION) { $extra += "-p:Version=$env:VERSION" }
New-Item -ItemType Directory -Force $dist | Out-Null
foreach ($rid in $Targets) {
  Write-Host "== $rid"
  $out = "$obj\$rid"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  & $dotnet publish "$root\src\EvidenceMonitor.csproj" -c Release -r $rid -o $out -v q @extra
  if ($LASTEXITCODE -ne 0) { throw "falha na compilacao ($rid)" }

  if ($rid -eq 'win-x64') { Copy-Item "$out\EvidenceMonitor.exe" "$dist\EvidenceMonitor.exe" -Force }
  elseif ($rid -like 'linux-*') { Copy-Item "$out\EvidenceMonitor" "$dist\EvidenceMonitor-$rid" -Force }
  else {
    # macOS: monta o EvidenceMonitor.app (binario + bibliotecas nativas + Info.plist) e zipa com o instalador
    $arch = $rid.Split('-')[1]
    $stage = "$obj\mac-$arch"; $app = "$stage\EvidenceMonitor.app"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force "$app\Contents\MacOS", "$app\Contents\Resources" | Out-Null
    Copy-Item "$out\*" "$app\Contents\MacOS\" -Recurse
    $plist = Get-Content "$root\packaging\Info.plist" -Raw
    if ($env:VERSION) { $plist = $plist.Replace('<string>2.0.0</string>', "<string>$env:VERSION</string>") }
    [IO.File]::WriteAllText("$app\Contents\Info.plist", $plist, (New-Object Text.UTF8Encoding $false))
    # scripts com fim de linha Unix
    $sh = (Get-Content "$root\packaging\install-mac.sh" -Raw) -replace "`r`n", "`n"
    [IO.File]::WriteAllText("$stage\install-mac.sh", $sh, (New-Object Text.UTF8Encoding $false))
    $zip = "$dist\EvidenceMonitor-macos-$arch.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$stage\*" -DestinationPath $zip
  }
}
Get-ChildItem $dist | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
