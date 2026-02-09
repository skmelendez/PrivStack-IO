# FFI Layer

The `privstack-ffi` crate exposes the Rust core to non-Rust consumers through a C ABI. The desktop shell (.NET/Avalonia), and future iOS (Swift) and Android (JNI) clients all interact with the core through this boundary.

## Handle-Based API

All FFI operations go through a single `PrivStackHandle` that owns the entire runtime:

```rust
pub struct PrivStackHandle {
    db_path: String,
    entity_store: Arc<EntityStore>,
    event_store: Arc<EventStore>,
    entity_registry: EntityRegistry,
    peer_id: PeerId,
    runtime: Runtime,              // Tokio async runtime
    sync_engine: SyncEngine,
    p2p_transport: Option<Arc<TokioMutex<P2pTransport>>>,
    orchestrator_handle: Option<OrchestratorHandle>,
    vault_manager: Arc<VaultManager>,
    blob_store: BlobStore,
    plugin_host: PluginHostManager,
}
```

The handle is initialized once at startup and destroyed on shutdown. All exported functions operate on this handle.

## Exported Functions

### Lifecycle

| Function | Purpose |
|---|---|
| `privstack_init(db_path)` | Initialize the runtime with a database path |
| `privstack_shutdown()` | Tear down the runtime |
| `privstack_version() -> *const c_char` | Return the core version string |

### Authentication

| Function | Purpose |
|---|---|
| `privstack_auth_is_initialized() -> bool` | Check if a master password has been set |
| `privstack_auth_is_unlocked() -> bool` | Check if the vault is currently unlocked |
| `privstack_auth_initialize(password)` | Set the initial master password |
| `privstack_auth_unlock(password)` | Unlock with password |
| `privstack_auth_lock()` | Lock the vault |
| `privstack_auth_change_password(old, new)` | Change the master password |

### Vault

| Function | Purpose |
|---|---|
| `privstack_vault_create(vault_id)` | Create a new named vault |
| `privstack_vault_initialize(vault_id, password)` | Set vault password |
| `privstack_vault_unlock(vault_id, password)` | Unlock a specific vault |
| `privstack_vault_lock(vault_id)` | Lock a specific vault |
| `privstack_vault_is_initialized(vault_id) -> bool` | Check vault state |
| `privstack_vault_is_unlocked(vault_id) -> bool` | Check vault state |

### Generic Execute

```c
const char* privstack_execute(const char* request_json);
```

This is the primary endpoint for all domain operations. The request is a JSON object:

```json
{
  "plugin_id": "privstack.notes",
  "action": "create",
  "entity_type": "page",
  "entity_id": null,
  "payload": "{\"title\": \"Hello\", \"body\": \"World\"}",
  "parameters": {}
}
```

Actions: `create`, `read`, `update`, `delete`, `query`, `command`.

The response is also JSON, containing the result or error. This design keeps the FFI surface small — one generic endpoint handles all CRUD and query operations for all entity types.

### Entity Registration

```c
int privstack_register_entity_type(const char* schema_json);
```

Registers an entity schema so the core knows how to index and merge entities of that type.

### Search

```c
const char* privstack_search(const char* query_json);
```

Full-text and field-based search across all entity types.

### Plugin Management

| Function | Purpose |
|---|---|
| `privstack_plugin_list() -> *const c_char` | List loaded plugins (JSON) |
| `privstack_plugin_load(id, path, ...) -> *const c_char` | Load a Wasm plugin |
| `privstack_plugin_unload(id) -> PrivStackError` | Unload a plugin |
| `privstack_plugin_get_view_state(id) -> *const c_char` | Get plugin's UI state (JSON component tree) |
| `privstack_plugin_send_command(id, cmd, args) -> *const c_char` | Send a command to a plugin |
| `privstack_plugin_get_metadata(id) -> *const c_char` | Get plugin metadata |

### Sync

| Function | Purpose |
|---|---|
| `privstack_sync_start()` | Begin P2P sync |
| `privstack_sync_poll_events() -> *const c_char` | Poll for sync status changes |

### Memory Management

```c
void privstack_free_string(const char* ptr);
void privstack_free_bytes(const uint8_t* data, size_t len);
```

All strings returned from Rust must be freed by the caller using these functions. The .NET P/Invoke bindings handle this automatically.

## Error Codes

```c
enum PrivStackError {
    Ok                = 0,
    NullPointer       = 1,
    InvalidUtf8       = 2,
    JsonError         = 3,
    StorageError      = 4,
    NotFound          = 5,
    // ...
    VaultLocked       = 21,
    VaultNotFound     = 22,
    PluginError       = 23,
};
```

## .NET P/Invoke Bindings

The desktop shell uses .NET's `LibraryImport` attribute (source-generated P/Invoke) to call the FFI functions:

```csharp
[LibraryImport("privstack_ffi")]
internal static partial int privstack_init(
    [MarshalAs(UnmanagedType.LPUTF8Str)] string dbPath);
```

The `PrivStackService` class wraps all P/Invoke calls and implements multiple service interfaces (`IPrivStackNative`, `IPrivStackRuntime`, `IAuthService`, `ISyncService`, `IPairingService`, `ICloudStorageService`, `ILicensingService`).

The `SdkHost` class sits between plugins and `PrivStackService`, providing the `IPrivStackSdk` interface and handling:

- JSON serialization/deserialization of SDK messages
- Thread-safe access via `ReaderWriterLockSlim` (blocks during workspace switches)
- Timeout enforcement (5-second lock acquisition timeout)

## Platform Libraries

| Platform | Library |
|---|---|
| macOS arm64 | `libprivstack_ffi.dylib` |
| macOS x64 | `libprivstack_ffi.dylib` |
| Windows x64 | `privstack_ffi.dll` |
| Linux x64 | `libprivstack_ffi.so` |

The correct library is selected at build time via conditional `ItemGroup` entries in the `.csproj` based on the runtime identifier.
