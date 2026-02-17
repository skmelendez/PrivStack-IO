using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;

namespace PrivStack.Desktop.ViewModels;

public partial class SyncViewModel : ViewModelBase
{
    private readonly ISyncService _syncService;
    private readonly IPairingService _pairingService;
    private readonly IUiDispatcher _dispatcher;
    private System.Timers.Timer? _refreshTimer;
    private int _refreshRunning; // re-entrancy guard

    [ObservableProperty]
    private string _statusMessage = "Sync";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusText))]
    [NotifyPropertyChangedFor(nameof(SyncButtonText))]
    [NotifyPropertyChangedFor(nameof(StatusIndicatorColor))]
    private bool _isSyncRunning;

    [ObservableProperty]
    private string _localPeerId = string.Empty;

    [ObservableProperty]
    private int _peerCount;

    [ObservableProperty]
    private int _trustedPeerCount;

    // Tab state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairingTab))]
    private bool _isStatusTab = true;

    public bool IsPairingTab => !IsStatusTab;

    // Connected trusted peers for status display
    public ObservableCollection<TrustedPeer> ConnectedTrustedPeers { get; } = [];

    public ObservableCollection<DiscoveredPeer> DiscoveredPeers { get; } = [];

    // Pairing ViewModel
    public SyncPairingViewModel PairingViewModel { get; }

    public SyncViewModel(ISyncService syncService, IPairingService pairingService, IUiDispatcher dispatcher)
    {
        _syncService = syncService;
        _pairingService = pairingService;
        _dispatcher = dispatcher;
        PairingViewModel = new SyncPairingViewModel(syncService, pairingService, dispatcher, App.Services.GetRequiredService<IAppSettingsService>());
    }

    public void StartRefreshTimer()
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();

        _refreshTimer = new System.Timers.Timer(10_000); // Refresh every 10 seconds
        _refreshTimer.AutoReset = true;
        _refreshTimer.Elapsed += (_, _) => RefreshStatusFromBackground();
        _refreshTimer.Start();
    }

    public void StopRefreshTimer()
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    [RelayCommand]
    public async Task ShowStatusTab()
    {
        IsStatusTab = true;
        await RefreshStatus();
    }

    [RelayCommand]
    public void ShowPairingTab()
    {
        IsStatusTab = false;
    }

    [RelayCommand]
    public async Task RefreshStatus()
    {
        try
        {
            var (isRunning, peerId, peers, trustedPeers) = await Task.Run(() => (
                _syncService.IsSyncRunning(),
                _syncService.GetLocalPeerId(),
                _syncService.GetDiscoveredPeers<DiscoveredPeer>(),
                _pairingService.GetTrustedPeers()
            ));

            IsSyncRunning = isRunning;
            LocalPeerId = peerId;
            PeerCount = peers.Count;

            DiscoveredPeers.Clear();
            foreach (var peer in peers)
                DiscoveredPeers.Add(peer);

            TrustedPeerCount = trustedPeers.Count;
            ConnectedTrustedPeers.Clear();
            foreach (var peer in trustedPeers)
                ConnectedTrustedPeers.Add(peer);

            StatusMessage = IsSyncRunning
                ? $"Sync running - {TrustedPeerCount} trusted device(s)"
                : "Sync stopped";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Called from the timer's thread pool thread. Runs FFI off the UI thread,
    /// then dispatches only the UI property updates to the dispatcher.
    /// </summary>
    private void RefreshStatusFromBackground()
    {
        // Skip if a previous tick is still running (mutex contention)
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0) return;
        try
        {
            var isRunning = _syncService.IsSyncRunning();
            var peerId = _syncService.GetLocalPeerId();
            var peers = _syncService.GetDiscoveredPeers<DiscoveredPeer>();
            var trustedPeers = _pairingService.GetTrustedPeers();

            _dispatcher.Post(() =>
            {
                IsSyncRunning = isRunning;
                LocalPeerId = peerId;
                PeerCount = peers.Count;

                DiscoveredPeers.Clear();
                foreach (var peer in peers)
                    DiscoveredPeers.Add(peer);

                TrustedPeerCount = trustedPeers.Count;
                ConnectedTrustedPeers.Clear();
                foreach (var peer in trustedPeers)
                    ConnectedTrustedPeers.Add(peer);

                StatusMessage = isRunning
                    ? $"Sync running - {trustedPeers.Count} trusted device(s)"
                    : "Sync stopped";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
        }
    }

    [RelayCommand]
    public async Task ToggleSync()
    {
        try
        {
            if (IsSyncRunning)
            {
                StatusMessage = "Stopping sync...";
                await Task.Run(() => _syncService.StopSync());
                StatusMessage = "Sync stopped";
            }
            else
            {
                StatusMessage = "Starting sync...";
                await Task.Run(() => _syncService.StartSync());
                StatusMessage = "Sync started - discovering peers...";
            }
            await RefreshStatus();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StartSync()
    {
        try
        {
            StatusMessage = "Starting sync...";
            await Task.Run(() => _syncService.StartSync());
            StatusMessage = "Sync started - discovering peers...";
            await RefreshStatus();
            StartRefreshTimer();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StopSync()
    {
        try
        {
            StatusMessage = "Stopping sync...";
            await Task.Run(() => _syncService.StopSync());
            StatusMessage = "Sync stopped";
            await RefreshStatus();
            StopRefreshTimer();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets a short version of the local peer ID for display.
    /// </summary>
    public string ShortPeerId => LocalPeerId.Length > 8 ? $"{LocalPeerId[..8]}..." : LocalPeerId;

    /// <summary>
    /// Gets the sync status text for display.
    /// </summary>
    public string SyncStatusText => IsSyncRunning ? "Running" : "Stopped";

    /// <summary>
    /// Gets the sync button text.
    /// </summary>
    public string SyncButtonText => IsSyncRunning ? "Stop Sync" : "Start Sync";

    /// <summary>
    /// Gets the status indicator brush.
    /// </summary>
    public IBrush StatusIndicatorColor => IsSyncRunning
        ? ThemeHelper.GetBrush("ThemeSuccessBrush", Brushes.Green)
        : ThemeHelper.GetBrush("ThemeTextMutedBrush", Brushes.Gray);
}
