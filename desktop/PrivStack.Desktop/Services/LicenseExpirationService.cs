using System.Diagnostics;
using PrivStack.Desktop.Native;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Tracks license expiration state and exposes it for the UI banner.
/// </summary>
public sealed class LicenseExpirationService
{
    private static readonly ILogger _log = Log.ForContext<LicenseExpirationService>();

    public bool IsExpired { get; private set; }

    /// <summary>
    /// Raised on the calling thread when the expiration state changes.
    /// Subscribers must dispatch to UI thread if needed.
    /// </summary>
    public event Action? ExpiredChanged;

    /// <summary>
    /// Checks the current license status and updates <see cref="IsExpired"/>.
    /// Call after native runtime initialization.
    /// </summary>
    public void CheckLicenseStatus(ILicensingService licensing)
    {
        var status = licensing.GetLicenseStatus();
        var wasExpired = IsExpired;
        IsExpired = status is LicenseStatus.ReadOnly or LicenseStatus.Expired or LicenseStatus.NotActivated;

        if (IsExpired)
            _log.Warning("License status is {Status} — app is in read-only mode", status);

        if (IsExpired != wasExpired)
            ExpiredChanged?.Invoke();
    }

    /// <summary>
    /// Called by SdkHost when a mutation is blocked by the Rust core.
    /// </summary>
    public void OnMutationBlocked()
    {
        if (IsExpired) return;

        IsExpired = true;
        _log.Warning("Mutation blocked by Rust core — switching to read-only mode");
        ExpiredChanged?.Invoke();
    }

    /// <summary>
    /// Opens the PrivStack pricing page in the default browser.
    /// </summary>
    public static void OpenPricingPage()
    {
        Process.Start(new ProcessStartInfo("https://privstack.io/pricing") { UseShellExecute = true });
    }
}
