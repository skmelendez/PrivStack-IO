namespace PrivStack.Desktop.Services.Update;

/// <summary>
/// Returns the correct IUpdateInstaller for the current platform and install format.
/// </summary>
public static class UpdateInstallerFactory
{
    public static IUpdateInstaller Create()
    {
        var format = PlatformDetector.DetectCurrentInstallFormat();

        return format switch
        {
            "deb" => new LinuxDebInstaller(),
            "exe" or "msix" => new WindowsExeInstaller(),
            "dmg" => new MacOsDmgInstaller(),
            _ => new LinuxDebInstaller()
        };
    }
}
