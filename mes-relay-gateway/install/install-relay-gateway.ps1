[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$StationName,

  [string]$InstallRoot = "C:\MESApps\ClientGateway",
  [string]$ProgramDataCimPath = "C:\ProgramData\MESApps\CIM",
  [string]$RelaySerialNumber = "",
  [int]$RelayPassChannel = 1,
  [int]$RelayFailChannel = 2,
  [int]$RelayPulseMs = 500,
  [switch]$BuildIfNeeded,
  [switch]$WithGui
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
  Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-File([string]$PathToCheck, [string]$Label) {
  if (-not (Test-Path -LiteralPath $PathToCheck -PathType Leaf)) {
    throw "$Label introuvable: $PathToCheck"
  }
}

function Resolve-RelayGatewayBinaryPath {
  param(
    [string]$ProjectRootPath,
    [switch]$TryBuild
  )

  $csproj = Join-Path $ProjectRootPath "src\MesRelayGateway\MesRelayGateway.csproj"
  $publishDir = Join-Path $ProjectRootPath "publish"
  $exePath = Join-Path $publishDir "MesRelayGateway.exe"

  if (Test-Path -LiteralPath $exePath -PathType Leaf) {
    return $exePath
  }

  if (-not $TryBuild) {
    throw "MesRelayGateway.exe introuvable dans $publishDir. Activez -BuildIfNeeded ou publiez le projet avant (dotnet publish -r win-x64 --self-contained false -o publish)."
  }

  $dotnetSdks = & dotnet --list-sdks 2>$null
  if (-not $dotnetSdks) {
    throw "Aucun .NET SDK detecte. Installez un SDK pour compiler le projet."
  }

  Assert-File $csproj "Projet MesRelayGateway"
  Write-Step "Compilation MesRelayGateway (dotnet publish)"
  & dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $publishDir | Out-Host

  if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Compilation terminee mais MesRelayGateway.exe absent: $exePath"
  }
  return $exePath
}

function Resolve-RelayGatewayGuiBinaryPath {
  param(
    [string]$ProjectRootPath,
    [switch]$TryBuild
  )

  $csproj = Join-Path $ProjectRootPath "src\MesRelayGateway.Gui\MesRelayGateway.Gui.csproj"
  $publishDir = Join-Path $ProjectRootPath "publish-gui"
  $exePath = Join-Path $publishDir "MesRelayGatewayGui.exe"

  if (Test-Path -LiteralPath $exePath -PathType Leaf) {
    return $exePath
  }

  if (-not $TryBuild) {
    throw "MesRelayGatewayGui.exe introuvable dans $publishDir. Activez -BuildIfNeeded ou publiez le projet avant (dotnet publish -r win-x64 --self-contained false -o publish-gui)."
  }

  Assert-File $csproj "Projet MesRelayGateway.Gui"
  Write-Step "Compilation MesRelayGateway.Gui (dotnet publish)"
  & dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $publishDir | Out-Host

  if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Compilation terminee mais MesRelayGatewayGui.exe absent: $exePath"
  }
  return $exePath
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$embarqueRoot = (Resolve-Path (Join-Path $projectRoot "..")).Path

$configTemplateSource = Join-Path $projectRoot "config\client-config.template.json"
$relayConfigExampleSource = Join-Path $projectRoot "config\relay-config.example.json"
$mesXmlSource = Join-Path $embarqueRoot "MES_HAI.xml"
$mesDllSource = Join-Path $embarqueRoot "MES_HAI.dll"

Assert-File $configTemplateSource "Template config"
Assert-File $mesXmlSource "MES_HAI.xml"
Assert-File $mesDllSource "MES_HAI.dll"

$exeSource = Resolve-RelayGatewayBinaryPath -ProjectRootPath $projectRoot -TryBuild:$BuildIfNeeded
$publishDir = Split-Path -Parent $exeSource

# Meme structure cible que production/install/install-client.ps1 :
#   C:\MESApps\ClientGateway\bridge\MES_HAI.dll
#   C:\ProgramData\MESApps\CIM\MES_HAI.xml
# + un dossier propre a cet outil pour l'exe et sa config.
$bridgeDir = Join-Path $InstallRoot "bridge"
$relayGatewayDir = Join-Path $InstallRoot "relay-gateway"
$relayConfigDir = Join-Path $relayGatewayDir "config"
$logDir = Join-Path $InstallRoot "logs"

Write-Step "Creation des dossiers d'installation"
New-Item -ItemType Directory -Force -Path $InstallRoot, $bridgeDir, $relayGatewayDir, $relayConfigDir, $logDir, $ProgramDataCimPath | Out-Null

Write-Step "Copie de MesRelayGateway.exe + dependances"
Copy-Item -Path (Join-Path $publishDir "*") -Destination $relayGatewayDir -Force

if ($WithGui) {
  $guiExeSource = Resolve-RelayGatewayGuiBinaryPath -ProjectRootPath $projectRoot -TryBuild:$BuildIfNeeded
  $guiPublishDir = Split-Path -Parent $guiExeSource
  Write-Step "Copie de MesRelayGatewayGui.exe + dependances"
  Copy-Item -Path (Join-Path $guiPublishDir "*") -Destination $relayGatewayDir -Force
}

Write-Step "Copie de MES_HAI.dll (partagee avec le bridge C#)"
Copy-Item -LiteralPath $mesDllSource -Destination (Join-Path $bridgeDir "MES_HAI.dll") -Force

Write-Step "Copie de la configuration MES XML"
Copy-Item -LiteralPath $mesXmlSource -Destination (Join-Path $ProgramDataCimPath "MES_HAI.xml") -Force

Write-Step "Generation de client-config.json"
$clientConfigPath = Join-Path $relayConfigDir "client-config.json"
$clientConfig = @{
  stationName     = $StationName
  haiXmlPath      = Join-Path $ProgramDataCimPath "MES_HAI.xml"
  haiDllPath      = Join-Path $bridgeDir "MES_HAI.dll"
  haiInstanceName = "MES_HAI"
  relayConfigPath = Join-Path $relayConfigDir "relay-config.json"
  logDir          = $logDir
  logFileName     = "mes-relay-gateway.log"
} | ConvertTo-Json -Depth 6
$clientConfig | Set-Content -LiteralPath $clientConfigPath -Encoding UTF8

Write-Step "Generation de relay-config.json"
$relayConfigPath = Join-Path $relayConfigDir "relay-config.json"
$relayConfig = @{
  relaySerialNumber = if ($RelaySerialNumber) { $RelaySerialNumber } else { $null }
  passChannel       = $RelayPassChannel
  failChannel       = $RelayFailChannel
  pulseMs           = $RelayPulseMs
} | ConvertTo-Json -Depth 6
$relayConfig | Set-Content -LiteralPath $relayConfigPath -Encoding UTF8

Write-Step "Suppression du blocage de securite sur les fichiers copies"
Get-ChildItem -LiteralPath $InstallRoot -Recurse -File | ForEach-Object {
  try { Unblock-File -LiteralPath $_.FullName -ErrorAction Stop } catch { }
}
Get-ChildItem -LiteralPath $ProgramDataCimPath -File -Filter "MES_HAI.xml" | ForEach-Object {
  try { Unblock-File -LiteralPath $_.FullName -ErrorAction Stop } catch { }
}

$usbRelayDll = Join-Path $relayGatewayDir "usb_relay_device.dll"
$usbRelayPresent = Test-Path -LiteralPath $usbRelayDll -PathType Leaf

Write-Host ""
Write-Host "Installation terminee." -ForegroundColor Green
Write-Host "InstallRoot          : $InstallRoot"
Write-Host "MES XML              : $(Join-Path $ProgramDataCimPath 'MES_HAI.xml')"
Write-Host "MES DLL              : $(Join-Path $bridgeDir 'MES_HAI.dll')"
Write-Host "MesRelayGateway.exe  : $(Join-Path $relayGatewayDir 'MesRelayGateway.exe')"
if ($WithGui) {
  Write-Host "MesRelayGatewayGui.exe : $(Join-Path $relayGatewayDir 'MesRelayGatewayGui.exe')"
}
Write-Host "Config               : $clientConfigPath"
Write-Host "Relay config         : $relayConfigPath"
Write-Host ""

if (-not $usbRelayPresent) {
  Write-Host "ATTENTION: usb_relay_device.dll est absent de $relayGatewayDir" -ForegroundColor Yellow
  Write-Host "  Ce fichier natif (https://github.com/pavel-a/usb-relay-hid) n'est pas fourni par ce depot." -ForegroundColor Yellow
  Write-Host "  Copiez-y la DLL (meme architecture que le relais, x86 ou x64) avant le premier test relais." -ForegroundColor Yellow
  Write-Host ""
}

Write-Host "Test rapide (sans relais si usb_relay_device.dll absent) :"
Write-Host "  & `"$relayGatewayDir\MesRelayGateway.exe`" --config `"$clientConfigPath`" --action login"
