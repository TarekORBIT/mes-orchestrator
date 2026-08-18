# MES Client Runtime Pack

Ce dossier contient:

- `bridge/`: bridge C# `MesHaiBridge` (stdin/stdout JSON) qui appelle `MES_HAI.dll`
- `orchestrator/`: service Node.js (API locale HTTP) qui orchestre le flux MES
- `install/`: script d'installation client (dossiers, service Windows, config)
- `config/`: template de configuration de l'orchestrateur

## 1) Compiler le bridge C#

Prérequis: .NET SDK installé sur la machine de build.

```powershell
cd production\bridge
dotnet publish .\MesHaiBridge.csproj -c Release -o .\publish
```

Sortie attendue: `production\bridge\publish\MesHaiBridge.exe`

## 2) Installer sur le PC client

Exécuter PowerShell en administrateur:

```powershell
cd production\install
.\install-client.ps1 -StationName "STATION_01" -BuildBridgeIfNeeded
```

Par défaut, l'installation déploie:

- `C:\ProgramData\MESApps\CIM\MES_HAI.xml`
- `C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe`
- `C:\MESApps\ClientGateway\bridge\MES_HAI.dll`
- `C:\MESApps\ClientGateway\orchestrator\mes-orchestrator.js`
- `C:\MESApps\ClientGateway\config\client-config.json`
- Service Windows `MESNodeOrchestrator`

## 3) Vérifier le service

```powershell
Invoke-RestMethod -Method GET -Uri http://127.0.0.1:7070/health
```

## 4) Endpoints principaux

- `POST /v1/login`
- `POST /v1/get-info`
- `POST /v1/move-in`
- `POST /v1/move-out-and-test`
- `POST /v1/bridge` (pass-through bridge)

## 5) Exemple rapide

```powershell
Invoke-RestMethod -Method POST -Uri http://127.0.0.1:7070/v1/get-info -ContentType "application/json" -Body (@{
  serialNumber = "SN123456"
} | ConvertTo-Json)
```

