[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$StationName,

  [string]$InstallRoot = "C:\MESApps\ClientGateway",
  [string]$ProgramDataCimPath = "C:\ProgramData\MESApps\CIM",
  [string]$ServiceName = "MESNodeOrchestrator",
  [string]$ServiceDisplayName = "MES Node Orchestrator",
  [string]$BindHost = "127.0.0.1",
  [int]$Port = 7070,
  [int]$BridgeTimeoutMs = 20000,
  [switch]$BuildBridgeIfNeeded,
  [switch]$NoService
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

function Resolve-RequiredNodePath {
  $cmd = Get-Command node -ErrorAction SilentlyContinue
  if (-not $cmd) {
    throw "Node.js n'est pas installe (commande 'node' introuvable)."
  }
  return $cmd.Source
}

function Resolve-BridgeBinaryPath {
  param(
    [string]$ProductionRootPath,
    [switch]$TryBuild
  )

  $publishDir = Join-Path $ProductionRootPath "bridge\publish"
  $bridgeExe = Join-Path $publishDir "MesHaiBridge.exe"
  if (Test-Path -LiteralPath $bridgeExe -PathType Leaf) {
    return $bridgeExe
  }

  if (-not $TryBuild) {
    throw "MesHaiBridge.exe introuvable dans $publishDir. Activez -BuildBridgeIfNeeded ou publiez le bridge avant."
  }

  $dotnetSdks = & dotnet --list-sdks 2>$null
  if (-not $dotnetSdks) {
    throw "Aucun .NET SDK detecte. Installez un SDK pour compiler le bridge."
  }

  $csproj = Join-Path $ProductionRootPath "bridge\MesHaiBridge.csproj"
  Assert-File $csproj "Projet C# bridge"
  Write-Step "Compilation bridge C# (dotnet publish)"
  & dotnet publish $csproj -c Release -o $publishDir | Out-Host

  if (-not (Test-Path -LiteralPath $bridgeExe -PathType Leaf)) {
    throw "Compilation terminee mais MesHaiBridge.exe absent: $bridgeExe"
  }
  return $bridgeExe
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productionRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$projectRoot = (Resolve-Path (Join-Path $productionRoot "..")).Path

$orchestratorSource = Join-Path $productionRoot "orchestrator\mes-orchestrator.js"
$configTemplateSource = Join-Path $productionRoot "config\client-config.template.json"
$mesXmlSource = Join-Path $projectRoot "MES_HAI.xml"
$mesDllSource = Join-Path $projectRoot "MES_HAI.dll"

Assert-File $orchestratorSource "Orchestrateur Node"
Assert-File $configTemplateSource "Template config"
Assert-File $mesXmlSource "MES_HAI.xml"
Assert-File $mesDllSource "MES_HAI.dll"

$bridgeExeSource = Resolve-BridgeBinaryPath -ProductionRootPath $productionRoot -TryBuild:$BuildBridgeIfNeeded
$bridgePublishDir = Split-Path -Parent $bridgeExeSource

$bridgeDir = Join-Path $InstallRoot "bridge"
$orchestratorDir = Join-Path $InstallRoot "orchestrator"
$configDir = Join-Path $InstallRoot "config"
$logDir = Join-Path $InstallRoot "logs"

Write-Step "Creation des dossiers d'installation"
New-Item -ItemType Directory -Force -Path $InstallRoot, $bridgeDir, $orchestratorDir, $configDir, $logDir, $ProgramDataCimPath | Out-Null

Write-Step "Copie du bridge + dependances"
Copy-Item -Path (Join-Path $bridgePublishDir "*") -Destination $bridgeDir -Force
Copy-Item -LiteralPath $mesDllSource -Destination (Join-Path $bridgeDir "MES_HAI.dll") -Force

Write-Step "Copie de l'orchestrateur Node"
Copy-Item -LiteralPath $orchestratorSource -Destination (Join-Path $orchestratorDir "mes-orchestrator.js") -Force

Write-Step "Copie de la configuration MES XML"
Copy-Item -LiteralPath $mesXmlSource -Destination (Join-Path $ProgramDataCimPath "MES_HAI.xml") -Force

Write-Step "Generation du station.ini"
$stationIniPath = Join-Path $configDir "station.ini"
@(
  "[MES]"
  "StationName=$StationName"
  "TimeoutMs=1200"
  "RetryCount=1"
) | Set-Content -LiteralPath $stationIniPath -Encoding UTF8

Write-Step "Generation de client-config.json"
$clientConfigPath = Join-Path $configDir "client-config.json"
$clientConfig = @{
  stationName = $StationName
  bridgeExePath = (Join-Path $bridgeDir "MesHaiBridge.exe")
  haiDllPath = (Join-Path $bridgeDir "MES_HAI.dll")
  haiInstanceName = "MES_HAI"
  host = $BindHost
  port = $Port
  bridgeTimeoutMs = $BridgeTimeoutMs
  defaultLayer = 0
  defaultActivateWorkOrder = $true
  defaultCheckMultiBoard = $false
  logDir = $logDir
  logFileName = "mes-orchestrator.log"
} | ConvertTo-Json -Depth 6
$clientConfig | Set-Content -LiteralPath $clientConfigPath -Encoding UTF8

Write-Step "Suppression du blocage de securite sur les fichiers copies"
Get-ChildItem -LiteralPath $InstallRoot -Recurse -File | ForEach-Object {
  try { Unblock-File -LiteralPath $_.FullName -ErrorAction Stop } catch { }
}
Get-ChildItem -LiteralPath $ProgramDataCimPath -File -Filter "MES_HAI.xml" | ForEach-Object {
  try { Unblock-File -LiteralPath $_.FullName -ErrorAction Stop } catch { }
}

if (-not $NoService) {
  Write-Step "Configuration du service Windows"
  $nodeExe = Resolve-RequiredNodePath
  $orchestratorMain = Join-Path $orchestratorDir "mes-orchestrator.js"
  $binaryPath = "`"$nodeExe`" `"$orchestratorMain`" --config `"$clientConfigPath`""

  $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
  if ($existing) {
    Write-Step "Service existant detecte, suppression"
    if ($existing.Status -ne "Stopped") {
      Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
      Start-Sleep -Seconds 1
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
  }

  & sc.exe create $ServiceName binPath= $binaryPath start= auto DisplayName= $ServiceDisplayName | Out-Null
  & sc.exe description $ServiceName "MES orchestrator local (Node.js + C# bridge)." | Out-Null
  & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
  Start-Service -Name $ServiceName
}

Write-Host ""
Write-Host "Installation terminee." -ForegroundColor Green
Write-Host "InstallRoot       : $InstallRoot"
Write-Host "MES XML           : $(Join-Path $ProgramDataCimPath 'MES_HAI.xml')"
Write-Host "Bridge EXE        : $(Join-Path $bridgeDir 'MesHaiBridge.exe')"
Write-Host "Bridge DLL        : $(Join-Path $bridgeDir 'MES_HAI.dll')"
Write-Host "Orchestrator      : $(Join-Path $orchestratorDir 'mes-orchestrator.js')"
Write-Host "Config            : $clientConfigPath"
Write-Host "Station INI       : $stationIniPath"
Write-Host "Health endpoint   : http://$BindHost`:$Port/health"
if (-not $NoService) {
  Write-Host "Service           : $ServiceName (AutoStart)"
}
Write-Host ""
Write-Host "Test rapide:"
Write-Host "  Invoke-RestMethod -Method GET -Uri http://$BindHost`:$Port/health"
