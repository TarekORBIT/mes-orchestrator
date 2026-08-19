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

Trois **modes**, disponibles aussi bien dans la GUI qu'en ligne de commande (`--mode`):
- **Mode Test** (`test`) : simulation complète (MES et relais tous les deux simulés), aucun
  fichier ni matériel requis — pour apprendre l'outil ou vérifier la logique de classification
  d'erreur.
- **Mode Test DLL** (`dll-test`) : appelle réellement `MES_HAI.dll` (via `MesHaiBridge.exe` si
  disponible, sinon en direct) et capture son vrai journal — mais **ne déclenche jamais le
  relais**, même si un `relay-config.json` est configuré. Utilise par défaut les **vraies**
  adresses IP de `MES_HAI.xml` (comme Mode Réel) : sur le réseau usine/VPN Visteon, ça valide un
  vrai login sans risquer de piloter le relais pour de faux. Hors réseau, cochez **"Simuler hors
  réseau"** (GUI) ou `--offline` (CLI) pour que la DLL échoue en quelques secondes contre une
  adresse locale au lieu d'attendre ~1-2 min de timeout réseau réel — voir §3.
- **Mode Réel** (`real`) : appel effectif à `MES_HAI.dll` (bridge ou direct) **et** au relais USB
  physique si configuré — le mode de production, sur le réseau usine/VPN Visteon.

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
3. Pour exercer la vraie DLL **sans jamais risquer de piloter le relais**: cocher
   **Mode Test DLL**, renseigner les chemins vers `MES_HAI.dll` (dossier complet avec ses
   dépendances, voir §3) et `MesHaiBridge.exe`, puis exécuter. Par défaut ce mode utilise les
   **vraies** adresses IP de `MES_HAI.xml` (identique à Mode Réel còté réseau) — sur le réseau
   usine/VPN Visteon, ça valide un vrai login. Hors réseau, cochez **"Simuler hors réseau"**:
   la DLL tourne quand même réellement (chargement, log4net/LogLibrary) mais son fichier de
   serveurs est temporairement substitué par une adresse locale qui échoue instantanément — elle
   ne touche alors jamais `10.216.140.205/206` — et répond avec un vrai statut métier (ex.
   `NotLogged`, ErrorCode 3) au lieu d'attendre ~1-2 min. Le relais, lui, reste toujours
   désactivé dans ce mode, coché ou non. Détails techniques en §3.

   Pendant l'exécution, le panneau **"Journal MES_HAI.dll en temps réel"** s'ouvre et se remplit
   au fur et à mesure — utile pour voir que ça travaille réellement pendant une attente réseau
   (jusqu'à ~2 min sans "Simuler hors réseau"), plutôt que de se demander si l'appli est bloquée.
   Le **"Diagramme d'exécution"** (juste au-dessus) illustre le protocole Machine ↔ `MES_HAI.dll`
   (`Login()` → `ConnectionState` → `Scan` → `Serial_GetInformation()` → **vérification
   PartNumber** (`WorkOrder_GetActiveByStation` vs `SerialInformation.PartNumber` — un vrai
   mismatch bloque le flux avant `Serial_MoveIn()`, comme sur le poste réel) → `Serial_MoveIn()`
   → ...) et met en surbrillance bleue l'étape en cours, puis colore chaque case en vert (OK) ou
   rouge (échec) une fois le résultat connu — `Serial_MoveOutAndTestResults()` est marquée à part
   (barrée) car non couverte par les tests actuels.
4. Pour la production: cocher **Mode Réel**, renseigner en plus le `relay-config.json`, puis
   exécuter depuis le réseau Visteon avec le relais USB branché — le verdict MES déclenche alors
   réellement le canal pass/fail.

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

# Mode Test DLL - vraie DLL + vrai journal, jamais de relais, vraies IP de MES_HAI.xml
MesRelayGateway.exe --mode dll-test --dll C:\MESApps\ClientGateway\bridge\MES_HAI.dll `
  --bridge-exe C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe --station STATION_01 --action login

# Mode Test DLL --offline - meme chose mais sans toucher au reseau Visteon (adresse locale)
MesRelayGateway.exe --mode dll-test --offline --dll "dll Env\dll Env\MES_HAI.dll" `
  --bridge-exe production\bridge\publish\MesHaiBridge.exe --station TEST_STATION --action login
```

Actions supportées: `login`, `get-info`, `move-in` (flux login→get-info→move-in),
`move-out-and-test` (flux login→move-out-and-test).

Sortie: un objet JSON sur stdout avec `mesClientMode` (`"bridge"` ou `"direct"`, voir §3), le
détail des étapes MES (chaque étape inclut `EngineLog`, le `Log\MES_HAI.log` capturé pendant cet
appel), la classification de l'erreur (`decision`) et le résultat de la commande relais (`relay`).
Code de sortie 0 si le flux MES et (si configuré) le relais ont réussi, 1 sinon.

## 3) Comment MES_HAI.dll est appelée (bridge vs direct), et le mode --offline

`bridgeExePath` (dans `client-config.json`, ou `--bridge-exe` en CLI, ou le champ
"MesHaiBridge.exe" dans la GUI) pointe par défaut vers
`C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe`. En **Mode Test DLL** comme en **Mode Réel**,
à chaque appel:

- **Si ce fichier existe**: l'appel passe par `MesHaiBridge.exe` (un process par appel, requête
  JSON sur stdin, réponse JSON sur stdout) — le même bridge C# que
  [`production/bridge`](../production/bridge), utilisé de la même façon que l'orchestrateur Node
  le fait déjà. `mesClientMode` dans la sortie JSON vaut alors `"bridge"`.
- **Sinon** (ou avec `--no-bridge`): `MES_HAI.dll` est chargée directement dans le process de
  `MesRelayGateway(Gui).exe` par réflexion, sans binaire intermédiaire. `mesClientMode` vaut
  `"direct"`.

Les deux chemins produisent le même `MesResult` (ErrorCode/ErrorDescription/log) — le choix
n'affecte que la façon dont la DLL est hébergée.

**Mode Test DLL utilise par défaut les vraies IP** de `MES_HAI.xml`, exactement comme Mode Réel
— seul le relais reste désactivé d'office, quoi qu'il arrive. C'est voulu: si vous êtes sur le
réseau usine/VPN Visteon, ça permet de valider un vrai login sans risquer de piloter le relais
pour de faux.

Techniquement, `MES_HAI.dll` ne lit pas le `--xml` qu'on lui passe — elle résout toujours son
propre chemin fixe, `C:\ProgramData\MESApps\CIM\MES_HAI.xml`, et son unique constructeur
(`Traceability(station)`) y lance un `LoadBalancing()` réseau *avant même* `Login()` ; il n'existe
aucun moyen de charger la DLL "silencieusement".

**Tester sans être sur le réseau usine**: cochez **"Simuler hors réseau"** (GUI, visible en Mode
Test DLL) ou ajoutez `--offline` (CLI). L'outil sauvegarde alors le contenu réel de ce fichier
fixe, le remplace temporairement par une adresse locale qui refuse la connexion instantanément
(`127.0.0.1`), laisse la DLL échouer en quelques secondes (au lieu de ~1-2 min de vrais timeouts
réseau) avec un vrai statut métier (`ErrorCode 3 "NotLogged"`) et un vrai `Log\MES_HAI.log`, puis
**restaure le fichier original** — même en cas de plantage (sauvegarde sur disque, auto-réparée
au prochain lancement, quel que soit le mode). Mode Réel, et Mode Test DLL sans `--offline`,
restaurent aussi ce fichier par précaution avant de démarrer, au cas où une exécution `--offline`
précédente aurait été interrompue brutalement — un swap ne peut donc jamais "fuiter" vers un vrai
appel. Seul un `ErrorCode 0` (login réellement accepté) confirme que la connexion au réseau
Visteon fonctionne pour de vrai.

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

## 6) Configuration du mapping relais (règles ErrorCode → canal)

`relay-config.json` (voir `config/relay-config.example.json`):

```json
{
  "relaySerialNumber": null,
  "rules": [
    { "errorCodes": "0", "channel": 1, "mode": "Pulse", "pulseMs": 3000 },
    { "errorCodes": "3", "channel": 3, "mode": "Latch" },
    { "errorCodes": "*", "channel": 2, "mode": "Pulse", "pulseMs": 3000 }
  ]
}
```

- `relaySerialNumber`: numéro de série de la carte à utiliser (`null` = première carte détectée).
- `rules`: liste de règles évaluées dans l'ordre. La **première règle spécifique** (`errorCodes`
  différent de `"*"`) qui correspond à l'`ErrorCode` MES gagne ; une règle `"*"` sert de secours
  si rien de plus spécifique n'a matché.
  - `errorCodes`: un code (`"0"`), une liste séparée par des virgules (`"1,2"`), ou `"*"` (tout le
    reste).
  - `channel`: canal du relais à déclencher.
  - `mode`:
    - `"Pulse"`: active le canal, attend `pulseMs` (ex. 3000 = 3s), puis le relâche automatiquement.
    - `"Latch"`: active le canal et le **laisse ON** — reste allumé jusqu'à un forçage OFF/Reset
      manuel (onglet "Relais USB" de la GUI, ou `--mode`... voir §7). Utile pour un défaut qui
      doit rester visible/actif tant qu'un opérateur ne l'a pas explicitement acquitté.
  - `pulseMs`: uniquement utilisé si `mode = "Pulse"`.

Si ce fichier est absent, l'étape relais est simplement ignorée (le résultat MES est quand même
retourné) — utile pour tester sans matériel branché. Les anciens fichiers au format
`passChannel`/`failChannel`/`pulseMs` continuent de se charger: ils sont automatiquement convertis
en deux règles (`"0"` → passChannel, `"*"` → failChannel) au premier chargement.

## 7) Onglet "Relais USB" (GUI) : détection, forçage manuel, édition des règles

Dans la GUI, la section **"Relais USB — Configuration et test"** (entre Configuration et Action)
permet de travailler sur le relais indépendamment d'un test MES:

- **Détecter les cartes relais**: énumère les cartes USB branchées (numéro de série, nombre de
  canaux) via `usb_relay_device.dll`.
- **Forçage manuel**: sur un canal donné (et une carte optionnelle par numéro de série), quatre
  actions indépendantes des règles ci-dessous — **Forcer ON** (maintenu), **Impulsion** (durée
  configurable), **Forcer OFF / Reset** (c'est ce bouton qui libère un canal resté ON en mode
  `Latch`), **Tout éteindre**, et **Lire l'état** (quels canaux sont actuellement ON).
- **Règles de déclenchement automatique**: table éditable (ajouter/supprimer des lignes) pour le
  `relay-config.json` en cours — "Charger" relit le fichier indiqué dans le champ Configuration,
  "Enregistrer" y écrit les règles actuelles.

Ces contrôles utilisent directement `usb_relay_device.dll` (§3 de la dépendance native) — sans
cette DLL, ils échouent proprement avec un message d'erreur explicite plutôt que de planter.
