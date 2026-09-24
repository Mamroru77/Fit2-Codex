// System.IO and System.Windows are explicit on purpose: enabling UseWindowsForms replaces the
// SDK's default implicit-usings set with a Windows-specific one, and pulls in
// System.Windows.Forms, whose Application and MessageBox would otherwise collide with the WPF
// types used below.
using System.IO;
using System.Windows;
using CodexQuota.Codex.Account;
using CodexQuota.Codex.Process;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using CodexQuota.Desktop.Logging;
using CodexQuota.Desktop.Runtime;
using CodexQuota.Desktop.Tray;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Views;
using CodexQuota.Networking.Auth;
using CodexQuota.Networking.Discovery;
using CodexQuota.Networking.Pairing;
using CodexQuota.Networking.Security;
using CodexQuota.Storage.Database;
using CodexQuota.Storage.Devices;
using CodexQuota.Storage.History;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexQuota.Desktop;

/// <summary>
/// Composition root of the desktop Bridge: it builds the generic host, wires logging, state,
/// persistence and the Codex services, then hands control to the tray icon.
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayController? _tray;
    private StatusWindow? _window;
    private PairingQrWindow? _pairingWindow;
    private PairingViewModel? _pairingViewModel;
    private BridgeDatabase? _database;
    private BridgeIdentity? _bridgeIdentity;
    private string? _logsDirectory;
    private bool _exiting;

    /// <summary>
    /// The LAN endpoint the phone should connect to, once the API host is listening. It is
    /// <c>null</c> until then, which is a real state: without an eligible interface there is no
    /// address to put in a QR code.
    /// </summary>
    private BridgeEndpointAddress? _endpoint;

    /// <summary>Where a paired phone should connect. Set when the API host has started.</summary>
    internal sealed record BridgeEndpointAddress(string Host, int Port);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bridgeFolder = Path.Combine(localAppData, CodexRuntimeLocator.BridgeFolderName);
        _logsDirectory = Path.Combine(bridgeFolder, "logs");

        var builder = Host.CreateApplicationBuilder();

        // Every line reaching disk goes through the redactor.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RedactingFileLoggerProvider(_logsDirectory));

        var store = new InMemoryQuotaStateStore();
        var runtime = new BridgeRuntimeState();

        _database = new BridgeDatabase(
            new SqliteConnection($"Data Source={Path.Combine(bridgeFolder, "bridge.db")}"));
        var repository = new SqliteHistoryRepository(_database);
        var worker = new HistoryPersistenceWorker(repository);

        builder.Services.AddSingleton<IQuotaStateStore>(store);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton<IHistoryRepository>(repository);
        builder.Services.AddSingleton(worker);

        var viewModel = new StatusViewModel(store, runtime);
        builder.Services.AddSingleton(viewModel);

        var codexRuntime = CodexRuntimeLocator.ResolveRuntime(AppContext.BaseDirectory);
        var codexHome = CodexRuntimeLocator.ResolveCodexHome(localAppData);

        // The pairing trust anchor. It is created on first start and reused forever after, so a
        // paired phone keeps working across restarts and address changes.
        _bridgeIdentity = await new WindowsCngBridgeIdentityStore(
            Path.Combine(bridgeFolder, "identity")).GetOrCreateAsync(CancellationToken.None);

        var pairedDevices = new PairedDeviceRepository(_database);
        var deviceTokens = new DeviceTokenService(pairedDevices);
        var pairingService = new PairingService(deviceTokens);

        _pairingViewModel = new PairingViewModel(
            pairingService,
            _bridgeIdentity,
            () => _endpoint is { } endpoint
                ? PairingQrPayload.Create(
                    endpoint.Host,
                    endpoint.Port,
                    pairingId: string.Empty,
                    _bridgeIdentity.BridgeId,
                    _bridgeIdentity.SpkiSha256)
                : null);

        builder.Services.AddSingleton<IHostedService>(services =>
        {
            // Resolved here rather than before Build so the Bridge logs through the same redacting
            // provider the host owns. The child's own stderr is the only place its diagnostics
            // appear, and a child that survives termination has to be reported, not swallowed.
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CodexQuota.Codex");

            return new BridgeHostedService(
                codexRuntime,
                codexHome,
                store,
                runtime,
                worker,
                onStderrLine: line => logger.LogWarning("Codex app-server: {Line}", line),
                onDiagnostic: message => logger.LogError("{Message}", message));
        });

        _host = builder.Build();

        _window = new StatusWindow(viewModel);
        _pairingWindow = new PairingQrWindow(_pairingViewModel);
        _tray = new TrayController(
            openStatus: () =>
            {
                _window.Show();
                _window.Activate();
            },
            refreshNow: () => _ = SafeAsync(() => RefreshNowAsync()),
            pairDevice: () => _pairingWindow.BeginPairing(),
            login: () => _ = SafeAsync(LoginAsync),
            logout: () => _ = SafeAsync(() => LogoutAsync()),
            openLogs: OpenLogs,
            exit: () => _ = SafeAsync(ExitAsync));

        try
        {
            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"The Codex App Server could not be started.\n\n{exception.Message}",
                "Codex Quota Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        viewModel.Refresh();
        _window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(true);
            _host.Dispose();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(true);
        }

        _tray?.Dispose();

        base.OnExit(e);
    }

    private Task RefreshNowAsync()
    {
        var host = _host?.Services.GetService<IHostedService>() as BridgeHostedService;

        return host is null ? Task.CompletedTask : host.RefreshNowAsync(CancellationToken.None);
    }

    private async Task LoginAsync()
    {
        var host = _host?.Services.GetService<IHostedService>() as BridgeHostedService;
        var account = host?.Account;

        if (account is null)
        {
            return;
        }

        var authUrl = await account.StartChatGptLoginAsync(CancellationToken.None).ConfigureAwait(true);

        // The user completes authentication in the system browser; the Bridge never sees it.
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(authUrl.AbsoluteUri) { UseShellExecute = true });
    }

    private Task LogoutAsync()
    {
        var host = _host?.Services.GetService<IHostedService>() as BridgeHostedService;

        return host?.Account is { } account
            ? account.LogoutAsync(CancellationToken.None)
            : Task.CompletedTask;
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // Await the hosted service: the child process must be gone before the dispatcher closes.
        await _host!.StopAsync().ConfigureAwait(true);

        _pairingViewModel?.Dispose();
        _pairingWindow?.AllowClose();
        _pairingWindow?.Close();

        _window?.AllowClose();
        _window?.Close();

        Shutdown();
    }

    private void OpenLogs()
    {
        if (_logsDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(_logsDirectory);

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", _logsDirectory) { UseShellExecute = true });
    }

    private async Task SafeAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Codex Quota Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
