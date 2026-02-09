# SDKs

PrivStack provides SDKs at two levels: a Rust-side Wasm plugin SDK for plugin authors, and a .NET-side SDK for native desktop plugins.

## Wasm Plugin SDK (Rust)

The `privstack-plugin-sdk` crate is the guest-side SDK for writing plugins that compile to WebAssembly.

### WIT Bindings

The SDK uses `wit-bindgen` to generate bindings from WebAssembly Interface Types (WIT) definitions. This provides a language-agnostic interface contract between the plugin (guest) and the host.

### Plugin Trait

Plugin authors implement the `Plugin` trait:

```rust
pub trait Plugin {
    fn metadata(&self) -> PluginMetadata;
    fn entity_schemas(&self) -> Vec<EntitySchema>;
    fn initialize(&mut self) -> bool;
    fn dispose(&mut self) -> bool;
}
```

### Registration Macro

The `privstack_wasm_export!` macro generates the Wasm export boilerplate:

```rust
privstack_wasm_export!(MyPlugin);
```

This exports the required WIT interface functions and wires them to the plugin implementation.

### Optional Capabilities

Plugins can implement additional traits to participate in shell features:

| Capability | Interface | Purpose |
|---|---|---|
| Linkable items | `LinkableItemProvider` | Provide items for cross-entity linking and backlinks |
| Commands | `CommandProvider` | Register commands in the command palette |
| Search | `SearchProvider` | Custom search result providers |

### Host Imports

Plugins can call back into the host for data operations. The host provides:

- Entity CRUD (create, read, update, delete) — scoped to the plugin's declared entity types
- Blob storage operations (read, write, delete)
- Full-text search queries
- Event publishing
- HTTP requests (gated by permission — requires explicit grant in plugin policy)

## .NET Plugin SDK

The `PrivStack.Sdk` project defines the interfaces and base classes for native desktop plugins.

### IAppPlugin Interface

```csharp
public interface IAppPlugin : IDisposable
{
    PluginMetadata Metadata { get; }
    NavigationItem? NavigationItem { get; }
    ICommandProvider? CommandProvider { get; }
    PluginState State { get; }
    IReadOnlyList<EntitySchema> EntitySchemas { get; }

    Task<bool> InitializeAsync(IPluginHost host, CancellationToken ct);
    void Activate();
    void Deactivate();
    ViewModelBase CreateViewModel();
    void ResetViewModel();
    Task OnNavigatedToAsync(CancellationToken ct);
    void OnNavigatedFrom();
}
```

### Plugin Lifecycle

```
Discovered → Initializing → Initialized → Active ⇄ Deactivated
                                ↓
                              Failed
```

1. **Discovered** — the plugin assembly or Wasm file was found and the class was instantiated
2. **Initializing** — `InitializeAsync()` is being called
3. **Initialized** — ready but not yet visible in the UI
4. **Active** — visible in the navigation sidebar, receiving navigation events
5. **Deactivated** — disabled by the user or the system
6. **Failed** — initialization threw an error

### PluginBase Convenience Class

`PluginBase<TViewModel>` provides a standard implementation with:

- Lifecycle state management
- Lazy ViewModel creation and caching
- Virtual methods for override: `OnInitializeAsync()`, `OnActivate()`, `OnDeactivate()`, `OnDispose()`

### IPluginHost

The host services provided to each plugin during initialization:

```csharp
public interface IPluginHost
{
    IPrivStackSdk Sdk { get; }               // Data operations (CRUD, search)
    INavigationService Navigation { get; }   // Switch to another plugin tab
    IInfoPanelService InfoPanel { get; }     // Report selected entity for backlinks
    ICapabilityBroker Capabilities { get; }  // Discover other plugins' capabilities
    IPluginSettings Settings { get; }        // Plugin-scoped key-value settings
    IUiDispatcher UiDispatcher { get; }      // Marshal to UI thread
}
```

### IPrivStackSdk

The primary data interface available to plugins:

```csharp
public interface IPrivStackSdk
{
    Task<SdkResponse<T>> SendAsync<T>(SdkMessage message);
    Task<SdkResponse<SearchResult>> SearchAsync(string query);
    // Vault operations
    // Blob operations
    // Sync operations
}
```

All data calls serialize to JSON, cross the FFI boundary, and return JSON responses.

### SdkMessage Structure

```csharp
public class SdkMessage
{
    public string PluginId { get; set; }
    public string Action { get; set; }       // "create", "read", "update", "delete", "query", "command"
    public string EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Payload { get; set; }     // JSON
    public Dictionary<string, string>? Parameters { get; set; }
}
```

### Capability Broker

Plugins discover each other's capabilities through the `ICapabilityBroker`:

| Capability | Interface | Purpose |
|---|---|---|
| Timer | `ITimerBehavior` | Single timer functionality |
| Multi-timer | `IMultiTimerBehavior` | Concurrent timer support |
| Reminders | `IReminderProvider` | Schedule OS notifications |
| Linkable items | `ILinkableItemProvider` | Backlink support |
| Deep links | `IDeepLinkTarget` | URL-based entity navigation |
| Seed data | `ISeedDataProvider` | Sample data population |
| Shutdown | `IShutdownAware` | Cleanup on app exit |
| Storage | `IStorageProvider` | Plugin-local storage |
| Data metrics | `IDataMetricsProvider` | Statistics for dashboard |

### View Resolution

Native plugins create ViewModels that are resolved to Views by convention:

```
FooViewModel → FooView (same namespace/assembly)
```

The `ViewLocator` in the desktop shell handles this mapping automatically.

### Wasm Plugin Proxy

Wasm plugins are wrapped in a `WasmPluginProxy` that presents the standard `IAppPlugin` interface. The proxy:

- Reads metadata from Rust via FFI
- Creates a `WasmPluginView` that uses the adaptive view renderer to display JSON component trees
- Routes commands from UI controls back through FFI (`privstack_plugin_send_command`)
- Loads command palettes from sidecar `command_palettes.json` files
