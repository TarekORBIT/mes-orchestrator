# MES Relay Gateway

Outil .NET (C#) autonome, compilé en `.exe` Windows, qui:

1. Lit sa configuration (`MES_HAI.xml`, `MES_HAI.dll`, nom de station, mapping relais) depuis des
   **chemins de fichiers fournis en entrée** — via `--config client-config.json` et/ou des
   `--flag` en ligne de commande. Rien n'est codé en dur au-delà des valeurs par défaut, qui
   reprennent les chemins de déploiement déjà utilisés par [`production/`](../production):
   - `C:\ProgramData\MESApps\CIM\MES_HAI.xml`
   - `C:\MESApps\ClientGateway\bridge\MES_HAI.dll`
2. Se connecte au MES (login, get-info, move-in, move-out-and-test) en chargeant `MES_HAI.dll`
   par réflexion — même principe que [`production/bridge/Program.cs`](../production/bridge/Program.cs).
3. **Détecte l'erreur** retournée par le MES (`ErrorCode` / `ErrorDescription`) et la classe
   (session expirée, station invalide, panne réseau, erreur métier...) — portage direct de
   `classifyErrorDetail` dans [`production/orchestrator/mes-orchestrator.js`](../production/orchestrator/mes-orchestrator.js).
4. **Déclenche une sortie physique via un relais USB** (carte HID compatible
   [usb-relay-hid](https://github.com/pavel-a/usb-relay-hid)) selon le verdict: un canal pour
   "pass", un canal pour "fail/reject".

Ce n'est pas un service qui tourne en continu comme l'orchestrateur Node — c'est un exécutable
qu'on appelle une fois par pièce/action (depuis un automate, un script, un déclencheur externe),
qui fait son travail et rend un code de sortie (0 = OK, 1 = erreur) plus un rapport JSON sur stdout.

## Structure

```
mes-relay-gateway/
  src/MesRelayGateway/        Projet C# (.csproj, Program.cs)
    Configuration/             Lecture config (client-config.json, station.ini, relay-config.json, CLI)
    Mes/                       Client MES (réflexion MES_HAI.dll) + classification d'erreurs
    Relay/                     P/Invoke usb_relay_device.dll + contrôleur haut niveau
  config/                      Templates de configuration
  install/                     Script d'installation client (PowerShell)
```

## 1) Compiler

Prérequis: .NET SDK 8 (`dotnet --version`).

```powershell
cd mes-relay-gateway\src\MesRelayGateway
dotnet publish .\MesRelayGateway.csproj -c Release -r win-x64 --self-contained false -o ..\..\publish
```

Sortie attendue: `mes-relay-gateway\publish\MesRelayGateway.exe`

## 2) Dépendance native : usb_relay_device.dll

Ce dépôt **ne fournit pas** la DLL native du relais. Elle vient du projet
[pavel-a/usb-relay-hid](https://github.com/pavel-a/usb-relay-hid) (build officiel ou fourni par
le vendeur de la carte). Placez `usb_relay_device.dll` (architecture x86 ou x64, cohérente avec
la façon dont `MesRelayGateway.exe` a été publié) **à côté de `MesRelayGateway.exe`**.

Sans cette DLL, l'outil fonctionne quand même pour lire la config et parler au MES — seule
l'étape de déclenchement du relais échoue (erreur explicite dans le rapport JSON, `relay.ok: false`).

## 3) Installer sur le PC client

PowerShell en administrateur:

```powershell
cd mes-relay-gateway\install
.\install-relay-gateway.ps1 -StationName "STATION_01" -BuildIfNeeded `
  -RelayPassChannel 1 -RelayFailChannel 2
```

Déploie par défaut:

- `C:\ProgramData\MESApps\CIM\MES_HAI.xml`
- `C:\MESApps\ClientGateway\bridge\MES_HAI.dll` (partagée avec le bridge C# existant)
- `C:\MESApps\ClientGateway\relay-gateway\MesRelayGateway.exe`
- `C:\MESApps\ClientGateway\relay-gateway\config\client-config.json`
- `C:\MESApps\ClientGateway\relay-gateway\config\relay-config.json`

Le script rappelle si `usb_relay_device.dll` doit encore être copiée manuellement.

## 4) Utilisation

```powershell
# Via le fichier de config genere par l'installation
MesRelayGateway.exe --config C:\MESApps\ClientGateway\relay-gateway\config\client-config.json `
  --action move-out-and-test --serial SN001 --result Pass

# Ou entierement en ligne de commande, sans fichier config
MesRelayGateway.exe --xml C:\ProgramData\MESApps\CIM\MES_HAI.xml `
  --dll C:\MESApps\ClientGateway\bridge\MES_HAI.dll `
  --station STATION_01 --action get-info --serial SN001
```

Actions supportées: `login`, `get-info`, `move-in` (flux login→get-info→move-in),
`move-out-and-test` (flux login→move-out-and-test).

Sortie: un objet JSON sur stdout avec le détail des étapes MES, la classification de l'erreur
(`decision`) et le résultat de la commande relais (`relay`). Code de sortie 0 si le flux MES et
(si configuré) le relais ont réussi, 1 sinon.

## 5) Configuration du mapping relais

`relay-config.json`:

```json
{
  "relaySerialNumber": null,
  "passChannel": 1,
  "failChannel": 2,
  "pulseMs": 500
}
```

- `relaySerialNumber`: numéro de série de la carte à utiliser (`null` = première carte détectée).
- `passChannel` / `failChannel`: canal activé selon le verdict (`ErrorCode == 0` → pass, sinon fail).
- `pulseMs`: durée d'activation du canal avant relâchement (impulsion), en millisecondes.

Si ce fichier est absent, l'étape relais est simplement ignorée (le résultat MES est quand même
retourné) — utile pour tester sans matériel branché.
