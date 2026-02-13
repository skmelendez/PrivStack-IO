using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Validates subscription status at startup via server call and exposes badge/detail state.
/// Falls back to local license data on network failure.
/// </summary>
public sealed class SubscriptionValidationService
{
    private static readonly ILogger _log = Log.ForContext<SubscriptionValidationService>();

    private readonly ILicensingService _licensing;
    private readonly PrivStackApiClient _apiClient;

    public string StatusBadgeText { get; private set; } = "";
    public string DetailPlanText { get; private set; } = "";
    public string DetailStatusText { get; private set; } = "";
    public string? DetailExpiresAt { get; private set; }
    public string ExpirationLabel { get; private set; } = "Expires";
    public int? TrialDaysRemaining { get; private set; }
    public bool IsPerpetual { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsBadgeVisible { get; private set; }

    /// <summary>
    /// Raised when state has been resolved (server or fallback). Subscribers must dispatch to UI thread.
    /// </summary>
    public event Action? StateChanged;

    public SubscriptionValidationService(ILicensingService licensing, PrivStackApiClient apiClient)
    {
        _licensing = licensing;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Validates the subscription once at startup. Safe to fire-and-forget.
    /// </summary>
    public async Task ValidateAsync()
    {
        try
        {
            var status = _licensing.GetLicenseStatus();
            var plan = _licensing.GetActivatedLicensePlan();

            _log.Information("[SubscriptionValidation] Local status={Status}, plan={Plan}", status, plan);

            // Not activated — hide badge, no server call
            if (status == LicenseStatus.NotActivated)
            {
                IsBadgeVisible = false;
                IsLoaded = true;
                StateChanged?.Invoke();
                return;
            }

            // Perpetual — set locally, skip server call
            if (plan == LicensePlan.Perpetual)
            {
                ApplyPerpetualState();
                IsLoaded = true;
                StateChanged?.Invoke();
                return;
            }

            // Trial / Monthly / Annual — validate with server
            var activation = _licensing.CheckLicense<ActivationInfo>();
            if (activation == null || string.IsNullOrEmpty(activation.LicenseKey))
            {
                _log.Warning("[SubscriptionValidation] No activation info found, falling back to local state");
                FallbackToLocalState(status, plan);
                IsLoaded = true;
                StateChanged?.Invoke();
                return;
            }

            var response = await _apiClient.ValidateLicenseAsync(activation.LicenseKey);

            if (!string.IsNullOrEmpty(response.Error))
            {
                _log.Warning("[SubscriptionValidation] Server error: {Error}, falling back to local", response.Error);
                FallbackToLocalState(status, plan);
            }
            else
            {
                ApplyServerResponse(response, plan);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[SubscriptionValidation] Unexpected error, falling back to local state");
            FallbackToLocalState(_licensing.GetLicenseStatus(), _licensing.GetActivatedLicensePlan());
        }

        IsLoaded = true;
        StateChanged?.Invoke();
    }

    private void ApplyPerpetualState()
    {
        IsPerpetual = true;
        IsBadgeVisible = true;
        StatusBadgeText = "Perpetual";
        DetailPlanText = "Perpetual License";
        DetailStatusText = "Active";
        DetailExpiresAt = null;
        ExpirationLabel = "Expires";
        TrialDaysRemaining = null;
    }

    private void ApplyServerResponse(LicenseValidationResponse response, LicensePlan localPlan)
    {
        IsBadgeVisible = true;

        // Determine plan display
        DetailPlanText = MapPlanDisplayText(response.Plan, localPlan);

        // Determine status display
        var statusLower = (response.Status ?? "").ToLowerInvariant();
        var isTrial = localPlan == LicensePlan.Trial ||
                      response.Plan.Equals("trial", StringComparison.OrdinalIgnoreCase);

        DetailStatusText = statusLower switch
        {
            "active" => isTrial ? "Trial" : "Active",
            "trial" => "Trial",
            "expired" => "Expired",
            "grace" => "Grace Period",
            _ => response.Status ?? "Unknown"
        };

        // Parse expiration
        if (!string.IsNullOrEmpty(response.ExpiresAt) && DateTimeOffset.TryParse(response.ExpiresAt, out var expiresAt))
        {
            DetailExpiresAt = expiresAt.LocalDateTime.ToString("MMMM d, yyyy");
            var daysLeft = (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalDays);

            if (isTrial)
            {
                TrialDaysRemaining = Math.Max(0, daysLeft);
                ExpirationLabel = "Trial Ends";
                StatusBadgeText = daysLeft > 0
                    ? $"Trial - {daysLeft} Day{(daysLeft == 1 ? "" : "s")} Remaining"
                    : "Trial Expired";
            }
            else
            {
                TrialDaysRemaining = null;
                ExpirationLabel = "Next Billing Date";
                StatusBadgeText = response.Valid ? "Active" : "Expired";
            }
        }
        else
        {
            DetailExpiresAt = null;
            TrialDaysRemaining = null;
            StatusBadgeText = isTrial ? "Trial" : (response.Valid ? "Active" : "Expired");
        }
    }

    private void FallbackToLocalState(LicenseStatus status, LicensePlan plan)
    {
        IsBadgeVisible = true;

        if (plan == LicensePlan.Perpetual)
        {
            ApplyPerpetualState();
            return;
        }

        var isTrial = plan == LicensePlan.Trial;
        DetailPlanText = MapPlanDisplayText(null, plan);
        DetailStatusText = status switch
        {
            LicenseStatus.Active => isTrial ? "Trial" : "Active",
            LicenseStatus.Grace => "Grace Period",
            LicenseStatus.Expired or LicenseStatus.ReadOnly => "Expired",
            _ => "Unknown"
        };

        // Try to read expiration from local activation info
        var activation = _licensing.CheckLicense<ActivationInfo>();
        if (activation?.ExpiresAt != null)
        {
            var expiresAt = activation.ExpiresAt.Value;
            DetailExpiresAt = expiresAt.LocalDateTime.ToString("MMMM d, yyyy");
            var daysLeft = (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalDays);

            if (isTrial)
            {
                TrialDaysRemaining = Math.Max(0, daysLeft);
                ExpirationLabel = "Trial Ends";
                StatusBadgeText = daysLeft > 0
                    ? $"Trial - {daysLeft} Day{(daysLeft == 1 ? "" : "s")} Remaining"
                    : "Trial Expired";
            }
            else
            {
                ExpirationLabel = "Next Billing Date";
                StatusBadgeText = status == LicenseStatus.Active ? "Active" : "Expired";
            }
        }
        else
        {
            DetailExpiresAt = null;
            StatusBadgeText = isTrial ? "Trial" : (status == LicenseStatus.Active ? "Active" : "Expired");
        }
    }

    private static string MapPlanDisplayText(string? serverPlan, LicensePlan localPlan)
    {
        if (!string.IsNullOrEmpty(serverPlan))
        {
            return serverPlan.ToLowerInvariant() switch
            {
                "monthly" => "Monthly Subscription",
                "annual" => "Annual Subscription",
                "perpetual" => "Perpetual License",
                "trial" => "Free Trial",
                _ => serverPlan
            };
        }

        return localPlan switch
        {
            LicensePlan.Monthly => "Monthly Subscription",
            LicensePlan.Annual => "Annual Subscription",
            LicensePlan.Perpetual => "Perpetual License",
            LicensePlan.Trial => "Free Trial",
            _ => "Unknown"
        };
    }
}
