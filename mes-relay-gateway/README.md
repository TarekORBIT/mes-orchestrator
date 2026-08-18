# MES Relay Gateway

Outils .NET (C#) autonomes, compilés en `.exe` Windows, qui:

1. Lisent leur configuration (`MES_HAI.xml`, `MES_HAI.dll`, nom de station, mapping relais) depuis des
   **chemins de fichiers fournis en entrée** — via un fichier `client-config.json`, des champs dans
   l'interface graphique, et/ou des `--flag` en ligne de commande. Rien n'est codé en dur au-delà
   des valeurs par défaut, qui reprennent les chemins de déploiement déjà utilisés par
   [`production/`](../production):
   - `C:\ProgramData\MESApps\CIM\MES_HAI.xml`
   - `C:\MESApps\ClientGateway\bridge\MES_HAI.dll`
2. **Exploitent `MES_HAI.dll`** pour de vrai (login, get-info, move-in, move-out-and-test). En
   Mode Réel, si `MesHaiBridge.exe` (le bridge C# de [`production/bridge`](../production/bridge))
   existe au chemin configuré, l'appel passe par ce bridge — un processus par appel, JSON sur
   stdin/stdout, exactement comme le fait l'orchestrateur Node. Sinon, la DLL est chargée
   directement dans le process (réflexion), en secours.
3. **Détectent l'erreur** retournée par le MES (`ErrorCode` / `ErrorDescription`, y compris quand
   le résultat brut est un enum direct comme `Login()` plutôt qu'un objet) et la classent (session
   expirée, station invalide, panne réseau, erreur métier...) — portage direct de
   `classifyErrorDetail` dans [`production/orchestrator/mes-orchestrator.js`](../production/orchestrator/mes-orchestrator.js).
4. **Lisent le journal que MES_HAI.dll génère normalement** (`Log\MES_HAI.log`, écrit par la DLL
   elle-même via log4net/LogLibrary.dll: résolution de la config, load-balancing entre les
   serveurs CIM, erreurs WCF...) et l'affichent en clair après chaque appel — utile pour
   diagnostiquer sans être sur le réseau usine.
5. **Déclenchent une sortie physique via un relais USB** (carte HID compatible
   [usb-relay-hid](https://github.com/pavel-a/usb-relay-hid)) selon le verdict: un canal pour
   "pass", un canal pour "fail/reject".

Deux **modes**, disponibles aussi bien dans la GUI qu'en ligne de commande:
- **Mode Test** : simulation complète (MES et relais tous les deux simulés), aucun fichier ni
  matériel requis — pour apprendre l'outil ou vérifier la logique de classification d'erreur.
- **Mode Réel** : appel effectif à `MES_HAI.dll` et au relais USB physique — nécessite le réseau
  usine/VPN Visteon et le matériel branché.

## Structure

```
mes-relay-gateway/
  MesRelayGateway.sln
  src/
    MesRelayGateway.Core/       Logique partagee (config, client MES, relais, classification,
                                 GatewayRunner) + implementations mock pour le Mode Test
    MesRelayGateway/             CLI (Program.cs) - pour automate/script externe
    MesRelayGateway.Gui/         Interface graphique WPF - pour un usage manuel/technicien
  config/                        Templates de configuration
  install/                       Script d'installation client (PowerShell)
```

## 1) Interface graphique (recommandé pour découvrir/tester l'outil)

```powershell
cd mes-relay-gateway\src\MesRelayGateway.Gui
dotnet publish .\MesRelayGateway.Gui.csproj -c Release -r win-x64 --self-contained false -o ..\..\publish-gui
..\..\publish-gui\MesRelayGatewayGui.exe
```

La fenêtre s'ouvre directement en **Mode Test** (aucun fichier requis, tout est simulé):

1. Choisir l'action (`Login`, `Get Info`, `Move In`, `Move Out + Test`), un numéro de série
   (`SN001` ou `SN002` sont "connus" en mode test, tout autre numéro renvoie une erreur simulée
   "SerialNotFound"), et cliquer **Exécuter**.
2. Le résultat s'affiche en clair (bandeau vert = OK, rouge = erreur, avec l'explication), plus le
   détail technique (JSON) repliable, et un historique des essais en bas de fenêtre.
3. Pour passer en conditions réelles: cocher **Mode Réel**, renseigner (ou charger via
   *"Charger client-config.json..."*) les chemins vers `MES_HAI.xml`, `MES_HAI.dll`,
   `MesHaiBridge.exe`, le nom de station, et le fichier `relay-config.json` — puis exécuter. Ça
   fonctionne aussi **sans être sur le réseau Visteon**: la DLL répond alors avec un vrai statut
   métier (ex. `NotLogged`, ErrorCode 3) au lieu de planter, ce qui permet de valider tout le
   pipeline (chargement DLL, bridge, logging) sans VPN — voir §3.

   Après exécution, l'expander **"Journal MES_HAI.dll"** affiche le `Log\MES_HAI.log` généré par
   la DLL pendant cet appel (config trouvée, serveurs CIM essayés, erreurs réseau...).

## 2) Ligne de commande (pour automate/script externe)

```powershell
cd mes-relay-gateway\src\MesRelayGateway
dotnet publish .\MesRelayGateway.csproj -c Release -r win-x64 --self-contained false -o ..\..\publish
```

Sortie attendue: `mes-relay-gateway\publish\MesRelayGateway.exe`

```powershell
# Mode test - aucun fichier requis
MesRelayGateway.exe --mode test --station TEST_STATION --action move-in --serial SN001

# Mode reel, via le fichier de config genere par l'installation
MesRelayGateway.exe --config C:\MESApps\ClientGateway\relay-gateway\config\client-config.json `
  --action move-out-and-test --serial SN001 --result Pass

# Mode reel, entierement en ligne de commande, sans fichier config
MesRelayGateway.exe --xml C:\ProgramData\MESApps\CIM\MES_HAI.xml `
  --dll C:\MESApps\ClientGateway\bridge\MES_HAI.dll `
  --station STATION_01 --action get-info --serial SN001

# Mode reel via MesHaiBridge.exe explicite (voir §3), avec le journal MES_HAI.dll dans la sortie
MesRelayGateway.exe --dll C:\MESApps\ClientGateway\bridge\MES_HAI.dll `
  --bridge-exe C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe `
  --station STATION_01 --action login
```

Actions supportées: `login`, `get-info`, `move-in` (flux login→get-info→move-in),
`move-out-and-test` (flux login→move-out-and-test).

Sortie: un objet JSON sur stdout avec `mesClientMode` (`"bridge"` ou `"direct"`, voir §3), le
détail des étapes MES (chaque étape inclut `EngineLog`, le `Log\MES_HAI.log` capturé pendant cet
appel), la classification de l'erreur (`decision`) et le résultat de la commande relais (`relay`).
Code de sortie 0 si le flux MES et (si configuré) le relais ont réussi, 1 sinon.

## 3) Comment MES_HAI.dll est appelée en Mode Réel (bridge vs direct)

`bridgeExePath` (dans `client-config.json`, ou `--bridge-exe` en CLI, ou le champ
"MesHaiBridge.exe" dans la GUI) pointe par défaut vers
`C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe`. À chaque appel:

- **Si ce fichier existe**: l'appel passe par `MesHaiBridge.exe` (un process par appel, requête
  JSON sur stdin, réponse JSON sur stdout) — le même bridge C# que
  [`production/bridge`](../production/bridge), utilisé de la même façon que l'orchestrateur Node
  le fait déjà. `mesClientMode` dans la sortie JSON vaut alors `"bridge"`.
- **Sinon** (ou avec `--no-bridge`): `MES_HAI.dll` est chargée directement dans le process de
  `MesRelayGateway(Gui).exe` par réflexion, sans binaire intermédiaire. `mesClientMode` vaut
  `"direct"`.

Les deux chemins produisent le même `MesResult` (ErrorCode/ErrorDescription/log) — le choix
n'affecte que la façon dont la DLL est hébergée.

**Tester sans être sur le réseau usine**: pointez `haiDllPath` vers le dossier qui contient
`MES_HAI.dll` **et ses dépendances** (`LogLibrary.dll`, `log4net.dll`, `Newtonsoft.Json.dll` —
dans ce dépôt, `dll Env\dll Env\`), lancez une action (ex. `login`). La DLL se charge, contacte
réellement les serveurs CIM listés dans `MES_HAI.xml`, échoue à s'y connecter (hors réseau/VPN
Visteon) et retourne un vrai statut métier — typiquement `ErrorCode 3 "NotLogged"` — au lieu de
planter. C'est normal et attendu: ça valide tout le pipeline (chargement DLL, bridge, logging)
sans nécessiter le VPN. Seul un `ErrorCode 0` (login réellement accepté) demande d'être sur le
réseau Visteon. Comptez large sur le temps de réponse: `MES_HAI.dll` retente les deux serveurs
CIM (primaire + secondaire), ~21s de timeout chacun, parfois deux fois — d'où le
`bridgeTimeoutMs` par défaut de 120000ms (au lieu des 20000ms de production/orchestrator, calibrés
pour un réseau qui répond).

## 4) Dépendance native : usb_relay_device.dll

Ce dépôt **ne fournit pas** la DLL native du relais. Elle vient du projet
[pavel-a/usb-relay-hid](https://github.com/pavel-a/usb-relay-hid) (build officiel ou fourni par
le vendeur de la carte). En Mode Réel, placez `usb_relay_device.dll` (architecture x86 ou x64,
cohérente avec la façon dont l'exe a été publié) **à côté de `MesRelayGateway.exe` /
`MesRelayGatewayGui.exe`**.

Sans cette DLL, l'outil fonctionne quand même pour lire la config et parler au MES — seule
l'étape de déclenchement du relais échoue (erreur explicite, `relay.ok: false`). En Mode Test,
elle n'est jamais nécessaire.

## 5) Installer sur le PC client

PowerShell en administrateur:

```powershell
cd mes-relay-gateway\install
.\install-relay-gateway.ps1 -StationName "STATION_01" -BuildIfNeeded -WithGui -WithBridge `
  -RelayPassChannel 1 -RelayFailChannel 2
```

Déploie par défaut:

- `C:\ProgramData\MESApps\CIM\MES_HAI.xml`
- `C:\MESApps\ClientGateway\bridge\MES_HAI.dll` (partagée avec le bridge C# existant)
- `C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe` (si `-WithBridge`; sinon Mode Réel bascule
  automatiquement en appel direct)
- `C:\MESApps\ClientGateway\relay-gateway\MesRelayGateway.exe`
- `C:\MESApps\ClientGateway\relay-gateway\MesRelayGatewayGui.exe` (si `-WithGui`)
- `C:\MESApps\ClientGateway\relay-gateway\config\client-config.json`
- `C:\MESApps\ClientGateway\relay-gateway\config\relay-config.json`

Le script rappelle si `usb_relay_device.dll` doit encore être copiée manuellement.

## 6) Configuration du mapping relais

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
