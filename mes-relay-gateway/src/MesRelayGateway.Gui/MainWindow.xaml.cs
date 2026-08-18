using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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

    public MainWindow()
    {
        InitializeComponent();
        HistoryListView.ItemsSource = _history;
    }

    private bool IsTestMode => ModeTestRadio.IsChecked == true;

    // ── Mode Test / Reel ─────────────────────────────────────────────────
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent (XAML sets IsChecked="True"), before the rest
        // of the window is built — bail out until every field we touch actually exists.
        if (ModeHintText is null) return;

        ModeHintText.Text = IsTestMode
            ? "Aucun fichier requis : donnees et relais simules."
            : "Necessite MES_HAI.dll + MES_HAI.xml valides et, pour le relais, usb_relay_device.dll.";
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

    private void BrowseFile(System.Windows.Controls.TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    // ── Action ───────────────────────────────────────────────────────────
    private void OnActionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
        var isTest = IsTestMode;
        var action = ResolveAction();
        var station = StationTextBox.Text.Trim();
        var serial = SerialTextBox.Text.Trim();
        var result = ((System.Windows.Controls.ComboBoxItem)ResultComboBox.SelectedItem)?.Content?.ToString() ?? "Pass";
        var xmlPath = XmlPathTextBox.Text.Trim();
        var dllPath = DllPathTextBox.Text.Trim();
        var bridgeExePath = BridgeExePathTextBox.Text.Trim();
        var relayConfigPath = RelayConfigPathTextBox.Text.Trim();
        var haiInstance = string.IsNullOrWhiteSpace(HaiInstanceTextBox.Text) ? "MES_HAI" : HaiInstanceTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(station))
        {
            if (isTest) station = "TEST_STATION";
            else { MessageBox.Show(this, "Le nom de station est requis en mode reel.", "Champ manquant", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }

        if (action != MesAction.Login && string.IsNullOrWhiteSpace(serial))
        {
            MessageBox.Show(this, "Le numero de serie est requis pour cette action.", "Champ manquant", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecuteButton.IsEnabled = false;
        StatusText.Text = "Execution en cours...";
        ResultBanner.Visibility = Visibility.Collapsed;

        try
        {
            var outcome = await Task.Run(() => RunFlow(isTest, dllPath, bridgeExePath, haiInstance, relayConfigPath, station, action, serial, result));
            ShowResult(isTest, outcome);
            AddHistory(isTest, action, station, serial, outcome.Flow, error: null);
            StatusText.Text = $"Termine a {DateTime.Now:HH:mm:ss} (mesClient={outcome.MesClientMode}).";
        }
        catch (Exception ex)
        {
            var effective = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            ShowError(effective.Message);
            AddHistory(isTest, action, station, serial, flow: null, error: effective.Message);
            StatusText.Text = $"Erreur a {DateTime.Now:HH:mm:ss}.";
        }
        finally
        {
            ExecuteButton.IsEnabled = true;
        }
    }

    private sealed record FlowOutcome(FlowResult Flow, string MesClientMode);

    /// <summary>Runs off the UI thread: builds the MES client / relay driver for the chosen mode and calls GatewayRunner.</summary>
    private static FlowOutcome RunFlow(bool isTest, string dllPath, string bridgeExePath, string haiInstance, string relayConfigPath, string station, MesAction action, string serial, string result)
    {
        RelayConfig? relayConfig = null;
        if (!string.IsNullOrWhiteSpace(relayConfigPath) && File.Exists(relayConfigPath))
        {
            relayConfig = RelayConfig.Load(relayConfigPath);
        }

        var mesClientMode = "mock";
        using IMesClient mes = isTest
            ? new MockMesClient()
            : CreateRealClient(dllPath, bridgeExePath, haiInstance, out mesClientMode);
        IRelayDriver? relayDriver = relayConfig is null ? null : (isTest ? new MockRelayDriver() : new UsbRelayDriver());

        var flow = GatewayRunner.Run(mes, relayDriver, relayConfig, station, action, serial, result, user: null, password: null);
        return new FlowOutcome(flow, mesClientMode);
    }

    private static IMesClient CreateRealClient(string dllPath, string bridgeExePath, string haiInstance, out string mesClientMode)
    {
        // Same bridgeTimeoutMs default as GatewayConfig - long enough for the off-network
        // case where MES_HAI.dll's own load-balancing retries both CIM servers before giving up.
        var created = MesClientFactory.CreateReal(dllPath, haiInstance, bridgeExePath, bridgeTimeoutMs: 120000, noBridge: false);
        mesClientMode = created.Mode;
        return created.Client;
    }

    // ── Affichage du resultat ────────────────────────────────────────────
    private void ShowResult(bool isTest, FlowOutcome outcome)
    {
        var flow = outcome.Flow;
        ResultBanner.Visibility = Visibility.Visible;

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

        var mode = isTest ? "TEST (simule)" : $"REEL ({outcome.MesClientMode})";
        ResultDetailText.Text =
            $"Mode: {mode}  |  Action: {flow.Action}  |  Station: {flow.Station}  |  Serie: {flow.SerialNumber ?? "-"}\n" +
            $"MES: ErrorCode={flow.FinalResult.ErrorCode?.ToString() ?? "-"}  ErrorDescription={flow.FinalResult.ErrorDescription ?? "-"}";

        ResultRelayText.Text = flow.Relay switch
        {
            { } r when r.Ok => $"Relais: canal {r.Channel} declenche ({r.Verdict}), carte {r.BoardSerialNumber}{(r.Simulated ? " [simule]" : "")}.",
            { } r => $"Relais: ECHEC canal {r.Channel} ({r.Verdict}) - {r.Error}",
            null when flow.RelayNote is not null => flow.RelayNote,
            _ => "Relais: non configure.",
        };

        var engineLog = string.Join(Environment.NewLine, flow.Steps.Select(s => s.EngineLog).Where(l => !string.IsNullOrEmpty(l)));
        if (!string.IsNullOrEmpty(engineLog))
        {
            EngineLogExpander.Visibility = Visibility.Visible;
            EngineLogExpander.IsExpanded = !flow.Ok;
            EngineLogText.Text = engineLog;
        }
        else
        {
            EngineLogExpander.Visibility = Visibility.Collapsed;
            EngineLogText.Text = string.Empty;
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
        EngineLogExpander.Visibility = Visibility.Collapsed;
        EngineLogText.Text = string.Empty;
        ResultJsonText.Text = string.Empty;
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

    private void AddHistory(bool isTest, MesAction action, string station, string serial, FlowResult? flow, string? error)
    {
        _history.Insert(0, new HistoryEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Mode = isTest ? "Test" : "Reel",
            Action = action.ToString(),
            Station = station,
            Serial = string.IsNullOrWhiteSpace(serial) ? "-" : serial,
            Result = flow is null ? "ERREUR" : (flow.Ok ? "OK" : "ECHEC"),
            Relay = flow?.Relay is null ? "-" : $"canal {flow.Relay.Channel} ({flow.Relay.Verdict})",
            Detail = error ?? flow?.FinalResult.ErrorDescription,
        });
    }
}
