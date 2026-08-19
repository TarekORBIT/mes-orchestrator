using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MesRelayGateway.Configuration;
using MesRelayGateway.Flow;
using MesRelayGateway.Mes;
using MesRelayGateway.Relay;

namespace MesRelayGateway.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private DispatcherTimer? _liveLogTimer;
    private string? _liveLogPath;
    private long _liveLogOffset;

    public MainWindow()
    {
        InitializeComponent();
        HistoryListView.ItemsSource = _history;
        ResetDiagram();
    }

    private GatewayMode SelectedMode =>
        ModeDllTestRadio.IsChecked == true ? GatewayMode.DllTest :
        ModeRealRadio.IsChecked == true ? GatewayMode.Real :
        GatewayMode.Mock;

    // ── Mode Test / Test DLL / Reel ────────────────────────────────────────
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent (XAML sets IsChecked="True"), before the rest
        // of the window is built — bail out until every field we touch actually exists.
        if (ModeHintText is null) return;

        ModeHintText.Text = SelectedMode switch
        {
            GatewayMode.Mock => "Aucun fichier requis : donnees et relais simules.",
            GatewayMode.DllTest => "Charge et interroge reellement MES_HAI.dll (via MesHaiBridge.exe si renseigne, sinon en direct) et capture son " +
                                    "log, mais ne declenche jamais le relais et ne contacte jamais les vraies IP Visteon (MES_HAI.xml est temporairement " +
                                    "remplace par une adresse locale, restauree automatiquement apres) - reponse en quelques secondes.",
            _ => "Necessite MES_HAI.dll + MES_HAI.xml valides, le reseau/VPN Visteon (les adresses IP de MES_HAI.xml doivent repondre) et, pour le relais, usb_relay_device.dll.",
        };
    }

    // ── Configuration ────────────────────────────────────────────────────
    private void OnLoadConfigClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "client-config.json|*.json|Tous les fichiers|*.*", Title = "Charger client-config.json" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var config = GatewayConfig.Load(dialog.FileName);
            StationTextBox.Text = config.StationName ?? StationTextBox.Text;
            XmlPathTextBox.Text = config.HaiXmlPath;
            DllPathTextBox.Text = config.HaiDllPath;
            BridgeExePathTextBox.Text = config.BridgeExePath;
            RelayConfigPathTextBox.Text = config.RelayConfigPath ?? RelayConfigPathTextBox.Text;
            HaiInstanceTextBox.Text = config.HaiInstanceName;
            ConfigPathText.Text = dialog.FileName;
            StatusText.Text = $"Configuration chargee depuis {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erreur de chargement", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnBrowseXmlClick(object sender, RoutedEventArgs e) => BrowseFile(XmlPathTextBox, "MES_HAI.xml|*.xml|Tous les fichiers|*.*");
    private void OnBrowseDllClick(object sender, RoutedEventArgs e) => BrowseFile(DllPathTextBox, "MES_HAI.dll|*.dll|Tous les fichiers|*.*");
    private void OnBrowseBridgeExeClick(object sender, RoutedEventArgs e) => BrowseFile(BridgeExePathTextBox, "MesHaiBridge.exe|*.exe|Tous les fichiers|*.*");
    private void OnBrowseRelayConfigClick(object sender, RoutedEventArgs e) => BrowseFile(RelayConfigPathTextBox, "relay-config.json|*.json|Tous les fichiers|*.*");

    private void BrowseFile(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    // ── Action ───────────────────────────────────────────────────────────
    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SerialTextBox is null || ResultComboBox is null || ResultLabel is null) return;

        var action = ResolveAction();
        SerialTextBox.IsEnabled = action != MesAction.Login;
        var showResult = action == MesAction.MoveOutAndTest;
        ResultComboBox.Visibility = showResult ? Visibility.Visible : Visibility.Collapsed;
        ResultLabel.Visibility = showResult ? Visibility.Visible : Visibility.Collapsed;
    }

    private MesAction ResolveAction() => ActionComboBox.SelectedIndex switch
    {
        0 => MesAction.Login,
        1 => MesAction.GetInfo,
        2 => MesAction.MoveIn,
        3 => MesAction.MoveOutAndTest,
        _ => MesAction.Login,
    };

    // ── Execution ────────────────────────────────────────────────────────
    private async void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        var mode = SelectedMode;
        var action = ResolveAction();
        var station = StationTextBox.Text.Trim();
        var serial = SerialTextBox.Text.Trim();
        var result = ((ComboBoxItem)ResultComboBox.SelectedItem)?.Content?.ToString() ?? "Pass";
        var xmlPath = XmlPathTextBox.Text.Trim();
        var dllPath = DllPathTextBox.Text.Trim();
        var bridgeExePath = BridgeExePathTextBox.Text.Trim();
        var relayConfigPath = RelayConfigPathTextBox.Text.Trim();
        var haiInstance = string.IsNullOrWhiteSpace(HaiInstanceTextBox.Text) ? "MES_HAI" : HaiInstanceTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(station))
        {
            if (mode is GatewayMode.Mock or GatewayMode.DllTest) station = "TEST_STATION";
            else { MessageBox.Show(this, "Le nom de station est requis en mode reel.", "Champ manquant", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }

        if (action != MesAction.Login && string.IsNullOrWhiteSpace(serial))
        {
            MessageBox.Show(this, "Le numero de serie est requis pour cette action.", "Champ manquant", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecuteButton.IsEnabled = false;
        StatusText.Text = mode == GatewayMode.Real
            ? "Execution en cours (peut prendre jusqu'a 1-2 min sur reseau lent)..."
            : "Execution en cours...";
        ResultBanner.Visibility = Visibility.Collapsed;
        ResetDiagram();
        StartLiveLog(mode, dllPath, bridgeExePath);

        try
        {
            var outcome = await Task.Run(() => RunFlow(mode, dllPath, bridgeExePath, haiInstance, relayConfigPath, station, action, serial, result, HighlightStep));
            StopLiveLog();
            ShowResult(mode, outcome);
            AddHistory(mode, action, station, serial, outcome.Flow, error: null);
            StatusText.Text = $"Termine a {DateTime.Now:HH:mm:ss} (mesClient={outcome.MesClientMode}).";
        }
        catch (Exception ex)
        {
            StopLiveLog();
            var effective = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            ShowError(effective.Message);
            AddHistory(mode, action, station, serial, flow: null, error: effective.Message);
            StatusText.Text = $"Erreur a {DateTime.Now:HH:mm:ss}.";
        }
        finally
        {
            ExecuteButton.IsEnabled = true;
        }
    }

    private sealed record FlowOutcome(FlowResult Flow, string MesClientMode, string? XmlOverrideNote);

    /// <summary>Runs off the UI thread: builds the MES client / relay driver for the chosen mode and calls GatewayRunner.</summary>
    private static FlowOutcome RunFlow(GatewayMode mode, string dllPath, string bridgeExePath, string haiInstance, string relayConfigPath, string station, MesAction action, string serial, string result, Action<GatewayStep> onStep)
    {
        // Mode Test DLL: swap the fixed MES_HAI.xml MES_HAI.dll actually reads for a local,
        // instantly-refusing address so a call fails in seconds instead of ~1-2 min of real
        // TCP timeouts, and never reaches the real Visteon network. Mode Reel instead heals
        // any leftover swap from a previously interrupted Test DLL run, defensively.
        IDisposable? xmlOverrideScope = null;
        string? xmlOverrideNote = null;
        if (mode == GatewayMode.DllTest)
        {
            xmlOverrideScope = MesXmlOverride.Apply(MesXmlOverride.FixedXmlPath);
            xmlOverrideNote = xmlOverrideScope is not null
                ? $"MES_HAI.xml ({MesXmlOverride.FixedXmlPath}) temporairement remplace par 127.0.0.1 - restaure a la fin de l'appel."
                : $"MES_HAI.xml ({MesXmlOverride.FixedXmlPath}) introuvable - pas de substitution.";
        }
        else if (mode == GatewayMode.Real)
        {
            MesXmlOverride.RestoreIfNeeded(MesXmlOverride.FixedXmlPath);
        }

        try
        {
            RelayConfig? relayConfig = null;
            if (!string.IsNullOrWhiteSpace(relayConfigPath) && File.Exists(relayConfigPath))
            {
                relayConfig = RelayConfig.Load(relayConfigPath);
            }

            var mesClientMode = "mock";
            using IMesClient mes = mode == GatewayMode.Mock
                ? new MockMesClient()
                : CreateRealClient(dllPath, bridgeExePath, haiInstance, out mesClientMode);

            // DllTest never touches the relay, even if a relay-config resolves — the point of
            // this mode is to exercise MES_HAI.dll/its log safely, not the physical output.
            IRelayDriver? relayDriver = mode switch
            {
                GatewayMode.Mock => relayConfig is null ? null : new MockRelayDriver(),
                GatewayMode.DllTest => null,
                GatewayMode.Real => relayConfig is null ? null : new UsbRelayDriver(),
                _ => null,
            };

            var flow = GatewayRunner.Run(mes, relayDriver, mode == GatewayMode.DllTest ? null : relayConfig, station, action, serial, result, user: null, password: null, onStep);
            return new FlowOutcome(flow, mesClientMode, xmlOverrideNote);
        }
        finally
        {
            xmlOverrideScope?.Dispose();
        }
    }

    private static IMesClient CreateRealClient(string dllPath, string bridgeExePath, string haiInstance, out string mesClientMode)
    {
        // Same bridgeTimeoutMs default as GatewayConfig - long enough for the off-network
        // case where MES_HAI.dll's own load-balancing retries both CIM servers before giving up.
        var created = MesClientFactory.CreateReal(dllPath, haiInstance, bridgeExePath, bridgeTimeoutMs: 120000, noBridge: false);
        mesClientMode = created.Mode;
        return created.Client;
    }

    // ── Journal en temps reel ────────────────────────────────────────────
    private void StartLiveLog(GatewayMode mode, string dllPath, string bridgeExePath)
    {
        StopLiveLog();
        EngineLogText.Text = string.Empty;

        if (mode == GatewayMode.Mock)
        {
            EngineLogExpander.Visibility = Visibility.Collapsed;
            return;
        }

        // Mirror MesClientFactory's own bridge-vs-direct choice so we watch the right file:
        // MesHaiBridge.exe writes Log\MES_HAI.log next to itself; a direct in-process load
        // writes it next to this GUI's own exe (AppContext.BaseDirectory).
        var usesBridge = !string.IsNullOrWhiteSpace(bridgeExePath) && File.Exists(bridgeExePath);
        _liveLogPath = usesBridge
            ? Path.Combine(Path.GetDirectoryName(bridgeExePath)!, "Log", "MES_HAI.log")
            : Path.Combine(AppContext.BaseDirectory, "Log", "MES_HAI.log");
        _liveLogOffset = MesLogReader.GetLength(_liveLogPath);

        EngineLogExpander.Visibility = Visibility.Visible;
        EngineLogExpander.IsExpanded = true;

        _liveLogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _liveLogTimer.Tick += (_, _) => PollLiveLog();
        _liveLogTimer.Start();
    }

    private void PollLiveLog()
    {
        if (_liveLogPath is null) return;
        var len = MesLogReader.GetLength(_liveLogPath);
        if (len <= _liveLogOffset) return;

        var chunk = MesLogReader.ReadFrom(_liveLogPath, _liveLogOffset);
        _liveLogOffset = len;
        if (!string.IsNullOrEmpty(chunk))
        {
            EngineLogText.AppendText(chunk);
            EngineLogText.ScrollToEnd();
        }
    }

    private void StopLiveLog()
    {
        if (_liveLogTimer is null) return;
        _liveLogTimer.Stop();
        _liveLogTimer = null;
        PollLiveLog(); // catch anything written between the last tick and the process exiting
    }

    // ── Diagramme d'execution ────────────────────────────────────────────
    private enum DiagramNodeState { Pending, Active, Ok, Fail }

    private static readonly SolidColorBrush PendingBg = new(Color.FromRgb(0xEC, 0xEF, 0xF1));
    private static readonly SolidColorBrush PendingBorder = new(Color.FromRgb(0xB0, 0xBE, 0xC5));
    private static readonly SolidColorBrush ActiveBg = new(Color.FromRgb(0xBB, 0xDE, 0xFB));
    private static readonly SolidColorBrush ActiveBorder = new(Color.FromRgb(0x15, 0x65, 0xC0));
    private static readonly SolidColorBrush OkBg = new(Color.FromRgb(0xC8, 0xE6, 0xC9));
    private static readonly SolidColorBrush OkBorder = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush FailBg = new(Color.FromRgb(0xFF, 0xCD, 0xD2));
    private static readonly SolidColorBrush FailBorder = new(Color.FromRgb(0xC6, 0x28, 0x28));

    private Border[] AllDiagramNodes => [NodeLogin, NodeConnState, NodeScan, NodeGetInfo, NodeErrorCheck1, NodeMoveIn, NodeErrorCheck2, NodeMoveOutAndTest, NodeErrorCheck3, NodeEnd];

    private void ResetDiagram()
    {
        foreach (var node in AllDiagramNodes) SetNode(node, DiagramNodeState.Pending);
        NodeEndText.Text = "Nouvelle piece / Erreur";
    }

    private static void SetNode(Border node, DiagramNodeState state)
    {
        (node.Background, node.BorderBrush, node.BorderThickness) = state switch
        {
            DiagramNodeState.Active => ((Brush)ActiveBg, (Brush)ActiveBorder, new Thickness(2.5)),
            DiagramNodeState.Ok => (OkBg, OkBorder, new Thickness(1.5)),
            DiagramNodeState.Fail => (FailBg, FailBorder, new Thickness(2.5)),
            _ => (PendingBg, PendingBorder, new Thickness(1)),
        };
    }

    /// <summary>
    /// Invoked (off the UI thread, from within GatewayRunner.Run) right before each MES call.
    /// Only meant to give live feedback while a call is in flight - the authoritative coloring
    /// happens afterwards in FinalizeDiagram, once the real per-step outcomes are known.
    /// </summary>
    private void HighlightStep(GatewayStep step)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (step)
            {
                case GatewayStep.Login:
                    SetNode(NodeLogin, DiagramNodeState.Active);
                    SetNode(NodeConnState, DiagramNodeState.Active);
                    break;
                case GatewayStep.GetInfo:
                    SetNode(NodeLogin, DiagramNodeState.Ok);
                    SetNode(NodeConnState, DiagramNodeState.Ok);
                    SetNode(NodeScan, DiagramNodeState.Active);
                    SetNode(NodeGetInfo, DiagramNodeState.Active);
                    SetNode(NodeErrorCheck1, DiagramNodeState.Active);
                    break;
                case GatewayStep.MoveIn:
                    SetNode(NodeGetInfo, DiagramNodeState.Ok);
                    SetNode(NodeErrorCheck1, DiagramNodeState.Ok);
                    SetNode(NodeMoveIn, DiagramNodeState.Active);
                    SetNode(NodeErrorCheck2, DiagramNodeState.Active);
                    break;
                case GatewayStep.MoveOutAndTest:
                    SetNode(NodeLogin, DiagramNodeState.Ok);
                    SetNode(NodeConnState, DiagramNodeState.Ok);
                    SetNode(NodeMoveOutAndTest, DiagramNodeState.Active);
                    SetNode(NodeErrorCheck3, DiagramNodeState.Active);
                    break;
            }
        });
    }

    /// <summary>Authoritative, final coloring of every node once the flow (all steps) is known.</summary>
    private void FinalizeDiagram(FlowResult flow)
    {
        ResetDiagram();

        var login = flow.Steps.Count > 0 ? flow.Steps[0] : null;
        SetNode(NodeLogin, StateFor(login));
        SetNode(NodeConnState, StateFor(login));
        if (login is not { Ok: true })
        {
            SetNode(NodeEnd, DiagramNodeState.Fail);
            NodeEndText.Text = "Erreur";
            return;
        }

        switch (flow.Action)
        {
            case MesAction.Login:
                SetNode(NodeEnd, DiagramNodeState.Ok);
                NodeEndText.Text = "Nouvelle piece";
                break;

            case MesAction.GetInfo:
            {
                var info = flow.Steps.Count > 1 ? flow.Steps[1] : null;
                SetNode(NodeScan, DiagramNodeState.Ok);
                SetNode(NodeGetInfo, StateFor(info));
                SetNode(NodeErrorCheck1, StateFor(info));
                FinishEnd(info);
                break;
            }

            case MesAction.MoveIn:
            {
                var info = flow.Steps.Count > 1 ? flow.Steps[1] : null;
                SetNode(NodeScan, DiagramNodeState.Ok);
                SetNode(NodeGetInfo, StateFor(info));
                SetNode(NodeErrorCheck1, StateFor(info));
                if (info is not { Ok: true }) { SetNode(NodeEnd, DiagramNodeState.Fail); NodeEndText.Text = "Erreur"; break; }

                var moveIn = flow.Steps.Count > 2 ? flow.Steps[2] : null;
                SetNode(NodeMoveIn, StateFor(moveIn));
                SetNode(NodeErrorCheck2, StateFor(moveIn));
                FinishEnd(moveIn);
                break;
            }

            case MesAction.MoveOutAndTest:
            {
                var mot = flow.Steps.Count > 1 ? flow.Steps[1] : null;
                SetNode(NodeMoveOutAndTest, StateFor(mot));
                SetNode(NodeErrorCheck3, StateFor(mot));
                FinishEnd(mot);
                break;
            }
        }

        void FinishEnd(MesResult? r)
        {
            SetNode(NodeEnd, r is { Ok: true } ? DiagramNodeState.Ok : DiagramNodeState.Fail);
            NodeEndText.Text = r is { Ok: true } ? "Nouvelle piece" : "Erreur";
        }
    }

    private static DiagramNodeState StateFor(MesResult? r) => r is null ? DiagramNodeState.Pending : (r.Ok ? DiagramNodeState.Ok : DiagramNodeState.Fail);

    // ── Affichage du resultat ────────────────────────────────────────────
    private void ShowResult(GatewayMode mode, FlowOutcome outcome)
    {
        var flow = outcome.Flow;
        ResultBanner.Visibility = Visibility.Visible;
        FinalizeDiagram(flow);

        if (flow.Ok)
        {
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
            ResultBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
            ResultTitleText.Text = "OK - Piece conforme";
            ResultTitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
        }
        else
        {
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0xEE));
            ResultBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            ResultTitleText.Text = "ERREUR - " + DecisionMessage(flow.Decision);
            ResultTitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0x1C, 0x1C));
        }

        var modeLabel = mode switch
        {
            GatewayMode.Mock => "TEST (simule)",
            GatewayMode.DllTest => $"TEST DLL ({outcome.MesClientMode}, relais desactive)",
            _ => $"REEL ({outcome.MesClientMode})",
        };
        ResultDetailText.Text =
            $"Mode: {modeLabel}  |  Action: {flow.Action}  |  Station: {flow.Station}  |  Serie: {flow.SerialNumber ?? "-"}\n" +
            $"MES: ErrorCode={flow.FinalResult.ErrorCode?.ToString() ?? "-"}  ErrorDescription={flow.FinalResult.ErrorDescription ?? "-"}";

        var relayLine = flow.Relay switch
        {
            { } r when r.Ok => $"Relais: canal {r.Channel} declenche ({r.Verdict}), carte {r.BoardSerialNumber}{(r.Simulated ? " [simule]" : "")}.",
            { } r => $"Relais: ECHEC canal {r.Channel} ({r.Verdict}) - {r.Error}",
            null when flow.RelayNote is not null => flow.RelayNote,
            null when mode == GatewayMode.DllTest => "Relais: volontairement desactive en Mode Test DLL.",
            _ => "Relais: non configure.",
        };
        ResultRelayText.Text = outcome.XmlOverrideNote is null ? relayLine : $"{relayLine}\n{outcome.XmlOverrideNote}";

        // The live-streamed text should already match, but the flow's own captured log is
        // authoritative (e.g. includes trailing lines written right as the process exited).
        var engineLog = string.Join(Environment.NewLine, flow.Steps.Select(s => s.EngineLog).Where(l => !string.IsNullOrEmpty(l)));
        if (!string.IsNullOrEmpty(engineLog))
        {
            EngineLogExpander.Visibility = Visibility.Visible;
            EngineLogText.Text = engineLog;
            EngineLogText.ScrollToEnd();
        }

        ResultJsonText.Text = JsonSerializer.Serialize(flow, JsonOptions);
    }

    private void ShowError(string message)
    {
        ResultBanner.Visibility = Visibility.Visible;
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0xEE));
        ResultBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        ResultTitleText.Text = "ERREUR";
        ResultTitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0x1C, 0x1C));
        ResultDetailText.Text = message;
        ResultRelayText.Text = string.Empty;
        ResultJsonText.Text = string.Empty;
        ResetDiagram();
        SetNode(NodeEnd, DiagramNodeState.Fail);
        NodeEndText.Text = "Erreur";
    }

    private static string DecisionMessage(ErrorDecision decision) => decision.Action switch
    {
        "CONTINUE_FLOW" => "OK",
        "RELOGIN_AND_RETRY_ONCE" => "Session MES non connectee, relogin necessaire",
        "BLOCK_AND_CHECK_STATION_CONFIG" => "Station MES invalide, verifier la configuration",
        "SWITCH_SERVER_AND_RETRY_ONCE" => "Probleme reseau, basculer sur le serveur secondaire",
        "BLOCK_AND_ESCALATE" => "Reponse MES invalide, escalader",
        "BLOCK_PART_AND_CREATE_INCIDENT" => "Erreur MES non classifiee, piece bloquee",
        _ => decision.Reason,
    };

    private void AddHistory(GatewayMode mode, MesAction action, string station, string serial, FlowResult? flow, string? error)
    {
        var modeLabel = mode switch
        {
            GatewayMode.Mock => "Test",
            GatewayMode.DllTest => "Test DLL",
            _ => "Reel",
        };

        _history.Insert(0, new HistoryEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Mode = modeLabel,
            Action = action.ToString(),
            Station = station,
            Serial = string.IsNullOrWhiteSpace(serial) ? "-" : serial,
            Result = flow is null ? "ERREUR" : (flow.Ok ? "OK" : "ECHEC"),
            Relay = flow?.Relay is null ? "-" : $"canal {flow.Relay.Channel} ({flow.Relay.Verdict})",
            Detail = error ?? flow?.FinalResult.ErrorDescription,
        });
    }
}
