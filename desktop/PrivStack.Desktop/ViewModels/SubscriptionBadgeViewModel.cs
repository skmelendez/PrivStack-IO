using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Services;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Exposes subscription badge state and modal commands for XAML binding.
/// </summary>
public partial class SubscriptionBadgeViewModel : ViewModelBase
{
    private readonly SubscriptionValidationService _service;

    [ObservableProperty] private string _badgeText = "";
    [ObservableProperty] private bool _isBadgeVisible;
    [ObservableProperty] private bool _isModalOpen;
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _expiresAtText = "";
    [ObservableProperty] private bool _hasExpiration;
    [ObservableProperty] private bool _isPerpetual;
    [ObservableProperty] private string _expirationLabel = "Expires";

    public SubscriptionBadgeViewModel(SubscriptionValidationService service)
    {
        _service = service;
        _service.StateChanged += OnStateChanged;

        // If already loaded (e.g., VM created after validation), sync immediately
        if (_service.IsLoaded) SyncFromService();
    }

    private void OnStateChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(SyncFromService);
    }

    private void SyncFromService()
    {
        BadgeText = _service.StatusBadgeText;
        IsBadgeVisible = _service.IsBadgeVisible;
        PlanText = _service.DetailPlanText;
        StatusText = _service.DetailStatusText;
        ExpiresAtText = _service.DetailExpiresAt ?? "";
        HasExpiration = _service.DetailExpiresAt != null;
        IsPerpetual = _service.IsPerpetual;
        ExpirationLabel = _service.ExpirationLabel;
    }

    [RelayCommand]
    private void OpenModal() => IsModalOpen = true;

    [RelayCommand]
    private void CloseModal() => IsModalOpen = false;

    [RelayCommand]
    private void OpenAccountPage()
    {
        Process.Start(new ProcessStartInfo("https://privstack.io/account") { UseShellExecute = true });
    }
}
