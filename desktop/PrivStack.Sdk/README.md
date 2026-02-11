# PrivStack.Sdk

SDK for building PrivStack plugins. Provides base classes, entity schemas, messaging, and capability interfaces used by the desktop shell's plugin system.

## What's included

- **Plugin base classes** — `PluginBase` and lifecycle hooks for initialization, activation, and deactivation
- **Entity schemas** — Define indexed fields, merge strategies, and domain handlers for your plugin's data types
- **Messaging** — Inter-plugin and plugin-to-host communication via the capability broker
- **Capability interfaces** — Declare and discover capabilities like timers, reminders, search providers, and deep links

## Usage

```xml
<PackageReference Include="PrivStack.Sdk" Version="1.5.1" />
```

If your plugin includes UI, also reference [PrivStack.UI.Adaptive](https://www.nuget.org/packages/PrivStack.UI.Adaptive) for themed Avalonia controls.

## Documentation

- [PrivStack website](https://privstack.io)
- [Desktop SDKs documentation](https://github.com/PrivStackApp/PrivStack-IO/wiki/sdks)

## License

[PolyForm Internal Use License 1.0.0](https://github.com/PrivStackApp/PrivStack-IO/blob/main/LICENSE)
