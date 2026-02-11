//! C ABI exports for PrivStack cross-platform integration.
//!
//! This crate exposes the Rust core as a C-compatible library for:
//! - .NET/Avalonia desktop app (P/Invoke)
//! - Android (JNI)
//! - iOS (Swift C interop)
//!
//! All functions use C-compatible types and handle errors via return codes.
//! Zero domain logic — plugins consume generic vault, blob, and entity APIs.

mod datasets;

use privstack_blobstore::BlobStore;
use privstack_license::{
    Activation, ActivationStore, DeviceFingerprint, DeviceInfo, LicenseError, LicenseKey,
    LicensePlan, LicenseStatus,
};
use privstack_model::{Entity, EntitySchema, PluginDomainHandler};
#[cfg(feature = "wasm-plugins")]
use privstack_plugin_host::PluginHostManager;
use privstack_storage::{EntityStore, EventStore};
use privstack_sync::{
    cloud::{CloudStorage, GoogleDriveConfig, GoogleDriveStorage, ICloudConfig, ICloudStorage},
    create_personal_orchestrator,
    pairing::{PairingManager, SyncCode},
    Keypair, OrchestratorConfig, OrchestratorHandle, P2pConfig, P2pTransport,
    PersonalSyncPolicy, SyncCommand, SyncConfig, SyncEngine, SyncEvent, SyncTransport,
};
use privstack_types::{EntityId, Event, PeerId};
use privstack_vault::VaultManager;
use serde::{Deserialize, Serialize};
use std::ffi::{c_char, c_int, CStr, CString};
use std::path::Path;
use std::sync::{Arc, Mutex};
use tokio::runtime::Runtime;
use tokio::sync::{mpsc, Mutex as TokioMutex};
use uuid::Uuid;

/// Error codes returned by FFI functions.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PrivStackError {
    /// Operation succeeded.
    Ok = 0,
    /// Null pointer argument.
    NullPointer = 1,
    /// Invalid UTF-8 string.
    InvalidUtf8 = 2,
    /// JSON serialization error.
    JsonError = 3,
    /// Storage error.
    StorageError = 4,
    /// Document not found.
    NotFound = 5,
    /// Handle not initialized.
    NotInitialized = 6,
    /// Sync not running.
    SyncNotRunning = 7,
    /// Sync already running.
    SyncAlreadyRunning = 8,
    /// Network/sync error.
    SyncError = 9,
    /// Peer not found.
    PeerNotFound = 10,
    /// Authentication error.
    AuthError = 11,
    /// Cloud storage error.
    CloudError = 12,
    /// License key format invalid.
    LicenseInvalidFormat = 13,
    /// License signature failed.
    LicenseInvalidSignature = 14,
    /// License expired.
    LicenseExpired = 15,
    /// License not activated.
    LicenseNotActivated = 16,
    /// License activation failed.
    LicenseActivationFailed = 17,
    /// Invalid sync code format.
    InvalidSyncCode = 18,
    /// Peer not trusted.
    PeerNotTrusted = 19,
    /// Pairing error.
    PairingError = 20,
    /// Vault is locked.
    VaultLocked = 21,
    /// Vault not found.
    VaultNotFound = 22,
    /// Plugin error (load, unload, or route failure).
    PluginError = 23,
    /// Plugin not found.
    PluginNotFound = 24,
    /// Plugin permission denied.
    PluginPermissionDenied = 25,
    /// Vault already initialized.
    VaultAlreadyInitialized = 26,
    /// Password too short.
    PasswordTooShort = 27,
    /// Invalid argument.
    InvalidArgument = 28,
    /// Unknown error.
    Unknown = 99,
}

/// Registry of entity schemas and optional domain handlers.
pub struct EntityRegistry {
    schemas: std::collections::HashMap<String, EntitySchema>,
    handlers: std::collections::HashMap<String, Box<dyn PluginDomainHandler>>,
}

impl EntityRegistry {
    fn new() -> Self {
        Self {
            schemas: std::collections::HashMap::new(),
            handlers: std::collections::HashMap::new(),
        }
    }

    fn register_schema(&mut self, schema: EntitySchema) {
        self.schemas.insert(schema.entity_type.clone(), schema);
    }

    #[allow(dead_code)]
    fn register_handler(&mut self, entity_type: String, handler: Box<dyn PluginDomainHandler>) {
        self.handlers.insert(entity_type, handler);
    }

    fn get_schema(&self, entity_type: &str) -> Option<&EntitySchema> {
        self.schemas.get(entity_type)
    }

    fn get_handler(&self, entity_type: &str) -> Option<&dyn PluginDomainHandler> {
        self.handlers.get(entity_type).map(|h| h.as_ref())
    }

    fn has_schema(&self, entity_type: &str) -> bool {
        self.schemas.contains_key(entity_type)
    }
}

/// Opaque handle to the PrivStack runtime.
pub struct PrivStackHandle {
    /// The database path passed to privstack_init (needed for deriving keypair file path).
    db_path: String,
    entity_store: Arc<EntityStore>,
    #[allow(dead_code)]
    event_store: Arc<EventStore>,
    entity_registry: EntityRegistry,
    peer_id: PeerId,
    runtime: Runtime,
    #[allow(dead_code)]
    sync_engine: SyncEngine,
    p2p_transport: Option<Arc<TokioMutex<P2pTransport>>>,
    orchestrator_handle: Option<OrchestratorHandle>,
    sync_event_rx: Option<mpsc::Receiver<SyncEvent>>,
    pairing_manager: Arc<std::sync::Mutex<PairingManager>>,
    personal_policy: Option<Arc<PersonalSyncPolicy>>,
    device_name: String,
    google_drive: Option<GoogleDriveStorage>,
    icloud: Option<ICloudStorage>,
    activation_store: ActivationStore,
    // Generic capabilities — no domain logic
    vault_manager: Arc<VaultManager>,
    blob_store: BlobStore,
    // Tabular datasets (unencrypted DuckDB for SQL queries)
    dataset_store: Option<privstack_datasets::DatasetStore>,
    // Wasm plugin host manager
    #[cfg(feature = "wasm-plugins")]
    plugin_host: PluginHostManager,
}

/// Discovered peer info for JSON serialization.
#[derive(Serialize)]
struct DiscoveredPeerInfo {
    peer_id: String,
    device_name: Option<String>,
    discovery_method: String,
    addresses: Vec<String>,
}

/// Sync status for JSON serialization.
#[derive(Serialize)]
struct SyncStatus {
    running: bool,
    local_peer_id: String,
    discovered_peers: Vec<DiscoveredPeerInfo>,
}

/// Sync event DTO for JSON serialization.
#[derive(Serialize)]
struct SyncEventDto {
    event_type: String,
    peer_id: Option<String>,
    device_name: Option<String>,
    entity_id: Option<String>,
    events_sent: Option<usize>,
    events_received: Option<usize>,
    error: Option<String>,
    entity_type: Option<String>,
    json_data: Option<String>,
}

impl From<SyncEvent> for SyncEventDto {
    fn from(event: SyncEvent) -> Self {
        match event {
            SyncEvent::PeerDiscovered {
                peer_id,
                device_name,
            } => SyncEventDto {
                event_type: "peer_discovered".to_string(),
                peer_id: Some(peer_id.to_string()),
                device_name,
                entity_id: None,
                events_sent: None,
                events_received: None,
                error: None,
                entity_type: None,
                json_data: None,
            },
            SyncEvent::SyncStarted { peer_id } => SyncEventDto {
                event_type: "sync_started".to_string(),
                peer_id: Some(peer_id.to_string()),
                device_name: None,
                entity_id: None,
                events_sent: None,
                events_received: None,
                error: None,
                entity_type: None,
                json_data: None,
            },
            SyncEvent::SyncCompleted {
                peer_id,
                events_sent,
                events_received,
            } => SyncEventDto {
                event_type: "sync_completed".to_string(),
                peer_id: Some(peer_id.to_string()),
                device_name: None,
                entity_id: None,
                events_sent: Some(events_sent),
                events_received: Some(events_received),
                error: None,
                entity_type: None,
                json_data: None,
            },
            SyncEvent::SyncFailed { peer_id, error } => SyncEventDto {
                event_type: "sync_failed".to_string(),
                peer_id: Some(peer_id.to_string()),
                device_name: None,
                entity_id: None,
                events_sent: None,
                events_received: None,
                error: Some(error),
                entity_type: None,
                json_data: None,
            },
            SyncEvent::EntityUpdated { entity_id } => SyncEventDto {
                event_type: "entity_updated".to_string(),
                peer_id: None,
                device_name: None,
                entity_id: Some(entity_id.to_string()),
                events_sent: None,
                events_received: None,
                error: None,
                entity_type: None,
                json_data: None,
            },
        }
    }
}

/// Cloud file info for JSON serialization.
#[derive(Serialize)]
struct CloudFileInfo {
    id: String,
    name: String,
    path: String,
    size: u64,
    modified_at_ms: i64,
    content_hash: Option<String>,
}

/// Cloud provider type.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CloudProvider {
    GoogleDrive = 0,
    ICloud = 1,
}

/// Global handle storage (single instance for now).
static HANDLE: Mutex<Option<PrivStackHandle>> = Mutex::new(None);

/// Acquire the HANDLE lock, recovering from poison if a prior
/// `catch_unwind` caught a DuckDB panic while the lock was held.
pub(crate) fn lock_handle() -> std::sync::MutexGuard<'static, Option<PrivStackHandle>> {
    HANDLE.lock().unwrap_or_else(|poisoned| {
        eprintln!("[FFI] recovering from poisoned HANDLE mutex");
        poisoned.into_inner()
    })
}

// ============================================================================
// Core Functions
// ============================================================================

/// Initializes the PrivStack runtime.
///
/// # Safety
/// - `db_path` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_init(db_path: *const c_char) -> PrivStackError { unsafe {
    let _ = tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .with_writer(std::io::stderr)
        .try_init();

    if db_path.is_null() {
        return PrivStackError::NullPointer;
    }

    let path = match CStr::from_ptr(db_path).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    #[cfg(feature = "wasm-plugins")]
    { init_with_plugin_host_builder(path, |es, ev| PluginHostManager::new(es, ev)) }
    #[cfg(not(feature = "wasm-plugins"))]
    { init_core(path) }
}}

/// Loads an existing peer ID from disk, or generates and saves a new one.
/// The peer ID file lives alongside the database (e.g. `data.peer_id`).
/// For `:memory:` databases, a fresh ID is generated every time.
fn load_or_create_peer_id(db_path: &str) -> PeerId {
    if db_path == ":memory:" {
        return PeerId::new();
    }

    let peer_id_path = Path::new(db_path).with_extension("peer_id");

    // Try to read existing peer ID
    if let Ok(contents) = std::fs::read_to_string(&peer_id_path) {
        let trimmed = contents.trim();
        if let Ok(uuid) = Uuid::parse_str(trimmed) {
            eprintln!("[FFI] Loaded existing peer ID: {uuid}");
            return PeerId::from_uuid(uuid);
        }
        eprintln!("[FFI] Corrupt peer_id file at {}, generating new one", peer_id_path.display());
    }

    // Generate new peer ID and persist it
    let peer_id = PeerId::new();
    if let Err(e) = std::fs::write(&peer_id_path, peer_id.to_string()) {
        eprintln!("[FFI] Failed to persist peer ID to {}: {e}", peer_id_path.display());
    } else {
        eprintln!("[FFI] Generated and saved new peer ID: {peer_id}");
    }
    peer_id
}

/// Loads an existing libp2p keypair from disk, or generates and saves a new one.
/// The keypair file lives alongside the database (e.g. `data.keypair`).
/// For `:memory:` databases, a fresh keypair is generated every time.
fn load_or_create_keypair(db_path: &str) -> Keypair {
    if db_path == ":memory:" {
        return Keypair::generate_ed25519();
    }

    let keypair_path = Path::new(db_path).with_extension("keypair");

    // Try to read existing keypair
    if let Ok(bytes) = std::fs::read(&keypair_path) {
        if let Ok(kp) = Keypair::from_protobuf_encoding(&bytes) {
            eprintln!("[FFI] Loaded existing libp2p keypair from {}", keypair_path.display());
            return kp;
        }
        eprintln!("[FFI] Corrupt keypair file at {}, generating new one", keypair_path.display());
    }

    // Generate new keypair and persist it
    let keypair = Keypair::generate_ed25519();
    match keypair.to_protobuf_encoding() {
        Ok(bytes) => {
            if let Err(e) = std::fs::write(&keypair_path, &bytes) {
                eprintln!("[FFI] Failed to persist keypair to {}: {e}", keypair_path.display());
            } else {
                eprintln!("[FFI] Generated and saved new libp2p keypair to {}", keypair_path.display());
            }
        }
        Err(e) => {
            eprintln!("[FFI] Failed to encode keypair: {e}");
        }
    }
    keypair
}

/// Core init logic — sets up vault, blob, entity, event stores, runtime, sync engine.
/// Used directly when wasm-plugins feature is disabled.
#[cfg(not(feature = "wasm-plugins"))]
fn init_core(path: &str) -> PrivStackError {
    let peer_id = load_or_create_peer_id(path);

    // Create vault manager (encrypted blob storage)
    let vault_db_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("vault.duckdb")
    };
    eprintln!("[FFI] Opening vault DB: {}", vault_db_path.display());
    let vault_manager = match VaultManager::open(&vault_db_path) {
        Ok(vm) => {
            eprintln!("[FFI] Vault DB opened OK");
            Arc::new(vm)
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open vault DB: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    // Create blob store with vault-backed encryption
    let blob_db_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("blobs.duckdb")
    };
    eprintln!("[FFI] Opening blob store: {}", blob_db_path.display());
    let blob_store = match BlobStore::open_with_encryptor(
        &blob_db_path,
        vault_manager.clone() as Arc<dyn privstack_crypto::DataEncryptor>,
    ) {
        Ok(bs) => {
            eprintln!("[FFI] Blob store opened OK");
            bs
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open blob store: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    // Create generic entity store with vault-backed encryption
    let entity_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("entities.duckdb")
    };
    eprintln!("[FFI] Opening entity store: {}", entity_path.display());
    let entity_store = match EntityStore::open_with_encryptor(
        &entity_path,
        vault_manager.clone() as Arc<dyn privstack_crypto::DataEncryptor>,
    ) {
        Ok(s) => {
            eprintln!("[FFI] Entity store opened OK");
            s
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open entity store: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    // Event store for sync replication
    let events_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("events.duckdb")
    };
    eprintln!("[FFI] Opening event store: {}", events_path.display());
    let event_store = match EventStore::open(&events_path) {
        Ok(s) => {
            eprintln!("[FFI] Event store opened OK");
            s
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open event store: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    let entity_store = Arc::new(entity_store);
    let event_store = Arc::new(event_store);

    let entity_registry = EntityRegistry::new();

    let runtime = match Runtime::new() {
        Ok(rt) => rt,
        Err(_) => return PrivStackError::Unknown,
    };

    let sync_config = SyncConfig::default();
    let sync_engine = SyncEngine::new(peer_id, sync_config);

    let activation_store = ActivationStore::new(ActivationStore::default_path());

    let device_name = hostname::get()
        .map(|h| h.to_string_lossy().to_string())
        .unwrap_or_else(|_| "PrivStack Device".to_string());

    // Dataset store (unencrypted DuckDB for tabular data)
    let dataset_store = {
        let ds_path = if path == ":memory:" {
            None
        } else {
            Some(Path::new(path).with_extension("datasets.duckdb"))
        };
        match ds_path {
            Some(p) => {
                eprintln!("[FFI] Opening dataset store: {}", p.display());
                match privstack_datasets::DatasetStore::open(&p) {
                    Ok(ds) => {
                        eprintln!("[FFI] Dataset store opened OK");
                        Some(ds)
                    }
                    Err(e) => {
                        eprintln!("[FFI] WARN: Failed to open dataset store: {e:?}");
                        None
                    }
                }
            }
            None => match privstack_datasets::DatasetStore::open_in_memory() {
                Ok(ds) => Some(ds),
                Err(_) => None,
            },
        }
    };

    let mut handle = HANDLE.lock().unwrap();
    *handle = Some(PrivStackHandle {
        db_path: path.to_string(),
        entity_store,
        event_store,
        entity_registry,
        peer_id,
        runtime,
        sync_engine,
        p2p_transport: None,
        orchestrator_handle: None,
        sync_event_rx: None,
        pairing_manager: Arc::new(std::sync::Mutex::new(PairingManager::new())),
        personal_policy: None,
        device_name,
        google_drive: None,
        icloud: None,
        activation_store,
        vault_manager,
        blob_store,
        dataset_store,
    });

    PrivStackError::Ok
}

/// Shared init logic with plugin host — accepts a closure to construct the PluginHostManager,
/// so tests can inject a policy-free manager without touching the filesystem.
#[cfg(feature = "wasm-plugins")]
fn init_with_plugin_host_builder<F>(path: &str, build_plugin_host: F) -> PrivStackError
where
    F: FnOnce(Arc<EntityStore>, Arc<EventStore>) -> PluginHostManager,
{
    let peer_id = load_or_create_peer_id(path);

    // Create vault manager (encrypted blob storage)
    let vault_db_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("vault.duckdb")
    };
    eprintln!("[FFI] Opening vault DB: {}", vault_db_path.display());
    let vault_manager = match VaultManager::open(&vault_db_path) {
        Ok(vm) => {
            eprintln!("[FFI] Vault DB opened OK");
            Arc::new(vm)
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open vault DB: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    // Create blob store with vault-backed encryption
    let blob_db_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("blobs.duckdb")
    };
    eprintln!("[FFI] Opening blob store: {}", blob_db_path.display());
    let blob_store = match BlobStore::open_with_encryptor(
        &blob_db_path,
        vault_manager.clone() as Arc<dyn privstack_crypto::DataEncryptor>,
    ) {
        Ok(bs) => {
            eprintln!("[FFI] Blob store opened OK");
            bs
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open blob store: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    // Create generic entity store with vault-backed encryption
    let entity_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("entities.duckdb")
    };
    eprintln!("[FFI] Opening entity store: {}", entity_path.display());
    let entity_store = match EntityStore::open_with_encryptor(
        &entity_path,
        vault_manager.clone() as Arc<dyn privstack_crypto::DataEncryptor>,
    ) {
        Ok(s) => {
            eprintln!("[FFI] Entity store opened OK");
            s
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open entity store: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    // Event store for sync replication
    let events_path = if path == ":memory:" {
        Path::new(":memory:").to_path_buf()
    } else {
        Path::new(path).with_extension("events.duckdb")
    };
    eprintln!("[FFI] Opening event store: {}", events_path.display());
    let event_store = match EventStore::open(&events_path) {
        Ok(s) => {
            eprintln!("[FFI] Event store opened OK");
            s
        }
        Err(e) => {
            eprintln!("[FFI] FAILED to open event store: {e:?}");
            return PrivStackError::StorageError;
        }
    };

    let entity_store = Arc::new(entity_store);
    let event_store = Arc::new(event_store);

    let entity_registry = EntityRegistry::new();

    let plugin_host = build_plugin_host(
        Arc::clone(&entity_store),
        Arc::clone(&event_store),
    );

    let runtime = match Runtime::new() {
        Ok(rt) => rt,
        Err(_) => return PrivStackError::Unknown,
    };

    let sync_config = SyncConfig::default();
    let sync_engine = SyncEngine::new(peer_id, sync_config);

    let activation_store = ActivationStore::new(ActivationStore::default_path());

    let device_name = hostname::get()
        .map(|h| h.to_string_lossy().to_string())
        .unwrap_or_else(|_| "PrivStack Device".to_string());

    // Dataset store (unencrypted DuckDB for tabular data)
    let dataset_store = {
        let ds_path = if path == ":memory:" {
            None
        } else {
            Some(Path::new(path).with_extension("datasets.duckdb"))
        };
        match ds_path {
            Some(p) => {
                eprintln!("[FFI] Opening dataset store: {}", p.display());
                match privstack_datasets::DatasetStore::open(&p) {
                    Ok(ds) => {
                        eprintln!("[FFI] Dataset store opened OK");
                        Some(ds)
                    }
                    Err(e) => {
                        eprintln!("[FFI] WARN: Failed to open dataset store: {e:?}");
                        None
                    }
                }
            }
            None => match privstack_datasets::DatasetStore::open_in_memory() {
                Ok(ds) => Some(ds),
                Err(_) => None,
            },
        }
    };

    let mut handle = HANDLE.lock().unwrap();
    *handle = Some(PrivStackHandle {
        db_path: path.to_string(),
        entity_store,
        event_store,
        entity_registry,
        peer_id,
        runtime,
        sync_engine,
        p2p_transport: None,
        orchestrator_handle: None,
        sync_event_rx: None,
        pairing_manager: Arc::new(std::sync::Mutex::new(PairingManager::new())),
        personal_policy: None,
        device_name,
        google_drive: None,
        icloud: None,
        activation_store,
        vault_manager,
        blob_store,
        dataset_store,
        plugin_host,
    });

    PrivStackError::Ok
}

/// Shuts down the PrivStack runtime and frees resources.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_shutdown() {
    let mut handle = HANDLE.lock().unwrap();
    *handle = None;
}

/// Returns the library version as a string.
///
/// # Safety
/// - The returned string is statically allocated and must not be freed.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_version() -> *const c_char {
    static VERSION: &[u8] = concat!(env!("CARGO_PKG_VERSION"), "\0").as_bytes();
    VERSION.as_ptr() as *const c_char
}

/// Frees a string allocated by this library.
///
/// # Safety
/// - `s` must be a string allocated by this library, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_free_string(s: *mut c_char) { unsafe {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}}

/// Frees a byte buffer allocated by this library.
///
/// # Safety
/// - `data` must be a pointer allocated by this library, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_free_bytes(data: *mut u8, len: usize) { unsafe {
    if !data.is_null() && len > 0 {
        drop(Box::from_raw(std::slice::from_raw_parts_mut(data, len)));
    }
}}

// ============================================================================
// App-Level Authentication Functions
// ============================================================================

/// Checks if the app master password has been initialized.
/// Uses the "default" vault as the source of truth.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_auth_is_initialized() -> bool {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };
    handle.vault_manager.is_initialized("default")
}

/// Checks if the app is currently unlocked.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_auth_is_unlocked() -> bool {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };
    handle.vault_manager.is_unlocked("default")
}

/// Initializes the app with a master password (first-time setup).
/// Creates and initializes the "default" vault.
///
/// # Safety
/// - `master_password` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_auth_initialize(
    master_password: *const c_char,
) -> PrivStackError { unsafe {
    if master_password.is_null() {
        return PrivStackError::NullPointer;
    }

    let password = match CStr::from_ptr(master_password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.initialize("default", password) {
        Ok(_) => {
            // Migrate any pre-existing unencrypted data
            let _ = handle.entity_store.migrate_unencrypted();
            let _ = handle.blob_store.migrate_unencrypted();
            PrivStackError::Ok
        }
        Err(privstack_vault::VaultError::PasswordTooShort) => PrivStackError::PasswordTooShort,
        Err(privstack_vault::VaultError::AlreadyInitialized) => PrivStackError::VaultAlreadyInitialized,
        Err(privstack_vault::VaultError::Storage(_)) => PrivStackError::StorageError,
        Err(_) => PrivStackError::AuthError,
    }
}}

/// Unlocks the app with a master password.
/// Unlocks all initialized vaults sharing the master password.
///
/// # Safety
/// - `master_password` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_auth_unlock(
    master_password: *const c_char,
) -> PrivStackError { unsafe {
    if master_password.is_null() {
        return PrivStackError::NullPointer;
    }

    let password = match CStr::from_ptr(master_password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.unlock_all(password) {
        Ok(_) => {
            // Migrate any pre-existing unencrypted data to encrypted form
            let _ = handle.entity_store.migrate_unencrypted();
            let _ = handle.blob_store.migrate_unencrypted();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::AuthError,
    }
}}

/// Locks the app, securing all sensitive data.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_auth_lock() -> PrivStackError {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.vault_manager.lock_all();
    PrivStackError::Ok
}

/// Changes the master password for all vaults.
///
/// # Safety
/// - `old_password` and `new_password` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_auth_change_password(
    old_password: *const c_char,
    new_password: *const c_char,
) -> PrivStackError { unsafe {
    if old_password.is_null() || new_password.is_null() {
        return PrivStackError::NullPointer;
    }

    let old_pwd = match CStr::from_ptr(old_password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let new_pwd = match CStr::from_ptr(new_password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    if new_pwd.len() < 8 {
        return PrivStackError::PasswordTooShort;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    // Capture old key bytes before password change for re-encryption
    let old_key_bytes = handle.vault_manager.default_key_bytes();

    match handle.vault_manager.change_password_all(old_pwd, new_pwd) {
        Ok(_) => {
            // Re-encrypt entity and blob stores with new key
            if let (Some(old_kb), Some(new_kb)) =
                (old_key_bytes, handle.vault_manager.default_key_bytes())
            {
                let _ = handle.entity_store.re_encrypt_all(&old_kb, &new_kb);
                let _ = handle.blob_store.re_encrypt_all(&old_kb, &new_kb);
            }
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::AuthError,
    }
}}

// ============================================================================
// Vault Management Functions
// ============================================================================

/// Creates a new vault with the given ID.
///
/// # Safety
/// - `vault_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_create(vault_id: *const c_char) -> PrivStackError { unsafe {
    if vault_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.create_vault(id) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::StorageError,
    }
}}

/// Initializes a vault with a password.
///
/// # Safety
/// - `vault_id` and `password` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_initialize(
    vault_id: *const c_char,
    password: *const c_char,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || password.is_null() {
        return PrivStackError::NullPointer;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let pwd = match CStr::from_ptr(password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.initialize(id, pwd) {
        Ok(_) => PrivStackError::Ok,
        Err(privstack_vault::VaultError::PasswordTooShort) => PrivStackError::PasswordTooShort,
        Err(privstack_vault::VaultError::AlreadyInitialized) => PrivStackError::VaultAlreadyInitialized,
        Err(privstack_vault::VaultError::Storage(_)) => PrivStackError::StorageError,
        Err(privstack_vault::VaultError::Crypto(_)) => PrivStackError::StorageError,
        Err(_) => PrivStackError::AuthError,
    }
}}

/// Unlocks a vault with a password.
///
/// # Safety
/// - `vault_id` and `password` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_unlock(
    vault_id: *const c_char,
    password: *const c_char,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || password.is_null() {
        return PrivStackError::NullPointer;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let pwd = match CStr::from_ptr(password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.unlock(id, pwd) {
        Ok(_) => PrivStackError::Ok,
        Err(privstack_vault::VaultError::NotInitialized) => PrivStackError::NotInitialized,
        Err(privstack_vault::VaultError::InvalidPassword) => PrivStackError::AuthError,
        Err(privstack_vault::VaultError::VaultNotFound(_)) => PrivStackError::VaultNotFound,
        Err(privstack_vault::VaultError::Storage(_)) => PrivStackError::StorageError,
        Err(_) => PrivStackError::AuthError,
    }
}}

/// Locks a vault.
///
/// # Safety
/// - `vault_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_lock(vault_id: *const c_char) -> PrivStackError { unsafe {
    if vault_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.vault_manager.lock(id);
    PrivStackError::Ok
}}

/// Locks all vaults.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_vault_lock_all() -> PrivStackError {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.vault_manager.lock_all();
    PrivStackError::Ok
}

/// Checks if a vault has been initialized.
///
/// # Safety
/// - `vault_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_is_initialized(vault_id: *const c_char) -> bool { unsafe {
    if vault_id.is_null() {
        return false;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return false,
    };

    let handle = HANDLE.lock().unwrap();
    match handle.as_ref() {
        Some(h) => h.vault_manager.is_initialized(id),
        None => false,
    }
}}

/// Checks if a vault is unlocked.
///
/// # Safety
/// - `vault_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_is_unlocked(vault_id: *const c_char) -> bool { unsafe {
    if vault_id.is_null() {
        return false;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return false,
    };

    let handle = HANDLE.lock().unwrap();
    match handle.as_ref() {
        Some(h) => h.vault_manager.is_unlocked(id),
        None => false,
    }
}}

/// Changes a vault's password.
///
/// # Safety
/// - All parameters must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_change_password(
    vault_id: *const c_char,
    old_password: *const c_char,
    new_password: *const c_char,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || old_password.is_null() || new_password.is_null() {
        return PrivStackError::NullPointer;
    }

    let id = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let old_pwd = match CStr::from_ptr(old_password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let new_pwd = match CStr::from_ptr(new_password).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.change_password(id, old_pwd, new_pwd) {
        Ok(_) => PrivStackError::Ok,
        Err(privstack_vault::VaultError::PasswordTooShort) => PrivStackError::PasswordTooShort,
        Err(privstack_vault::VaultError::InvalidPassword) => PrivStackError::AuthError,
        Err(privstack_vault::VaultError::Storage(_)) => PrivStackError::StorageError,
        Err(_) => PrivStackError::AuthError,
    }
}}

// ============================================================================
// Encrypted Blob Storage (Vault Blobs)
// ============================================================================

/// Stores an encrypted blob in a vault.
///
/// # Safety
/// - `vault_id` and `blob_id` must be valid null-terminated UTF-8 strings.
/// - `data` must point to `data_len` valid bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_blob_store(
    vault_id: *const c_char,
    blob_id: *const c_char,
    data: *const u8,
    data_len: usize,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || blob_id.is_null() || data.is_null() {
        return PrivStackError::NullPointer;
    }

    let vid = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let bid = match CStr::from_ptr(blob_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let content = std::slice::from_raw_parts(data, data_len);

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.store_blob(vid, bid, content) {
        Ok(_) => PrivStackError::Ok,
        Err(privstack_vault::VaultError::Locked) => PrivStackError::VaultLocked,
        Err(_) => PrivStackError::StorageError,
    }
}}

/// Reads an encrypted blob from a vault.
///
/// # Safety
/// - `vault_id` and `blob_id` must be valid null-terminated UTF-8 strings.
/// - `out_data` and `out_len` must be valid pointers.
/// - The returned data must be freed with `privstack_free_bytes`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_blob_read(
    vault_id: *const c_char,
    blob_id: *const c_char,
    out_data: *mut *mut u8,
    out_len: *mut usize,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || blob_id.is_null() || out_data.is_null() || out_len.is_null() {
        return PrivStackError::NullPointer;
    }

    let vid = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let bid = match CStr::from_ptr(blob_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.read_blob(vid, bid) {
        Ok(data) => {
            let boxed = data.into_boxed_slice();
            *out_len = boxed.len();
            *out_data = Box::into_raw(boxed) as *mut u8;
            PrivStackError::Ok
        }
        Err(privstack_vault::VaultError::Locked) => PrivStackError::VaultLocked,
        Err(privstack_vault::VaultError::BlobNotFound(_)) => PrivStackError::NotFound,
        Err(_) => PrivStackError::StorageError,
    }
}}

/// Deletes an encrypted blob from a vault.
///
/// # Safety
/// - `vault_id` and `blob_id` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_blob_delete(
    vault_id: *const c_char,
    blob_id: *const c_char,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || blob_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let vid = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let bid = match CStr::from_ptr(blob_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.delete_blob(vid, bid) {
        Ok(_) => PrivStackError::Ok,
        Err(privstack_vault::VaultError::BlobNotFound(_)) => PrivStackError::NotFound,
        Err(_) => PrivStackError::StorageError,
    }
}}

/// Lists blobs in a vault as JSON.
///
/// # Safety
/// - `vault_id` must be a valid null-terminated UTF-8 string.
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_vault_blob_list(
    vault_id: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if vault_id.is_null() || out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let vid = match CStr::from_ptr(vault_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.vault_manager.list_blobs(vid) {
        Ok(blobs) => match serde_json::to_string(&blobs) {
            Ok(json) => {
                *out_json = CString::new(json).unwrap().into_raw();
                PrivStackError::Ok
            }
            Err(_) => PrivStackError::JsonError,
        },
        Err(_) => PrivStackError::StorageError,
    }
}}

// ============================================================================
// Unencrypted Blob Storage
// ============================================================================

/// Stores a blob in the unencrypted blob store.
///
/// # Safety
/// - `namespace`, `blob_id` must be valid null-terminated UTF-8 strings.
/// - `data` must point to `data_len` valid bytes.
/// - `metadata_json` can be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_blob_store(
    namespace: *const c_char,
    blob_id: *const c_char,
    data: *const u8,
    data_len: usize,
    metadata_json: *const c_char,
) -> PrivStackError { unsafe {
    if namespace.is_null() || blob_id.is_null() || data.is_null() {
        return PrivStackError::NullPointer;
    }

    let ns = match CStr::from_ptr(namespace).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let bid = match CStr::from_ptr(blob_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let meta = if metadata_json.is_null() {
        None
    } else {
        match CStr::from_ptr(metadata_json).to_str() {
            Ok(s) => Some(s),
            Err(_) => return PrivStackError::InvalidUtf8,
        }
    };

    let content = std::slice::from_raw_parts(data, data_len);

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.blob_store.store(ns, bid, content, meta) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::StorageError,
    }
}}

/// Reads a blob from the unencrypted blob store.
///
/// # Safety
/// - `namespace` and `blob_id` must be valid null-terminated UTF-8 strings.
/// - The returned data must be freed with `privstack_free_bytes`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_blob_read(
    namespace: *const c_char,
    blob_id: *const c_char,
    out_data: *mut *mut u8,
    out_len: *mut usize,
) -> PrivStackError { unsafe {
    if namespace.is_null() || blob_id.is_null() || out_data.is_null() || out_len.is_null() {
        return PrivStackError::NullPointer;
    }

    let ns = match CStr::from_ptr(namespace).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let bid = match CStr::from_ptr(blob_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.blob_store.read(ns, bid) {
        Ok(data) => {
            let boxed = data.into_boxed_slice();
            *out_len = boxed.len();
            *out_data = Box::into_raw(boxed) as *mut u8;
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::NotFound,
    }
}}

/// Deletes a blob from the unencrypted blob store.
///
/// # Safety
/// - `namespace` and `blob_id` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_blob_delete(
    namespace: *const c_char,
    blob_id: *const c_char,
) -> PrivStackError { unsafe {
    if namespace.is_null() || blob_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let ns = match CStr::from_ptr(namespace).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let bid = match CStr::from_ptr(blob_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.blob_store.delete(ns, bid) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::NotFound,
    }
}}

/// Lists blobs in a namespace as JSON.
///
/// # Safety
/// - `namespace` must be a valid null-terminated UTF-8 string.
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_blob_list(
    namespace: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if namespace.is_null() || out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let ns = match CStr::from_ptr(namespace).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.blob_store.list(ns) {
        Ok(blobs) => match serde_json::to_string(&blobs) {
            Ok(json) => {
                *out_json = CString::new(json).unwrap().into_raw();
                PrivStackError::Ok
            }
            Err(_) => PrivStackError::JsonError,
        },
        Err(_) => PrivStackError::StorageError,
    }
}}

// ============================================================================
// Sync Functions
// ============================================================================

/// Starts the P2P sync transport and orchestrator.
///
/// # Safety
/// - Must be called after `privstack_init`.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_sync_start() -> PrivStackError {
    eprintln!("[FFI SYNC] privstack_sync_start called");
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    if handle.p2p_transport.is_some() {
        eprintln!("[FFI SYNC] privstack_sync_start: already running");
        return PrivStackError::SyncAlreadyRunning;
    }

    let mut config = P2pConfig::default();

    if let Some(sync_code) = handle.pairing_manager.lock().unwrap().current_code().cloned() {
        eprintln!("[FFI SYNC] privstack_sync_start: using sync code hash");
        if let Ok(hash_bytes) = hex::decode(&sync_code.hash) {
            config.sync_code_hash = Some(hash_bytes);
        }
    }

    config.device_name = handle.device_name.clone();
    eprintln!("[FFI SYNC] privstack_sync_start: device_name={}", config.device_name);

    // Load or create a persistent keypair so the libp2p PeerId is stable across restarts.
    // This ensures remote peers map to the same PrivStack PeerId every time.
    let keypair = load_or_create_keypair(&handle.db_path);

    let mut transport = match P2pTransport::with_keypair(handle.peer_id, keypair, config) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("[FFI SYNC] privstack_sync_start: failed to create transport: {:?}", e);
            return PrivStackError::SyncError;
        }
    };

    eprintln!("[FFI SYNC] privstack_sync_start: libp2p_peer_id={}", transport.libp2p_peer_id());
    eprintln!("[FFI SYNC] privstack_sync_start: starting transport...");
    let result = handle.runtime.block_on(transport.start());
    if let Err(e) = result {
        eprintln!("[FFI SYNC] privstack_sync_start: transport start failed: {:?}", e);
        return PrivStackError::SyncError;
    }
    eprintln!("[FFI SYNC] privstack_sync_start: transport started");

    let transport = Arc::new(TokioMutex::new(transport));

    eprintln!("[FFI SYNC] privstack_sync_start: creating orchestrator...");
    let orch_entity_store = Arc::clone(&handle.entity_store);
    let orch_event_store = Arc::clone(&handle.event_store);

    // Always use PersonalSyncPolicy + pairing gate
    let policy = Arc::new(PersonalSyncPolicy::new());
    handle.personal_policy = Some(policy.clone());

    let (orch_handle, event_rx, command_rx, orchestrator) = {
        eprintln!("[FFI SYNC] privstack_sync_start: using personal orchestrator with pairing");
        create_personal_orchestrator(
            handle.peer_id,
            orch_entity_store,
            orch_event_store,
            OrchestratorConfig::default(),
            policy,
            handle.pairing_manager.clone(),
        )
    };

    let transport_clone = transport.clone();
    handle.runtime.spawn(async move {
        eprintln!("[FFI SYNC] Orchestrator task starting...");
        if let Err(e) = orchestrator.run(transport_clone, command_rx).await {
            eprintln!("[FFI SYNC] Orchestrator error: {}", e);
        }
        eprintln!("[FFI SYNC] Orchestrator task exiting");
    });

    handle.p2p_transport = Some(transport);
    handle.orchestrator_handle = Some(orch_handle);
    handle.sync_event_rx = Some(event_rx);

    eprintln!("[FFI SYNC] privstack_sync_start: complete");
    PrivStackError::Ok
}

/// Stops the P2P sync transport and orchestrator.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_sync_stop() -> PrivStackError {
    eprintln!("[FFI SYNC] privstack_sync_stop called");
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    if let Some(ref orch_handle) = handle.orchestrator_handle {
        eprintln!("[FFI SYNC] privstack_sync_stop: shutting down orchestrator...");
        let _ = handle.runtime.block_on(orch_handle.shutdown());
        eprintln!("[FFI SYNC] privstack_sync_stop: orchestrator shutdown complete");
    }
    handle.orchestrator_handle = None;
    handle.sync_event_rx = None;

    if let Some(ref transport) = handle.p2p_transport {
        eprintln!("[FFI SYNC] privstack_sync_stop: stopping transport...");
        let _ = handle.runtime.block_on(async {
            let mut t = transport.lock().await;
            t.stop().await
        });
        eprintln!("[FFI SYNC] privstack_sync_stop: transport stopped");
    }
    handle.p2p_transport = None;

    eprintln!("[FFI SYNC] privstack_sync_stop: complete");
    PrivStackError::Ok
}

/// Returns whether sync is running.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_sync_is_running() -> bool {
    let handle = HANDLE.lock().unwrap();
    match handle.as_ref() {
        Some(h) => {
            if let Some(ref transport) = h.p2p_transport {
                h.runtime.block_on(async {
                    match transport.try_lock() {
                        Ok(t) => t.is_running(),
                        Err(_) => true,
                    }
                })
            } else {
                false
            }
        }
        None => false,
    }
}

/// Gets the current sync status.
///
/// # Safety
/// - `out_json` must be a valid pointer.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_status(out_json: *mut *mut c_char) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let running = if let Some(ref transport) = handle.p2p_transport {
        handle.runtime.block_on(async {
            match transport.try_lock() {
                Ok(t) => t.is_running(),
                Err(_) => true,
            }
        })
    } else {
        false
    };

    let discovered_peers = if let Some(ref transport) = handle.p2p_transport {
        handle.runtime.block_on(async {
            let t = transport.lock().await;
            t.discovered_peers()
                .into_iter()
                .map(|p| DiscoveredPeerInfo {
                    peer_id: p.peer_id.to_string(),
                    device_name: p.device_name.clone(),
                    discovery_method: format!("{:?}", p.discovery_method),
                    addresses: p.addresses.iter().map(|a| a.to_string()).collect(),
                })
                .collect()
        })
    } else {
        Vec::new()
    };

    let status = SyncStatus {
        running,
        local_peer_id: handle.peer_id.to_string(),
        discovered_peers,
    };

    match serde_json::to_string(&status) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Polls for the next sync event.
///
/// # Safety
/// - `out_json` must be a valid pointer.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_poll_event(out_json: *mut *mut c_char) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let rx = match handle.sync_event_rx.as_mut() {
        Some(rx) => rx,
        None => {
            *out_json = std::ptr::null_mut();
            return PrivStackError::SyncNotRunning;
        }
    };

    match rx.try_recv() {
        Ok(event) => {
            let dto = SyncEventDto::from(event);
            match serde_json::to_string(&dto) {
                Ok(json) => {
                    let c_json = CString::new(json).unwrap();
                    *out_json = c_json.into_raw();
                    PrivStackError::Ok
                }
                Err(_) => PrivStackError::JsonError,
            }
        }
        Err(mpsc::error::TryRecvError::Empty) => {
            *out_json = std::ptr::null_mut();
            PrivStackError::Ok
        }
        Err(mpsc::error::TryRecvError::Disconnected) => {
            *out_json = std::ptr::null_mut();
            PrivStackError::SyncNotRunning
        }
    }
}}

/// Triggers a manual sync with all discovered peers.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_sync_trigger() -> PrivStackError {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let orch_handle = match &handle.orchestrator_handle {
        Some(oh) => oh,
        None => return PrivStackError::SyncNotRunning,
    };

    // trigger_sync is not a direct method; sync is continuous while running
    let _ = orch_handle;
    PrivStackError::Ok
}

/// Publishes an event payload for sync.
///
/// # Safety
/// - `event_json` must be a valid null-terminated UTF-8 JSON string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_publish_event(
    event_json: *const c_char,
) -> PrivStackError { unsafe {
    if event_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let json_str = match CStr::from_ptr(event_json).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let event: Event = match serde_json::from_str(json_str) {
        Ok(e) => e,
        Err(_) => return PrivStackError::JsonError,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let orch_handle = match &handle.orchestrator_handle {
        Some(oh) => oh,
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(orch_handle.record_event(event)) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::SyncError,
    }
}}

/// Alias for `privstack_sync_status` — gets the current sync status.
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_get_status(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    privstack_sync_status(out_json)
}}

/// Gets the local peer ID as a string.
///
/// # Safety
/// - `out_peer_id` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_get_peer_id(
    out_peer_id: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_peer_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let c_str = CString::new(handle.peer_id.to_string()).unwrap();
    *out_peer_id = c_str.into_raw();
    PrivStackError::Ok
}}

/// Gets discovered peers as a JSON array.
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_get_peers(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let peers = if let Some(ref transport) = handle.p2p_transport {
        handle.runtime.block_on(async {
            let t = transport.lock().await;
            t.discovered_peers()
                .into_iter()
                .map(|p| DiscoveredPeerInfo {
                    peer_id: p.peer_id.to_string(),
                    device_name: p.device_name.clone(),
                    discovery_method: format!("{:?}", p.discovery_method),
                    addresses: p.addresses.iter().map(|a| a.to_string()).collect(),
                })
                .collect::<Vec<_>>()
        })
    } else {
        Vec::new()
    };

    match serde_json::to_string(&peers) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Gets the count of discovered peers. Returns -1 if not initialized.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_sync_peer_count() -> c_int {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return -1,
    };

    if let Some(ref transport) = handle.p2p_transport {
        handle.runtime.block_on(async {
            match transport.try_lock() {
                Ok(t) => t.discovered_peers().len() as c_int,
                Err(_) => -1,
            }
        })
    } else {
        0
    }
}

/// Shares a document for sync with all peers.
///
/// # Safety
/// - `document_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_share_document(
    document_id: *const c_char,
) -> PrivStackError { unsafe {
    if document_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let doc_str = match CStr::from_ptr(document_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let eid: EntityId = match doc_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::InvalidArgument,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let orch_handle = match &handle.orchestrator_handle {
        Some(oh) => oh,
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(orch_handle.share_entity(eid)) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::SyncError,
    }
}}

/// Records a local event for sync. Takes a document ID and event JSON payload.
///
/// # Safety
/// - `document_id` and `event_json` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_record_event(
    document_id: *const c_char,
    event_json: *const c_char,
) -> PrivStackError { unsafe {
    if document_id.is_null() || event_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let _doc_str = match CStr::from_ptr(document_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let json_str = match CStr::from_ptr(event_json).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let event: Event = match serde_json::from_str(json_str) {
        Ok(e) => e,
        Err(_) => return PrivStackError::JsonError,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    // Save to event store immediately (same rationale as privstack_sync_snapshot).
    if let Err(e) = handle.event_store.save_event(&event) {
        eprintln!("[FFI SYNC] record_event: failed to save event to store: {:?}", e);
        return PrivStackError::SyncError;
    }

    let orch_handle = match &handle.orchestrator_handle {
        Some(oh) => oh,
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(orch_handle.record_event(event)) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::SyncError,
    }
}}

/// Polls for all available sync events (non-blocking). Returns a JSON array.
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_poll_events(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let rx = match handle.sync_event_rx.as_mut() {
        Some(rx) => rx,
        None => {
            // Return empty array when sync is not running
            let c_json = CString::new("[]").unwrap();
            *out_json = c_json.into_raw();
            return PrivStackError::Ok;
        }
    };

    let mut events = Vec::new();
    loop {
        match rx.try_recv() {
            Ok(event) => events.push(SyncEventDto::from(event)),
            Err(_) => break,
        }
    }

    match serde_json::to_string(&events) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Triggers immediate sync for a document with all known peers.
///
/// # Safety
/// - `document_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_document(
    document_id: *const c_char,
) -> PrivStackError { unsafe {
    if document_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let doc_str = match CStr::from_ptr(document_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let eid: EntityId = match doc_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::InvalidArgument,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let orch_handle = match &handle.orchestrator_handle {
        Some(oh) => oh,
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(orch_handle.sync_entity(eid)) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::SyncError,
    }
}}

/// Records a full entity snapshot for sync.
///
/// # Safety
/// - All string parameters must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_snapshot(
    document_id: *const c_char,
    entity_type: *const c_char,
    json_data: *const c_char,
) -> PrivStackError { unsafe {
    if document_id.is_null() || entity_type.is_null() || json_data.is_null() {
        return PrivStackError::NullPointer;
    }

    let doc_str = match CStr::from_ptr(document_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let eid: EntityId = match doc_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::InvalidArgument,
    };

    let etype_str = match CStr::from_ptr(entity_type).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let data_str = match CStr::from_ptr(json_data).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let event = Event::full_snapshot(eid, handle.peer_id, etype_str, data_str);

    // Save to event store immediately so it's visible even if a sync cycle is in
    // progress (periodic_sync holds the command loop, blocking RecordLocalEvent).
    // The duplicate INSERT OR IGNORE in handle_local_event is harmless.
    if let Err(e) = handle.event_store.save_event(&event) {
        eprintln!("[FFI SYNC] snapshot: failed to save event to store: {:?}", e);
        return PrivStackError::SyncError;
    }

    let orch_handle = match &handle.orchestrator_handle {
        Some(oh) => oh,
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(orch_handle.record_event(event)) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::SyncError,
    }
}}

/// Imports an entity received from sync into the local store.
///
/// # Safety
/// - All string parameters must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_sync_import_entity(
    entity_type: *const c_char,
    json_data: *const c_char,
) -> PrivStackError { unsafe {
    if entity_type.is_null() || json_data.is_null() {
        return PrivStackError::NullPointer;
    }

    let _etype_str = match CStr::from_ptr(entity_type).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let data_str = match CStr::from_ptr(json_data).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let entity: Entity = match serde_json::from_str(data_str) {
        Ok(e) => e,
        Err(_) => return PrivStackError::JsonError,
    };

    match handle.entity_store.save_entity_raw(&entity) {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::StorageError,
    }
}}

// ============================================================================
// Pairing Functions
// ============================================================================

/// Generates a new sync code for pairing.
///
/// # Safety
/// - `out_code` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_generate_code(
    out_code: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_code.is_null() {
        return PrivStackError::NullPointer;
    }

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let code = SyncCode::generate();
    handle.pairing_manager.lock().unwrap().set_sync_code(code.clone());
    let json = match serde_json::to_string(&code) {
        Ok(j) => j,
        Err(_) => return PrivStackError::JsonError,
    };
    let c_str = CString::new(json).unwrap();
    *out_code = c_str.into_raw();
    PrivStackError::Ok
}}

/// Joins a sync group using a code.
///
/// # Safety
/// - `code` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_join_code(code: *const c_char) -> PrivStackError { unsafe {
    if code.is_null() {
        return PrivStackError::NullPointer;
    }

    let code_str = match CStr::from_ptr(code).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let sync_code = match SyncCode::from_input(code_str) {
        Ok(c) => c,
        Err(_) => return PrivStackError::InvalidSyncCode,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.pairing_manager.lock().unwrap().set_sync_code(sync_code);
    PrivStackError::Ok
}}

/// Gets the current sync code (if any).
///
/// # Safety
/// - `out_code` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_get_code(
    out_code: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_code.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let code = handle.pairing_manager.lock().unwrap().current_code().cloned();
    match code {
        Some(code) => {
            let json = match serde_json::to_string(&code) {
                Ok(j) => j,
                Err(_) => return PrivStackError::JsonError,
            };
            let c_str = CString::new(json).unwrap();
            *out_code = c_str.into_raw();
            PrivStackError::Ok
        }
        None => {
            *out_code = std::ptr::null_mut();
            PrivStackError::Ok
        }
    }
}}

/// Lists trusted peers as JSON.
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_get_trusted_peers(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    privstack_pairing_list_peers(out_json)
}}

/// Lists all discovered peers pending approval (JSON array).
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_get_discovered_peers(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let peers: Vec<_> = handle.pairing_manager.lock().unwrap().discovered_peers().into_iter().cloned().collect();
    match serde_json::to_string(&peers) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Lists all trusted peers (JSON array).
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_list_peers(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let peers: Vec<_> = handle.pairing_manager.lock().unwrap().trusted_peers().into_iter().cloned().collect();
    match serde_json::to_string(&peers) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Adds a discovered peer as trusted.
///
/// # Safety
/// - `peer_id` and `device_name` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_trust_peer(
    peer_id: *const c_char,
    device_name: *const c_char,
) -> PrivStackError { unsafe {
    if peer_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let pid_str = match CStr::from_ptr(peer_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let _name = if device_name.is_null() {
        None
    } else {
        match CStr::from_ptr(device_name).to_str() {
            Ok(s) => Some(s.to_string()),
            Err(_) => return PrivStackError::InvalidUtf8,
        }
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.pairing_manager.lock().unwrap().approve_peer(pid_str);
    PrivStackError::Ok
}}

/// Removes a trusted peer.
///
/// # Safety
/// - `peer_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_remove_peer(peer_id: *const c_char) -> PrivStackError { unsafe {
    if peer_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let pid_str = match CStr::from_ptr(peer_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.pairing_manager.lock().unwrap().remove_trusted_peer(pid_str);
    PrivStackError::Ok
}}

/// Alias for `privstack_pairing_remove_peer` — removes a trusted peer.
///
/// # Safety
/// - `peer_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_remove_trusted_peer(
    peer_id: *const c_char,
) -> PrivStackError { unsafe {
    privstack_pairing_remove_peer(peer_id)
}}

/// Alias for `privstack_pairing_join_code` — sets sync code from user input.
///
/// # Safety
/// - `code` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_set_code(
    code: *const c_char,
) -> PrivStackError { unsafe {
    privstack_pairing_join_code(code)
}}

/// Clears the current sync code and discovered peers.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_pairing_clear_code() -> PrivStackError {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.pairing_manager.lock().unwrap().clear_sync_code();
    PrivStackError::Ok
}

/// Alias for `privstack_pairing_trust_peer` — approves a discovered peer.
///
/// # Safety
/// - `peer_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_approve_peer(
    peer_id: *const c_char,
) -> PrivStackError { unsafe {
    privstack_pairing_trust_peer(peer_id, std::ptr::null())
}}

/// Rejects a discovered peer.
///
/// # Safety
/// - `peer_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_reject_peer(
    peer_id: *const c_char,
) -> PrivStackError { unsafe {
    if peer_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let pid_str = match CStr::from_ptr(peer_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.pairing_manager.lock().unwrap().reject_peer(pid_str);
    PrivStackError::Ok
}}

/// Checks if a peer is trusted.
///
/// # Safety
/// - `peer_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_is_trusted(
    peer_id: *const c_char,
) -> bool { unsafe {
    if peer_id.is_null() {
        return false;
    }

    let pid_str = match CStr::from_ptr(peer_id).to_str() {
        Ok(s) => s,
        Err(_) => return false,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };

    handle.pairing_manager.lock().unwrap().is_trusted(pid_str)
}}

/// Saves the pairing state to JSON.
///
/// # Safety
/// - `out_json` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_save_state(
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = handle.pairing_manager.lock().unwrap().to_json();
    match result {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Loads the pairing state from JSON.
///
/// # Safety
/// - `json` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_load_state(json: *const c_char) -> PrivStackError { unsafe {
    if json.is_null() {
        return PrivStackError::NullPointer;
    }

    let json_str = match CStr::from_ptr(json).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match PairingManager::from_json(json_str) {
        Ok(manager) => {
            *handle.pairing_manager.lock().unwrap() = manager;
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Gets the device name.
///
/// # Safety
/// - `out_name` must be a valid pointer. The result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_get_device_name(
    out_name: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_name.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let c_str = CString::new(handle.device_name.clone()).unwrap();
    *out_name = c_str.into_raw();
    PrivStackError::Ok
}}

/// Sets the device name.
///
/// # Safety
/// - `name` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_pairing_set_device_name(name: *const c_char) -> PrivStackError { unsafe {
    if name.is_null() {
        return PrivStackError::NullPointer;
    }

    let name_str = match CStr::from_ptr(name).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    handle.device_name = name_str.to_string();
    PrivStackError::Ok
}}

// ============================================================================
// Personal Sharing Functions
// ============================================================================

/// Shares an entity with a specific peer (personal sharing).
///
/// # Safety
/// - `entity_id` and `peer_id` must be valid null-terminated UTF-8 UUID strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_share_entity_with_peer(
    entity_id: *const c_char,
    peer_id: *const c_char,
) -> PrivStackError { unsafe {
    if entity_id.is_null() || peer_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let eid_str = match CStr::from_ptr(entity_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let pid_str = match CStr::from_ptr(peer_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let eid: EntityId = match eid_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::JsonError,
    };
    let pid: PeerId = match pid_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::JsonError,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    if let Some(policy) = &handle.personal_policy {
        handle.runtime.block_on(policy.share(eid, pid));
    }

    if let Some(orch) = &handle.orchestrator_handle {
        let _ = handle.runtime.block_on(
            orch.send(SyncCommand::ShareEntityWithPeer {
                entity_id: eid,
                peer_id: pid,
            }),
        );
    }

    PrivStackError::Ok
}}

/// Unshares an entity from a specific peer (personal sharing).
///
/// # Safety
/// - `entity_id` and `peer_id` must be valid null-terminated UTF-8 UUID strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_unshare_entity_with_peer(
    entity_id: *const c_char,
    peer_id: *const c_char,
) -> PrivStackError { unsafe {
    if entity_id.is_null() || peer_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let eid_str = match CStr::from_ptr(entity_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let pid_str = match CStr::from_ptr(peer_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let eid: EntityId = match eid_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::JsonError,
    };
    let pid: PeerId = match pid_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::JsonError,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    if let Some(policy) = &handle.personal_policy {
        handle.runtime.block_on(policy.unshare(eid, pid));
    }

    PrivStackError::Ok
}}

/// Lists all peers that have access to a given entity.
/// Returns a JSON array of peer ID strings.
///
/// # Safety
/// - `entity_id` must be a valid null-terminated UTF-8 UUID string.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_list_shared_peers(
    entity_id: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if entity_id.is_null() || out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let eid_str = match CStr::from_ptr(entity_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };
    let eid: EntityId = match eid_str.parse() {
        Ok(id) => id,
        Err(_) => return PrivStackError::JsonError,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let peers: Vec<String> = if let Some(policy) = &handle.personal_policy {
        handle
            .runtime
            .block_on(policy.shared_peers(&eid))
            .into_iter()
            .map(|p| p.to_string())
            .collect()
    } else {
        Vec::new()
    };

    match serde_json::to_string(&peers) {
        Ok(json) => {
            let c_str = CString::new(json).unwrap();
            *out_json = c_str.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

// ============================================================================
// Cloud Sync Functions
// ============================================================================

/// Initializes Google Drive cloud storage.
///
/// # Safety
/// - `client_id` and `client_secret` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_init_google_drive(
    client_id: *const c_char,
    client_secret: *const c_char,
) -> PrivStackError { unsafe {
    if client_id.is_null() || client_secret.is_null() {
        return PrivStackError::NullPointer;
    }

    let client_id_str = match CStr::from_ptr(client_id).to_str() {
        Ok(s) => s.to_string(),
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let client_secret_str = match CStr::from_ptr(client_secret).to_str() {
        Ok(s) => s.to_string(),
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let config = GoogleDriveConfig {
        client_id: client_id_str,
        client_secret: client_secret_str,
        ..Default::default()
    };

    handle.google_drive = Some(GoogleDriveStorage::new(config));
    PrivStackError::Ok
}}

/// Initializes iCloud Drive storage.
///
/// # Safety
/// - `bundle_id` can be null to use the default, or a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_init_icloud(bundle_id: *const c_char) -> PrivStackError { unsafe {
    let bundle_id_str = if bundle_id.is_null() {
        "com.privstack.app".to_string()
    } else {
        match CStr::from_ptr(bundle_id).to_str() {
            Ok(s) => s.to_string(),
            Err(_) => return PrivStackError::InvalidUtf8,
        }
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let config = ICloudConfig {
        bundle_id: bundle_id_str,
        ..Default::default()
    };

    handle.icloud = Some(ICloudStorage::new(config));
    PrivStackError::Ok
}}

/// Starts authentication for a cloud provider.
///
/// # Safety
/// - `out_auth_url` will receive a pointer to a URL string (or null if no auth needed).
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_authenticate(
    provider: CloudProvider,
    out_auth_url: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_auth_url.is_null() {
        return PrivStackError::NullPointer;
    }

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = match provider {
        CloudProvider::GoogleDrive => {
            let storage = match handle.google_drive.as_mut() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.authenticate())
        }
        CloudProvider::ICloud => {
            let storage = match handle.icloud.as_mut() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.authenticate())
        }
    };

    match result {
        Ok(Some(url)) => {
            let c_str = CString::new(url).unwrap();
            *out_auth_url = c_str.into_raw();
            PrivStackError::Ok
        }
        Ok(None) => {
            *out_auth_url = std::ptr::null_mut();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::AuthError,
    }
}}

/// Completes OAuth authentication with an authorization code.
///
/// # Safety
/// - `auth_code` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_complete_auth(
    provider: CloudProvider,
    auth_code: *const c_char,
) -> PrivStackError { unsafe {
    if auth_code.is_null() {
        return PrivStackError::NullPointer;
    }

    let code_str = match CStr::from_ptr(auth_code).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = match provider {
        CloudProvider::GoogleDrive => {
            let storage = match handle.google_drive.as_mut() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.complete_auth(code_str))
        }
        CloudProvider::ICloud => {
            let storage = match handle.icloud.as_mut() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.complete_auth(code_str))
        }
    };

    match result {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::AuthError,
    }
}}

/// Checks if cloud storage is authenticated.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloud_is_authenticated(provider: CloudProvider) -> bool {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };

    match provider {
        CloudProvider::GoogleDrive => handle
            .google_drive
            .as_ref()
            .map_or(false, |s| s.is_authenticated()),
        CloudProvider::ICloud => handle
            .icloud
            .as_ref()
            .map_or(false, |s| s.is_authenticated()),
    }
}

/// Lists files in cloud storage sync folder.
///
/// # Safety
/// - `out_json` will receive a pointer to a JSON array string.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_list_files(
    provider: CloudProvider,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = match provider {
        CloudProvider::GoogleDrive => {
            let storage = match handle.google_drive.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.list_files())
        }
        CloudProvider::ICloud => {
            let storage = match handle.icloud.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.list_files())
        }
    };

    match result {
        Ok(files) => {
            let file_infos: Vec<CloudFileInfo> = files
                .into_iter()
                .map(|f| {
                    let modified_at_ms = f
                        .modified_at
                        .duration_since(std::time::UNIX_EPOCH)
                        .map(|d| d.as_millis() as i64)
                        .unwrap_or(0);
                    CloudFileInfo {
                        id: f.id,
                        name: f.name,
                        path: f.path,
                        size: f.size,
                        modified_at_ms,
                        content_hash: f.content_hash,
                    }
                })
                .collect();

            match serde_json::to_string(&file_infos) {
                Ok(json) => {
                    let c_json = CString::new(json).unwrap();
                    *out_json = c_json.into_raw();
                    PrivStackError::Ok
                }
                Err(_) => PrivStackError::JsonError,
            }
        }
        Err(_) => PrivStackError::CloudError,
    }
}}

/// Uploads a file to cloud storage.
///
/// # Safety
/// - `name` must be a valid null-terminated UTF-8 string.
/// - `data` must be a valid pointer to `data_len` bytes.
/// - `out_json` will receive a pointer to a JSON string with file info.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_upload(
    provider: CloudProvider,
    name: *const c_char,
    data: *const u8,
    data_len: usize,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if name.is_null() || data.is_null() || out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let name_str = match CStr::from_ptr(name).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let content = std::slice::from_raw_parts(data, data_len);

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = match provider {
        CloudProvider::GoogleDrive => {
            let storage = match handle.google_drive.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.upload(name_str, content))
        }
        CloudProvider::ICloud => {
            let storage = match handle.icloud.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.upload(name_str, content))
        }
    };

    match result {
        Ok(f) => {
            let modified_at_ms = f
                .modified_at
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_millis() as i64)
                .unwrap_or(0);
            let file_info = CloudFileInfo {
                id: f.id,
                name: f.name,
                path: f.path,
                size: f.size,
                modified_at_ms,
                content_hash: f.content_hash,
            };

            match serde_json::to_string(&file_info) {
                Ok(json) => {
                    let c_json = CString::new(json).unwrap();
                    *out_json = c_json.into_raw();
                    PrivStackError::Ok
                }
                Err(_) => PrivStackError::JsonError,
            }
        }
        Err(_) => PrivStackError::CloudError,
    }
}}

/// Downloads a file from cloud storage.
///
/// # Safety
/// - `file_id` must be a valid null-terminated UTF-8 string.
/// - `out_data` will receive a pointer to the file data.
/// - `out_len` will receive the data length.
/// - The returned data must be freed with `privstack_free_bytes`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_download(
    provider: CloudProvider,
    file_id: *const c_char,
    out_data: *mut *mut u8,
    out_len: *mut usize,
) -> PrivStackError { unsafe {
    if file_id.is_null() || out_data.is_null() || out_len.is_null() {
        return PrivStackError::NullPointer;
    }

    let file_id_str = match CStr::from_ptr(file_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = match provider {
        CloudProvider::GoogleDrive => {
            let storage = match handle.google_drive.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.download(file_id_str))
        }
        CloudProvider::ICloud => {
            let storage = match handle.icloud.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.download(file_id_str))
        }
    };

    match result {
        Ok(data) => {
            let len = data.len();
            let ptr = Box::into_raw(data.into_boxed_slice()) as *mut u8;
            *out_data = ptr;
            *out_len = len;
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::CloudError,
    }
}}

/// Deletes a file from cloud storage.
///
/// # Safety
/// - `file_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloud_delete(
    provider: CloudProvider,
    file_id: *const c_char,
) -> PrivStackError { unsafe {
    if file_id.is_null() {
        return PrivStackError::NullPointer;
    }

    let file_id_str = match CStr::from_ptr(file_id).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let result = match provider {
        CloudProvider::GoogleDrive => {
            let storage = match handle.google_drive.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.delete(file_id_str))
        }
        CloudProvider::ICloud => {
            let storage = match handle.icloud.as_ref() {
                Some(s) => s,
                None => return PrivStackError::NotInitialized,
            };
            handle.runtime.block_on(storage.delete(file_id_str))
        }
    };

    match result {
        Ok(_) => PrivStackError::Ok,
        Err(_) => PrivStackError::CloudError,
    }
}}

/// Gets the name of a cloud provider.
///
/// # Safety
/// - The returned string is statically allocated and must not be freed.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloud_provider_name(provider: CloudProvider) -> *const c_char {
    match provider {
        CloudProvider::GoogleDrive => b"Google Drive\0".as_ptr() as *const c_char,
        CloudProvider::ICloud => b"iCloud Drive\0".as_ptr() as *const c_char,
    }
}

// ============================================================================
// License Functions
// ============================================================================

/// License plan enum for FFI.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FfiLicensePlan {
    Monthly = 0,
    Annual = 1,
    Perpetual = 2,
    Trial = 3,
}

impl From<LicensePlan> for FfiLicensePlan {
    fn from(lp: LicensePlan) -> Self {
        match lp {
            LicensePlan::Trial => FfiLicensePlan::Trial,
            LicensePlan::Monthly => FfiLicensePlan::Monthly,
            LicensePlan::Annual => FfiLicensePlan::Annual,
            LicensePlan::Perpetual => FfiLicensePlan::Perpetual,
        }
    }
}

/// License status enum for FFI.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FfiLicenseStatus {
    Active = 0,
    Expired = 1,
    Grace = 2,
    ReadOnly = 3,
    NotActivated = 4,
}

impl From<LicenseStatus> for FfiLicenseStatus {
    fn from(ls: LicenseStatus) -> Self {
        match ls {
            LicenseStatus::Active => FfiLicenseStatus::Active,
            LicenseStatus::Expired => FfiLicenseStatus::Expired,
            LicenseStatus::Grace { .. } => FfiLicenseStatus::Grace,
            LicenseStatus::ReadOnly => FfiLicenseStatus::ReadOnly,
            LicenseStatus::NotActivated => FfiLicenseStatus::NotActivated,
        }
    }
}

#[derive(Serialize)]
struct LicenseInfo {
    raw: String,
    plan: String,
    email: String,
    sub: i64,
    status: String,
    issued_at_ms: i64,
    expires_at_ms: Option<i64>,
    grace_days_remaining: Option<u32>,
}

#[derive(Serialize)]
struct ActivationInfo {
    license_key: String,
    plan: String,
    email: String,
    sub: i64,
    activated_at_ms: i64,
    expires_at_ms: Option<i64>,
    device_fingerprint: String,
    status: String,
    is_valid: bool,
    grace_days_remaining: Option<u32>,
}

#[derive(Serialize)]
struct FfiDeviceInfo {
    os_name: String,
    os_version: String,
    hostname: String,
    arch: String,
    fingerprint: String,
}

fn license_error_to_ffi(err: LicenseError) -> PrivStackError {
    match err {
        LicenseError::InvalidKeyFormat(_) => PrivStackError::LicenseInvalidFormat,
        LicenseError::InvalidSignature => PrivStackError::LicenseInvalidSignature,
        LicenseError::InvalidPayload(_) => PrivStackError::LicenseInvalidFormat,
        LicenseError::Expired(_) => PrivStackError::LicenseExpired,
        LicenseError::NotActivated => PrivStackError::LicenseNotActivated,
        LicenseError::ActivationFailed(_) => PrivStackError::LicenseActivationFailed,
        LicenseError::DeviceLimitExceeded(_) => PrivStackError::LicenseActivationFailed,
        LicenseError::Revoked => PrivStackError::LicenseExpired,
        LicenseError::Network(_) => PrivStackError::SyncError,
        LicenseError::Storage(_) => PrivStackError::StorageError,
        LicenseError::Serialization(_) => PrivStackError::JsonError,
    }
}

/// Parses and validates a license key.
///
/// # Safety
/// - `key` must be a valid null-terminated UTF-8 string.
/// - `out_json` will receive a pointer to a JSON string with license info.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_license_parse(
    key: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if key.is_null() || out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let key_str = match CStr::from_ptr(key).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let parsed = match LicenseKey::parse(key_str) {
        Ok(k) => k,
        Err(e) => return license_error_to_ffi(e),
    };

    let status = parsed.status();
    let grace_days = match &status {
        LicenseStatus::Grace { days_remaining } => Some(*days_remaining),
        _ => None,
    };
    let info = LicenseInfo {
        raw: parsed.raw().to_string(),
        plan: format!("{:?}", parsed.license_plan()).to_lowercase(),
        email: parsed.payload().email.clone(),
        sub: parsed.payload().sub,
        status: format!("{:?}", status).to_lowercase(),
        issued_at_ms: parsed.issued_at_secs() * 1000,
        expires_at_ms: parsed.expires_at_secs().map(|s| s * 1000),
        grace_days_remaining: grace_days,
    };

    match serde_json::to_string(&info) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Gets the license plan from a parsed key.
///
/// # Safety
/// - `key` must be a valid null-terminated UTF-8 string.
/// - `out_plan` will receive the license plan.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_license_get_plan(
    key: *const c_char,
    out_plan: *mut FfiLicensePlan,
) -> PrivStackError { unsafe {
    if key.is_null() || out_plan.is_null() {
        return PrivStackError::NullPointer;
    }

    let key_str = match CStr::from_ptr(key).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let parsed = match LicenseKey::parse(key_str) {
        Ok(k) => k,
        Err(e) => return license_error_to_ffi(e),
    };

    *out_plan = parsed.license_plan().into();
    PrivStackError::Ok
}}

/// Gets device information including fingerprint.
///
/// # Safety
/// - `out_json` will receive a pointer to a JSON string with device info.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_device_info(out_json: *mut *mut c_char) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let device_info = DeviceInfo::collect();
    let fingerprint = DeviceFingerprint::generate();

    let info = FfiDeviceInfo {
        os_name: device_info.os_name,
        os_version: device_info.os_version,
        hostname: device_info.hostname,
        arch: device_info.arch,
        fingerprint: fingerprint.id().to_string(),
    };

    match serde_json::to_string(&info) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Generates and returns the device fingerprint.
///
/// # Safety
/// - `out_fingerprint` will receive a pointer to the fingerprint string.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_device_fingerprint(
    out_fingerprint: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if out_fingerprint.is_null() {
        return PrivStackError::NullPointer;
    }

    let fingerprint = DeviceFingerprint::generate();
    let c_str = CString::new(fingerprint.id().to_string()).unwrap();
    *out_fingerprint = c_str.into_raw();

    PrivStackError::Ok
}}

/// Activates a license key (offline activation).
///
/// # Safety
/// - `key` must be a valid null-terminated UTF-8 string.
/// - `out_json` will receive a pointer to a JSON string with activation info.
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_license_activate(
    key: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError { unsafe {
    if key.is_null() || out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let key_str = match CStr::from_ptr(key).to_str() {
        Ok(s) => s,
        Err(_) => return PrivStackError::InvalidUtf8,
    };

    let parsed = match LicenseKey::parse(key_str) {
        Ok(k) => k,
        Err(e) => return license_error_to_ffi(e),
    };

    let fingerprint = DeviceFingerprint::generate();
    let token = format!("offline-{}-{}", parsed.raw(), fingerprint.id());
    let activation = Activation::new(&parsed, fingerprint, token);

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    if let Err(e) = handle.activation_store.save(&activation) {
        return license_error_to_ffi(e);
    }

    let status = activation.status();
    let grace_days = match &status {
        LicenseStatus::Grace { days_remaining } => Some(*days_remaining),
        _ => None,
    };
    let info = ActivationInfo {
        license_key: activation.license_key().to_string(),
        plan: format!("{:?}", activation.license_plan()).to_lowercase(),
        email: activation.email().to_string(),
        sub: activation.sub(),
        activated_at_ms: activation.activated_at().timestamp_millis(),
        expires_at_ms: None,
        device_fingerprint: activation.device_fingerprint().id().to_string(),
        status: format!("{:?}", status).to_lowercase(),
        is_valid: activation.is_valid(),
        grace_days_remaining: grace_days,
    };

    match serde_json::to_string(&info) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            *out_json = c_json.into_raw();
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}}

/// Checks if a valid license is activated.
///
/// # Safety
/// - `out_json` will receive a pointer to a JSON string with activation info (or null if not activated).
/// - The returned string must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_license_check(out_json: *mut *mut c_char) -> PrivStackError { unsafe {
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.activation_store.load() {
        Ok(Some(activation)) => {
            let status = activation.status();
            let grace_days = match &status {
                LicenseStatus::Grace { days_remaining } => Some(*days_remaining),
                _ => None,
            };
            let info = ActivationInfo {
                license_key: activation.license_key().to_string(),
                plan: format!("{:?}", activation.license_plan()).to_lowercase(),
                email: activation.email().to_string(),
                sub: activation.sub(),
                activated_at_ms: activation.activated_at().timestamp_millis(),
                expires_at_ms: None,
                device_fingerprint: activation.device_fingerprint().id().to_string(),
                status: format!("{:?}", status).to_lowercase(),
                is_valid: activation.is_valid(),
                grace_days_remaining: grace_days,
            };

            match serde_json::to_string(&info) {
                Ok(json) => {
                    let c_json = CString::new(json).unwrap();
                    *out_json = c_json.into_raw();
                    PrivStackError::Ok
                }
                Err(_) => PrivStackError::JsonError,
            }
        }
        Ok(None) => {
            *out_json = std::ptr::null_mut();
            PrivStackError::LicenseNotActivated
        }
        Err(e) => license_error_to_ffi(e),
    }
}}

/// Checks if the license is valid and usable.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_license_is_valid() -> bool {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };

    match handle.activation_store.load() {
        Ok(Some(activation)) => activation.is_valid(),
        _ => false,
    }
}

/// Gets the current license status.
///
/// # Safety
/// - `out_status` will receive the license status.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_license_status(
    out_status: *mut FfiLicenseStatus,
) -> PrivStackError { unsafe {
    if out_status.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.activation_store.load() {
        Ok(Some(activation)) => {
            *out_status = activation.status().into();
            PrivStackError::Ok
        }
        Ok(None) => {
            *out_status = FfiLicenseStatus::NotActivated;
            PrivStackError::Ok
        }
        Err(e) => license_error_to_ffi(e),
    }
}}

/// Gets the activated license plan.
///
/// # Safety
/// - `out_plan` will receive the license plan.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_license_activated_plan(
    out_plan: *mut FfiLicensePlan,
) -> PrivStackError { unsafe {
    if out_plan.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.activation_store.load() {
        Ok(Some(activation)) => {
            *out_plan = activation.license_plan().into();
            PrivStackError::Ok
        }
        Ok(None) => PrivStackError::LicenseNotActivated,
        Err(e) => license_error_to_ffi(e),
    }
}}

/// Deactivates the current license.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_license_deactivate() -> PrivStackError {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    match handle.activation_store.clear() {
        Ok(_) => PrivStackError::Ok,
        Err(e) => license_error_to_ffi(e),
    }
}

/// Returns the maximum number of devices for a license plan.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_license_max_devices(plan: FfiLicensePlan) -> u32 {
    match plan {
        FfiLicensePlan::Trial => 1,
        FfiLicensePlan::Monthly => 3,
        FfiLicensePlan::Annual => 5,
        FfiLicensePlan::Perpetual => 5,
    }
}

/// Returns whether a license plan includes priority support.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_license_has_priority_support(plan: FfiLicensePlan) -> bool {
    matches!(plan, FfiLicensePlan::Annual | FfiLicensePlan::Perpetual)
}

// ============================================================================
// Generic SDK Execute Endpoint
// ============================================================================

/// Request structure for the generic execute endpoint.
#[derive(Deserialize)]
struct SdkRequest {
    #[allow(dead_code)]
    plugin_id: String,
    action: String,
    entity_type: String,
    entity_id: Option<String>,
    payload: Option<String>,
    #[allow(dead_code)]
    parameters: Option<std::collections::HashMap<String, String>>,
}

/// Response structure for the generic execute endpoint.
#[derive(Serialize)]
struct SdkResponse {
    success: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    error_code: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error_message: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    data: Option<serde_json::Value>,
}

impl SdkResponse {
    fn ok(data: serde_json::Value) -> Self {
        SdkResponse { success: true, error_code: None, error_message: None, data: Some(data) }
    }
    fn ok_empty() -> Self {
        SdkResponse { success: true, error_code: None, error_message: None, data: None }
    }
    fn err(code: &str, message: &str) -> Self {
        SdkResponse {
            success: false,
            error_code: Some(code.to_string()),
            error_message: Some(message.to_string()),
            data: None,
        }
    }
}

/// Generic SDK execute endpoint. Routes (entity_type, action) to the entity store.
///
/// # Safety
/// - `request_json` must be a valid null-terminated UTF-8 string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_execute(request_json: *const c_char) -> *mut c_char { unsafe {
    let response = execute_inner(request_json);
    let json = serde_json::to_string(&response).unwrap_or_else(|_| {
        r#"{"success":false,"error_code":"json_error","error_message":"Failed to serialize response"}"#.to_string()
    });
    CString::new(json).unwrap_or_default().into_raw()
}}

unsafe fn execute_inner(request_json: *const c_char) -> SdkResponse {
    if request_json.is_null() {
        return SdkResponse::err("null_pointer", "Request JSON is null");
    }

    let json_str = match unsafe { CStr::from_ptr(request_json) }.to_str() {
        Ok(s) => s,
        Err(_) => return SdkResponse::err("invalid_utf8", "Request JSON is not valid UTF-8"),
    };

    let request: SdkRequest = match serde_json::from_str(json_str) {
        Ok(r) => r,
        Err(e) => return SdkResponse::err("json_parse_error", &format!("Failed to parse request: {e}")),
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return SdkResponse::err("not_initialized", "PrivStack runtime not initialized"),
    };

    if handle.entity_registry.has_schema(&request.entity_type) {
        return execute_generic(handle, &request);
    }

    SdkResponse::err("unknown_entity", &format!("No schema registered for entity type: {}. Ensure the plugin registered its EntitySchemas.", request.entity_type))
}

// ========================================================================
// Generic Entity Engine
// ========================================================================

/// Flatten an Entity into a single JSON object by merging the wrapper metadata
/// (id, entity_type, created_at, modified_at, created_by) into the inner `data`.
/// C# plugins expect flat domain objects, not the Entity wrapper.
fn flatten_entity(entity: &Entity) -> serde_json::Value {
    let mut merged = entity.data.clone();
    if let Some(obj) = merged.as_object_mut() {
        obj.insert("id".into(), serde_json::Value::String(entity.id.clone()));
        obj.insert("entity_type".into(), serde_json::Value::String(entity.entity_type.clone()));
        obj.insert("created_at".into(), serde_json::json!(entity.created_at));
        obj.insert("modified_at".into(), serde_json::json!(entity.modified_at));
        obj.insert("created_by".into(), serde_json::Value::String(entity.created_by.clone()));
    }
    merged
}

/// Flatten a list of entities into a JSON array of flat objects.
fn flatten_entities(entities: &[Entity]) -> serde_json::Value {
    serde_json::Value::Array(entities.iter().map(flatten_entity).collect())
}

fn execute_generic(handle: &PrivStackHandle, req: &SdkRequest) -> SdkResponse {
    let schema = match handle.entity_registry.get_schema(&req.entity_type) {
        Some(s) => s,
        None => return SdkResponse::err("unknown_entity", &format!("No schema for: {}", req.entity_type)),
    };
    let handler = handle.entity_registry.get_handler(&req.entity_type);

    match req.action.as_str() {
        "create" | "update" => {
            let payload = match req.payload.as_deref() {
                Some(p) => p,
                None => return SdkResponse::err("missing_payload", "Create/update requires a payload"),
            };
            let data: serde_json::Value = match serde_json::from_str(payload) {
                Ok(v) => v,
                Err(e) => return SdkResponse::err("json_error", &format!("Invalid JSON: {e}")),
            };

            let now = chrono::Utc::now().timestamp_millis();
            let id = req.entity_id.clone().unwrap_or_else(|| Uuid::new_v4().to_string());

            let override_created = req.parameters.as_ref()
                .and_then(|p| p.get("created_at"))
                .and_then(|v| v.parse::<i64>().ok());
            let override_modified = req.parameters.as_ref()
                .and_then(|p| p.get("modified_at"))
                .and_then(|v| v.parse::<i64>().ok());

            let (created_at, created_by) = if req.action == "update" {
                match handle.entity_store.get_entity(&id) {
                    Ok(Some(existing)) => (
                        override_created.unwrap_or(existing.created_at),
                        existing.created_by,
                    ),
                    _ => (override_created.unwrap_or(now), handle.peer_id.to_string()),
                }
            } else {
                (override_created.unwrap_or(now), handle.peer_id.to_string())
            };

            // Inject local_only flag into data if parameter is set
            let mut data = data;
            let is_local_only = req.parameters.as_ref()
                .and_then(|p| p.get("local_only"))
                .map(|v| v == "true")
                .unwrap_or(false);
            if is_local_only {
                if let Some(obj) = data.as_object_mut() {
                    obj.insert("local_only".into(), serde_json::Value::Bool(true));
                }
            }

            let mut entity = Entity {
                id,
                entity_type: req.entity_type.clone(),
                data,
                created_at,
                modified_at: override_modified.unwrap_or(now),
                created_by,
            };

            if let Some(h) = handler {
                if let Err(msg) = h.validate(&entity) {
                    return SdkResponse::err("validation_error", &msg);
                }
            }

            match handle.entity_store.save_entity(&entity, schema) {
                Ok(_) => {
                    if let Some(h) = handler {
                        h.on_after_load(&mut entity);
                    }
                    SdkResponse::ok(flatten_entity(&entity))
                }
                Err(e) => SdkResponse::err("storage_error", &format!("Failed to save: {e}")),
            }
        }
        "read" => {
            let id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Read requires entity_id"),
            };
            match handle.entity_store.get_entity(id) {
                Ok(Some(mut entity)) => {
                    if let Some(h) = handler {
                        h.on_after_load(&mut entity);
                    }
                    SdkResponse::ok(flatten_entity(&entity))
                }
                Ok(None) => SdkResponse::err("not_found", &format!("Entity not found: {id}")),
                Err(e) => SdkResponse::err("storage_error", &format!("Read failed: {e}")),
            }
        }
        "count" => {
            let include_trashed = req.parameters.as_ref()
                .and_then(|p| p.get("include_trashed"))
                .map(|v| v == "true")
                .unwrap_or(false);

            match handle.entity_store.count_entities(&req.entity_type, include_trashed) {
                Ok(count) => SdkResponse::ok(serde_json::json!({"count": count})),
                Err(e) => SdkResponse::err("storage_error", &format!("Count failed: {e}")),
            }
        }
        "read_list" => {
            let limit = req.parameters.as_ref()
                .and_then(|p| p.get("limit"))
                .and_then(|v| v.parse().ok());
            let offset = req.parameters.as_ref()
                .and_then(|p| p.get("offset"))
                .and_then(|v| v.parse().ok());
            let include_trashed = req.parameters.as_ref()
                .and_then(|p| p.get("include_trashed"))
                .map(|v| v == "true")
                .unwrap_or(false);

            match handle.entity_store.list_entities(&req.entity_type, include_trashed, limit, offset) {
                Ok(mut entities) => {
                    if let Some(h) = handler {
                        for entity in &mut entities {
                            h.on_after_load(entity);
                        }
                    }
                    SdkResponse::ok(flatten_entities(&entities))
                }
                Err(e) => SdkResponse::err("storage_error", &format!("List failed: {e}")),
            }
        }
        "delete" => {
            let id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Delete requires entity_id"),
            };
            match handle.entity_store.delete_entity(id) {
                Ok(_) => SdkResponse::ok_empty(),
                Err(e) => SdkResponse::err("storage_error", &format!("Delete failed: {e}")),
            }
        }
        "trash" => {
            let id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Trash requires entity_id"),
            };
            match handle.entity_store.trash_entity(id) {
                Ok(_) => SdkResponse::ok_empty(),
                Err(e) => SdkResponse::err("storage_error", &format!("Trash failed: {e}")),
            }
        }
        "restore" => {
            let id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Restore requires entity_id"),
            };
            match handle.entity_store.restore_entity(id) {
                Ok(_) => SdkResponse::ok_empty(),
                Err(e) => SdkResponse::err("storage_error", &format!("Restore failed: {e}")),
            }
        }
        "query" => {
            let mut filters: Vec<(String, serde_json::Value)> = req.payload.as_deref()
                .and_then(|p| serde_json::from_str(p).ok())
                .unwrap_or_default();
            let limit = req.parameters.as_ref()
                .and_then(|p| p.get("limit"))
                .and_then(|v| v.parse().ok());

            // Also extract field-level filters from parameters (excluding reserved keys)
            const RESERVED: &[&str] = &["limit", "offset", "query", "search", "action", "include_trashed"];
            if let Some(params) = &req.parameters {
                for (k, v) in params {
                    if !RESERVED.contains(&k.as_str()) {
                        filters.push((k.clone(), serde_json::Value::String(v.clone())));
                    }
                }
            }

            match handle.entity_store.query_entities(&req.entity_type, &filters, limit) {
                Ok(mut entities) => {
                    if let Some(h) = handler {
                        for entity in &mut entities {
                            h.on_after_load(entity);
                        }
                    }
                    SdkResponse::ok(flatten_entities(&entities))
                }
                Err(e) => SdkResponse::err("storage_error", &format!("Query failed: {e}")),
            }
        }
        "link" => {
            let target_type = req.parameters.as_ref().and_then(|p| p.get("target_type"));
            let target_id = req.parameters.as_ref().and_then(|p| p.get("target_id"));
            let source_id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Link requires entity_id"),
            };
            match (target_type, target_id) {
                (Some(tt), Some(ti)) => {
                    match handle.entity_store.save_link(&req.entity_type, source_id, tt, ti) {
                        Ok(_) => SdkResponse::ok_empty(),
                        Err(e) => SdkResponse::err("storage_error", &format!("Link failed: {e}")),
                    }
                }
                _ => SdkResponse::err("missing_params", "Link requires target_type and target_id parameters"),
            }
        }
        "unlink" => {
            let target_type = req.parameters.as_ref().and_then(|p| p.get("target_type"));
            let target_id = req.parameters.as_ref().and_then(|p| p.get("target_id"));
            let source_id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Unlink requires entity_id"),
            };
            match (target_type, target_id) {
                (Some(tt), Some(ti)) => {
                    match handle.entity_store.remove_link(&req.entity_type, source_id, tt, ti) {
                        Ok(_) => SdkResponse::ok_empty(),
                        Err(e) => SdkResponse::err("storage_error", &format!("Unlink failed: {e}")),
                    }
                }
                _ => SdkResponse::err("missing_params", "Unlink requires target_type and target_id parameters"),
            }
        }
        "get_links" => {
            let source_id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "get_links requires entity_id"),
            };
            match handle.entity_store.get_links_from(&req.entity_type, source_id) {
                Ok(links) => {
                    let link_data: Vec<serde_json::Value> = links.iter().map(|(t, id)| {
                        serde_json::json!({"target_type": t, "target_id": id})
                    }).collect();
                    SdkResponse::ok(serde_json::Value::Array(link_data))
                }
                Err(e) => SdkResponse::err("storage_error", &format!("Get links failed: {e}")),
            }
        }
        "command" => {
            let entity_id = match &req.entity_id {
                Some(id) => id,
                None => return SdkResponse::err("missing_id", "Command requires entity_id"),
            };
            let command = req.parameters.as_ref().and_then(|p| p.get("command"));
            let command = match command {
                Some(c) => c.as_str(),
                None => return SdkResponse::err("missing_params", "Command requires 'command' parameter"),
            };

            match (req.entity_type.as_str(), command) {
                ("contact_group", "add_member") | ("contact_group", "remove_member") => {
                    let contact_id = match req.parameters.as_ref().and_then(|p| p.get("contact_id")) {
                        Some(cid) => cid.clone(),
                        None => return SdkResponse::err("missing_params", "add_member/remove_member requires 'contact_id' parameter"),
                    };

                    let mut entity = match handle.entity_store.get_entity(entity_id) {
                        Ok(Some(e)) => e,
                        Ok(None) => return SdkResponse::err("not_found", &format!("Entity not found: {entity_id}")),
                        Err(e) => return SdkResponse::err("storage_error", &format!("Read failed: {e}")),
                    };

                    // Get or create contact_ids array
                    let contact_ids = entity.data.as_object_mut()
                        .and_then(|obj| {
                            if !obj.contains_key("contact_ids") {
                                obj.insert("contact_ids".into(), serde_json::json!([]));
                            }
                            obj.get_mut("contact_ids")
                        })
                        .and_then(|v| v.as_array_mut());

                    let contact_ids = match contact_ids {
                        Some(arr) => arr,
                        None => return SdkResponse::err("data_error", "Failed to access contact_ids array"),
                    };

                    if command == "add_member" {
                        let already_exists = contact_ids.iter().any(|v| v.as_str() == Some(&contact_id));
                        if !already_exists {
                            contact_ids.push(serde_json::Value::String(contact_id));
                        }
                    } else {
                        contact_ids.retain(|v| v.as_str() != Some(&contact_id));
                    }

                    // Update contact_count
                    let count = contact_ids.len();
                    if let Some(obj) = entity.data.as_object_mut() {
                        obj.insert("contact_count".into(), serde_json::json!(count));
                    }

                    entity.modified_at = chrono::Utc::now().timestamp_millis();

                    match handle.entity_store.save_entity(&entity, schema) {
                        Ok(_) => {
                            let mut result = entity.clone();
                            if let Some(h) = handler {
                                h.on_after_load(&mut result);
                            }
                            SdkResponse::ok(flatten_entity(&result))
                        }
                        Err(e) => SdkResponse::err("storage_error", &format!("Failed to save: {e}")),
                    }
                }
                _ => SdkResponse::err("unknown_command", &format!("Unknown command '{}' for entity type '{}'", command, req.entity_type)),
            }
        }
        other => SdkResponse::err("unknown_action", &format!("Unknown action: {other}")),
    }
}

/// Register an entity type schema at runtime.
///
/// # Safety
/// `schema_json` must be a valid null-terminated UTF-8 JSON string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_register_entity_type(schema_json: *const c_char) -> c_int { unsafe {
    if schema_json.is_null() {
        return -1;
    }

    let json_str = match CStr::from_ptr(schema_json).to_str() {
        Ok(s) => s,
        Err(_) => return -2,
    };

    let schema: EntitySchema = match serde_json::from_str(json_str) {
        Ok(s) => s,
        Err(_) => return -3,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return -4,
    };

    handle.entity_registry.register_schema(schema);
    0
}}

/// Search across all registered entity types.
///
/// # Safety
/// `query_json` must be a valid null-terminated UTF-8 JSON string with fields:
///   `query` (string), `entity_types` (optional string array), `limit` (optional int).
/// The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_search(query_json: *const c_char) -> *mut c_char { unsafe {
    let response = search_inner(query_json);
    let json = serde_json::to_string(&response).unwrap_or_else(|_| {
        r#"{"success":false,"error_code":"json_error","error_message":"Failed to serialize response"}"#.to_string()
    });
    CString::new(json).unwrap_or_default().into_raw()
}}

unsafe fn search_inner(query_json: *const c_char) -> SdkResponse {
    if query_json.is_null() {
        return SdkResponse::err("null_pointer", "Query JSON is null");
    }

    let json_str = match unsafe { CStr::from_ptr(query_json) }.to_str() {
        Ok(s) => s,
        Err(_) => return SdkResponse::err("invalid_utf8", "Query JSON is not valid UTF-8"),
    };

    #[derive(Deserialize)]
    struct SearchQuery {
        query: String,
        entity_types: Option<Vec<String>>,
        limit: Option<usize>,
    }

    let sq: SearchQuery = match serde_json::from_str(json_str) {
        Ok(q) => q,
        Err(e) => return SdkResponse::err("json_parse_error", &format!("Invalid query: {e}")),
    };

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return SdkResponse::err("not_initialized", "PrivStack runtime not initialized"),
    };

    let types_refs: Option<Vec<&str>> = sq.entity_types.as_ref().map(|v| v.iter().map(|s| s.as_str()).collect());
    let limit = sq.limit.unwrap_or(50);

    match handle.entity_store.search(&sq.query, types_refs.as_deref(), limit) {
        Ok(entities) => SdkResponse::ok(flatten_entities(&entities)),
        Err(e) => SdkResponse::err("storage_error", &format!("Search failed: {e}")),
    }
}

// ============================================================================
// Plugin Host FFI (requires wasm-plugins feature)
// ============================================================================

#[cfg(feature = "wasm-plugins")]
mod plugin_ffi {
use super::*;

/// Loads a Wasm plugin into the plugin host manager.
///
/// # Safety
/// - `metadata_json` must be a valid null-terminated UTF-8 JSON string.
/// - `schemas_json` must be a valid null-terminated UTF-8 JSON array string.
/// - `permissions_json` must be a valid null-terminated UTF-8 JSON string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_load(
    metadata_json: *const c_char,
    schemas_json: *const c_char,
    permissions_json: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let metadata_str = match nullable_cstr_to_str(metadata_json) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };
    let schemas_str = match nullable_cstr_to_str(schemas_json) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };
    let permissions_str = match nullable_cstr_to_str(permissions_json) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    let metadata: privstack_plugin_host::WitPluginMetadata = match serde_json::from_str(metadata_str) {
        Ok(m) => m,
        Err(_) => return PrivStackError::JsonError,
    };
    let schemas: Vec<privstack_plugin_host::WitEntitySchema> = match serde_json::from_str(schemas_str) {
        Ok(s) => s,
        Err(_) => return PrivStackError::JsonError,
    };
    let permissions: privstack_plugin_host::PermissionSet = match serde_json::from_str(permissions_str) {
        Ok(p) => p,
        Err(_) => return PrivStackError::JsonError,
    };

    let resource_limits = privstack_plugin_host::ResourceLimits::first_party();

    match handle.plugin_host.load_plugin(metadata, schemas, permissions, resource_limits) {
        Ok(()) => PrivStackError::Ok,
        Err(privstack_plugin_host::PluginHostError::PolicyDenied(_)) => PrivStackError::PluginPermissionDenied,
        Err(privstack_plugin_host::PluginHostError::PluginAlreadyLoaded(_)) => PrivStackError::PluginError,
        Err(_) => PrivStackError::PluginError,
    }
}}

/// Unloads a plugin from the host manager.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_unload(plugin_id: *const c_char) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    match handle.plugin_host.unload_plugin(id) {
        Ok(()) => PrivStackError::Ok,
        Err(privstack_plugin_host::PluginHostError::PluginNotFound(_)) => PrivStackError::PluginNotFound,
        Err(_) => PrivStackError::PluginError,
    }
}}

/// Routes an SDK message to a loaded plugin. Returns a JSON response string.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
/// - `message_json` must be a valid null-terminated UTF-8 JSON string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_route_sdk(
    plugin_id: *const c_char,
    message_json: *const c_char,
) -> *mut c_char { unsafe {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return to_c_string(r#"{"success":false,"error":"not_initialized","error_code":6}"#),
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return to_c_string(r#"{"success":false,"error":"null_plugin_id","error_code":1}"#),
    };
    let msg_str = match nullable_cstr_to_str(message_json) {
        Some(s) => s,
        None => return to_c_string(r#"{"success":false,"error":"null_message","error_code":1}"#),
    };

    let message: privstack_plugin_host::WitSdkMessage = match serde_json::from_str(msg_str) {
        Ok(m) => m,
        Err(e) => return to_c_string(&format!(r#"{{"success":false,"error":"invalid_json: {}","error_code":3}}"#, e)),
    };

    match handle.plugin_host.route_sdk_message(id, &message) {
        Ok(resp) => {
            let json = serde_json::to_string(&resp).unwrap_or_else(|_| {
                r#"{"success":false,"error":"serialization_error","error_code":3}"#.to_string()
            });
            to_c_string(&json)
        }
        Err(e) => to_c_string(&format!(r#"{{"success":false,"error":"{}","error_code":23}}"#, e)),
    }
}}

/// Lists all loaded plugins as a JSON array of metadata objects.
///
/// # Safety
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_plugin_list() -> *mut c_char {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return to_c_string("[]"),
    };

    let plugins = handle.plugin_host.list_plugins();
    let json = serde_json::to_string(&plugins).unwrap_or_else(|_| "[]".to_string());
    to_c_string(&json)
}

/// Returns navigation items for all loaded plugins as JSON array.
///
/// # Safety
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_plugin_get_nav_items() -> *mut c_char {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return to_c_string("[]"),
    };

    let items = handle.plugin_host.get_navigation_items();
    let json = serde_json::to_string(&items).unwrap_or_else(|_| "[]".to_string());
    to_c_string(&json)
}

/// Returns the number of loaded plugins.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_plugin_count() -> c_int {
    let handle = HANDLE.lock().unwrap();
    match handle.as_ref() {
        Some(h) => h.plugin_host.plugin_count() as c_int,
        None => 0,
    }
}

/// Checks if a plugin is loaded.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_is_loaded(plugin_id: *const c_char) -> bool { unsafe {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return false,
    };

    handle.plugin_host.is_loaded(id)
}}

/// Gets resource metrics for a specific plugin as JSON.
///
/// Returns a JSON object with memory, CPU fuel, and disk usage metrics.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_get_metrics(plugin_id: *const c_char) -> *mut c_char { unsafe {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return to_c_string(r#"{"error":"not initialized"}"#),
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return to_c_string(r#"{"error":"invalid plugin_id"}"#),
    };

    match handle.plugin_host.get_plugin_metrics(id) {
        Ok(metrics) => {
            let json = serde_json::to_string(&metrics).unwrap_or_else(|e| {
                format!(r#"{{"error":"serialization failed: {}"}}"#, e)
            });
            to_c_string(&json)
        }
        Err(e) => to_c_string(&format!(r#"{{"error":"{}"}}"#, e)),
    }
}}

/// Gets resource metrics for all loaded plugins as JSON.
///
/// Returns a JSON array of objects, each containing plugin_id and metrics.
///
/// # Safety
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_plugin_get_all_metrics() -> *mut c_char {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return to_c_string("[]"),
    };

    let all_metrics = handle.plugin_host.get_all_plugin_metrics();

    // Convert to a JSON-serializable format
    #[derive(serde::Serialize)]
    struct PluginMetricsEntry {
        plugin_id: String,
        #[serde(flatten)]
        metrics: privstack_plugin_host::PluginResourceMetrics,
    }

    let entries: Vec<PluginMetricsEntry> = all_metrics
        .into_iter()
        .map(|(id, metrics)| PluginMetricsEntry {
            plugin_id: id,
            metrics,
        })
        .collect();

    let json = serde_json::to_string(&entries).unwrap_or_else(|_| "[]".to_string());
    to_c_string(&json)
}

/// Gets commands from a specific plugin as JSON.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_get_commands(plugin_id: *const c_char) -> *mut c_char { unsafe {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return to_c_string("[]"),
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return to_c_string("[]"),
    };

    match handle.plugin_host.get_commands(id) {
        Ok(cmds) => {
            let json = serde_json::to_string(&cmds).unwrap_or_else(|_| "[]".to_string());
            to_c_string(&json)
        }
        Err(_) => to_c_string("[]"),
    }
}}

/// Gets all linkable item providers across loaded plugins as JSON.
///
/// # Safety
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_plugin_get_link_providers() -> *mut c_char {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return to_c_string("[]"),
    };

    let providers = handle.plugin_host.get_all_link_providers();
    eprintln!("[FFI] get_link_providers returning {} providers", providers.len());
    for p in &providers {
        eprintln!("[FFI]   provider: {} -> link_type={}", p.plugin_id, p.link_type);
    }
    let json = serde_json::to_string(&providers).unwrap_or_else(|_| "[]".to_string());
    to_c_string(&json)
}

/// Searches across all loaded plugins for linkable items matching a query.
/// Returns a JSON array of results.
///
/// # Safety
/// - `query` must be a valid null-terminated UTF-8 string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_search_items(
    query: *const c_char,
    max_results: c_int,
) -> *mut c_char { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return to_c_string("[]"),
    };

    let q = match nullable_cstr_to_str(query) {
        Some(s) => s,
        None => return to_c_string("[]"),
    };

    let results = handle.plugin_host.query_all_linkable_items(q, max_results as u32);
    let json = serde_json::to_string(&results).unwrap_or_else(|_| "[]".to_string());
    to_c_string(&json)
}}

/// Navigates to a specific item within a plugin via its deep-link-target export.
///
/// # Safety
/// - `plugin_id` and `item_id` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_navigate_to_item(
    plugin_id: *const c_char,
    item_id: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let pid = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    let iid = match nullable_cstr_to_str(item_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    match handle.plugin_host.navigate_to_item(pid, iid) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::PluginError,
    }
}}

/// Navigates to a specific item within a plugin and returns its view data.
/// Combines navigate_to_item + get_view_data in a single call for hover prefetch.
///
/// This is safe for cross-plugin prefetch (when the target plugin is not currently
/// displayed). For same-plugin prefetch, use with caution as it changes the plugin's
/// internal state.
///
/// Returns JSON string (caller must free with `privstack_free_string`).
///
/// # Safety
/// - `plugin_id` and `item_id` must be valid null-terminated UTF-8 strings.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_get_entity_view_data(
    plugin_id: *const c_char,
    item_id: *const c_char,
) -> *mut c_char { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return to_c_string(r#"{}"#),
    };

    let pid = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return to_c_string(r#"{}"#),
    };

    let iid = match nullable_cstr_to_str(item_id) {
        Some(s) => s,
        None => return to_c_string(r#"{}"#),
    };

    match handle.plugin_host.get_entity_view_data(pid, iid) {
        Ok(json) => to_c_string(&json),
        Err(e) => {
            eprintln!(
                "[privstack-ffi] get_entity_view_data({}, {}) failed: {:?}",
                pid, iid, e
            );
            to_c_string(r#"{}"#)
        }
    }
}}

// ============================================================
// Plugin Install / Update (P6.4)
// ============================================================

/// Installs a plugin from a .ppk file path. Validates the manifest and loads the Wasm module.
/// Returns Ok on success, PluginError on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_install_ppk(
    ppk_path: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let path_str = match nullable_cstr_to_str(ppk_path) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    let path = Path::new(path_str);
    if !path.exists() {
        return PrivStackError::NotFound;
    }

    // Read and validate the .ppk file
    let file = match std::fs::File::open(path) {
        Ok(f) => f,
        Err(_) => return PrivStackError::StorageError,
    };

    let reader = std::io::BufReader::new(file);
    let package = match privstack_ppk::PpkPackage::open(reader) {
        Ok(p) => p,
        Err(_) => return PrivStackError::PluginError,
    };

    if package.manifest.validate().is_err() {
        return PrivStackError::PluginError;
    }

    // Convert PpkManifest to WitPluginMetadata
    let m = &package.manifest;
    let category = match m.category.to_lowercase().as_str() {
        "productivity" => privstack_plugin_host::WitPluginCategory::Productivity,
        "security" => privstack_plugin_host::WitPluginCategory::Security,
        "communication" => privstack_plugin_host::WitPluginCategory::Communication,
        "information" => privstack_plugin_host::WitPluginCategory::Information,
        "extension" => privstack_plugin_host::WitPluginCategory::Extension,
        _ => privstack_plugin_host::WitPluginCategory::Utility,
    };

    let metadata = privstack_plugin_host::WitPluginMetadata {
        id: m.id.clone(),
        name: m.name.clone(),
        description: m.description.clone(),
        version: m.version.clone(),
        author: m.author.clone(),
        icon: m.icon.clone(),
        navigation_order: m.navigation_order,
        category,
        can_disable: m.can_disable,
        is_experimental: m.is_experimental,
    };

    // Convert schemas
    let schemas: Vec<privstack_plugin_host::WitEntitySchema> = m.schemas.iter().map(|s| {
        let merge_strategy = match s.merge_strategy.as_str() {
            "lww_document" => privstack_plugin_host::WitMergeStrategy::LwwDocument,
            "lww_per_field" => privstack_plugin_host::WitMergeStrategy::LwwPerField,
            _ => privstack_plugin_host::WitMergeStrategy::Custom,
        };
        privstack_plugin_host::WitEntitySchema {
            entity_type: s.entity_type.clone(),
            indexed_fields: s.indexed_fields.iter().map(|f| {
                let field_type = match f.field_type.as_str() {
                    "text" => privstack_plugin_host::WitFieldType::Text,
                    "tag" => privstack_plugin_host::WitFieldType::Tag,
                    "date_time" => privstack_plugin_host::WitFieldType::DateTime,
                    "number" => privstack_plugin_host::WitFieldType::Number,
                    "bool" => privstack_plugin_host::WitFieldType::Boolean,
                    "vector" => privstack_plugin_host::WitFieldType::Vector,
                    "counter" => privstack_plugin_host::WitFieldType::Counter,
                    "relation" => privstack_plugin_host::WitFieldType::Relation,
                    "decimal" => privstack_plugin_host::WitFieldType::Decimal,
                    "json" => privstack_plugin_host::WitFieldType::Json,
                    _ => privstack_plugin_host::WitFieldType::Text,
                };
                privstack_plugin_host::WitIndexedField {
                    field_path: f.field_path.clone(),
                    field_type,
                    searchable: f.searchable,
                    vector_dim: None,
                    enum_options: None,
                }
            }).collect(),
            merge_strategy,
        }
    }).collect();

    // Determine resource limits and permissions based on first-party status
    let is_first_party = m.is_first_party();
    let resource_limits = if is_first_party {
        privstack_plugin_host::ResourceLimits::first_party()
    } else {
        privstack_plugin_host::ResourceLimits::third_party()
    };
    let permissions = if is_first_party {
        privstack_plugin_host::PermissionSet::default_first_party()
    } else {
        privstack_plugin_host::PermissionSet::default_third_party()
    };

    match handle.plugin_host.load_plugin(metadata, schemas, permissions, resource_limits) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::PluginError,
    }
}}

// ============================================================
// Plugin Wasm Runtime (Phase 4: real command routing)
// ============================================================

/// Loads a plugin from a .wasm component file path.
/// Returns the plugin ID via out_plugin_id on success.
///
/// # Safety
/// - `wasm_path` must be a valid null-terminated UTF-8 file path.
/// - `permissions_json` must be a valid null-terminated UTF-8 JSON string.
/// - `out_plugin_id` receives a heap-allocated C string (free with `privstack_free_string`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_load_wasm(
    wasm_path: *const c_char,
    permissions_json: *const c_char,
    out_plugin_id: *mut *mut c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let path_str = match nullable_cstr_to_str(wasm_path) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    let permissions: privstack_plugin_host::PermissionSet = if let Some(p_str) =
        nullable_cstr_to_str(permissions_json)
    {
        serde_json::from_str(p_str).unwrap_or_else(|_| {
            privstack_plugin_host::PermissionSet::default_first_party()
        })
    } else {
        privstack_plugin_host::PermissionSet::default_first_party()
    };

    let resource_limits = privstack_plugin_host::ResourceLimits::first_party();
    let path = Path::new(path_str);

    match handle
        .plugin_host
        .load_plugin_from_wasm(path, permissions, resource_limits)
    {
        Ok(plugin_id) => {
            if !out_plugin_id.is_null() {
                *out_plugin_id = to_c_string(&plugin_id);
            }
            PrivStackError::Ok
        }
        Err(privstack_plugin_host::PluginHostError::PolicyDenied(_)) => {
            PrivStackError::PluginPermissionDenied
        }
        Err(privstack_plugin_host::PluginHostError::PluginAlreadyLoaded(_)) => {
            PrivStackError::PluginError
        }
        Err(e) => {
            eprintln!("[privstack-ffi] Failed to load Wasm plugin from {}: {:?}", path_str, e);
            PrivStackError::PluginError
        }
    }
}}

/// Loads multiple Wasm plugins in parallel (compilation is concurrent).
///
/// Input: JSON array `[{"path": "...", "permissions": {...}}, ...]`
/// Output: JSON array `[{"plugin_id": "...", "error": null}, ...]`
///
/// # Safety
/// - `plugins_json` must be a valid null-terminated UTF-8 C string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_load_wasm_batch(
    plugins_json: *const c_char,
) -> *mut c_char { unsafe {
    #[derive(Deserialize)]
    struct BatchEntry {
        path: String,
        #[serde(default)]
        permissions: Option<serde_json::Value>,
    }

    #[derive(Serialize)]
    struct BatchResult {
        plugin_id: Option<String>,
        error: Option<String>,
    }

    let json_str = match nullable_cstr_to_str(plugins_json) {
        Some(s) => s,
        None => return to_c_string("[]"),
    };

    let entries: Vec<BatchEntry> = match serde_json::from_str(json_str) {
        Ok(v) => v,
        Err(e) => {
            let err = vec![BatchResult {
                plugin_id: None,
                error: Some(format!("invalid batch JSON: {e}")),
            }];
            return to_c_string(&serde_json::to_string(&err).unwrap_or_default());
        }
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => {
            let results: Vec<BatchResult> = entries
                .iter()
                .map(|_| BatchResult {
                    plugin_id: None,
                    error: Some("not initialized".into()),
                })
                .collect();
            return to_c_string(&serde_json::to_string(&results).unwrap_or_default());
        }
    };

    let tuples: Vec<_> = entries
        .into_iter()
        .map(|e| {
            let perms: privstack_plugin_host::PermissionSet = e
                .permissions
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_else(privstack_plugin_host::PermissionSet::default_first_party);
            let limits = privstack_plugin_host::ResourceLimits::first_party();
            (std::path::PathBuf::from(e.path), perms, limits)
        })
        .collect();

    let results = handle.plugin_host.load_plugins_from_wasm_parallel(tuples);

    let batch_results: Vec<BatchResult> = results
        .into_iter()
        .map(|r| match r {
            Ok(id) => BatchResult {
                plugin_id: Some(id),
                error: None,
            },
            Err(e) => BatchResult {
                plugin_id: None,
                error: Some(format!("{e:?}")),
            },
        })
        .collect();

    to_c_string(&serde_json::to_string(&batch_results).unwrap_or_default())
}}

/// Sends a named command to a plugin's `handle_command()` export.
/// Returns JSON response string (caller must free with `privstack_free_string`).
///
/// # Safety
/// - All string parameters must be valid null-terminated UTF-8.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_send_command(
    plugin_id: *const c_char,
    command_name: *const c_char,
    args_json: *const c_char,
) -> *mut c_char { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => {
            return to_c_string(
                r#"{"success":false,"error":"not_initialized","error_code":6}"#,
            )
        }
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => {
            return to_c_string(
                r#"{"success":false,"error":"null_plugin_id","error_code":1}"#,
            )
        }
    };
    let cmd = match nullable_cstr_to_str(command_name) {
        Some(s) => s,
        None => {
            return to_c_string(
                r#"{"success":false,"error":"null_command_name","error_code":1}"#,
            )
        }
    };
    let args = nullable_cstr_to_str(args_json).unwrap_or("{}");

    match handle.plugin_host.send_command(id, cmd, args) {
        Ok(result_json) => to_c_string(&result_json),
        Err(e) => to_c_string(&format!(
            r#"{{"success":false,"error":"{}","error_code":23}}"#,
            e
        )),
    }
}}

/// Fetch a URL on behalf of a plugin, checking its Network permission.
/// Returns the response body bytes. Caller must free with `privstack_free_bytes`.
///
/// # Safety
/// - `plugin_id` and `url` must be valid null-terminated UTF-8.
/// - `out_data` and `out_len` must be valid pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_fetch_url(
    plugin_id: *const c_char,
    url: *const c_char,
    out_data: *mut *mut u8,
    out_len: *mut usize,
) -> PrivStackError { unsafe {
    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };
    let url_str = match nullable_cstr_to_str(url) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    match handle.plugin_host.fetch_url_for_plugin(id, url_str) {
        Ok(bytes) => {
            let len = bytes.len();
            let ptr = if len > 0 {
                let layout = std::alloc::Layout::from_size_align(len, 1).unwrap();
                let p = std::alloc::alloc(layout);
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), p, len);
                p
            } else {
                std::ptr::null_mut()
            };
            *out_data = ptr;
            *out_len = len;
            PrivStackError::Ok
        }
        Err(e) => {
            *out_data = std::ptr::null_mut();
            *out_len = 0;
            eprintln!("[privstack_ffi] plugin fetch_url failed: plugin={id} url={url_str} error={e}");
            PrivStackError::PluginPermissionDenied
        }
    }
}}

/// Gets the view state JSON from a plugin's `get_view_state()` export.
/// Returns JSON string (caller must free with `privstack_free_string`).
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_get_view_state(
    plugin_id: *const c_char,
) -> *mut c_char { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => {
            return to_c_string(r#"{"components":{"type":"error","message":"Core not initialized"}}"#);
        }
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => {
            return to_c_string(r#"{"components":{"type":"error","message":"Invalid plugin ID (null)"}}"#);
        }
    };

    match handle.plugin_host.get_view_state(id) {
        Ok(json) => to_c_string(&json),
        Err(e) => {
            eprintln!("[privstack-ffi] get_view_state({}) failed: {:?}", id, e);
            let msg = format!(
                r#"{{"components":{{"type":"error","message":"get_view_state failed: {}"}}}}"#,
                e.to_string().replace('"', "'")
            );
            to_c_string(&msg)
        }
    }
}}

/// Gets the raw view data JSON from a plugin's `get_view_data()` export.
/// Used for host-side template evaluation. Returns JSON string (caller must
/// free with `privstack_free_string`).
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
/// - The returned pointer must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_get_view_data(
    plugin_id: *const c_char,
) -> *mut c_char { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => {
            return to_c_string(r#"{}"#);
        }
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => {
            return to_c_string(r#"{}"#);
        }
    };

    match handle.plugin_host.get_view_data(id) {
        Ok(json) => to_c_string(&json),
        Err(e) => {
            eprintln!("[privstack-ffi] get_view_data({}) failed: {:?}", id, e);
            to_c_string(r#"{}"#)
        }
    }
}}

/// Activates a plugin — calls its `activate()` export.
/// Must be called after loading and entity type registration.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_activate(
    plugin_id: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    match handle.plugin_host.activate_plugin(id) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::PluginError,
    }
}}

/// Notifies a plugin that the user navigated to its view.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_navigated_to(
    plugin_id: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    match handle.plugin_host.notify_navigated_to(id) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::PluginError,
    }
}}

/// Notifies a plugin that the user navigated away from its view.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_navigated_from(
    plugin_id: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    match handle.plugin_host.notify_navigated_from(id) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::PluginError,
    }
}}

/// Updates the permission set for a loaded plugin at runtime.
///
/// # Safety
/// - `plugin_id` must be a valid null-terminated UTF-8 string.
/// - `permissions_json` must be a valid null-terminated UTF-8 JSON string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_plugin_update_permissions(
    plugin_id: *const c_char,
    permissions_json: *const c_char,
) -> PrivStackError { unsafe {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let id = match nullable_cstr_to_str(plugin_id) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    let perms_str = match nullable_cstr_to_str(permissions_json) {
        Some(s) => s,
        None => return PrivStackError::NullPointer,
    };

    let permissions: privstack_plugin_host::PermissionSet = match serde_json::from_str(perms_str) {
        Ok(p) => p,
        Err(_) => return PrivStackError::JsonError,
    };

    match handle.plugin_host.update_plugin_permissions(id, permissions) {
        Ok(()) => PrivStackError::Ok,
        Err(privstack_plugin_host::PluginHostError::PluginNotFound(_)) => PrivStackError::PluginNotFound,
        Err(_) => PrivStackError::PluginError,
    }
}}

} // mod plugin_ffi (cfg wasm-plugins)

/// Returns JSON metadata for a .ppk file without installing it.
/// Caller must free the returned string with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_ppk_inspect(
    ppk_path: *const c_char,
) -> *mut c_char { unsafe {
    let path_str = match nullable_cstr_to_str(ppk_path) {
        Some(s) => s,
        None => return to_c_string("{}"),
    };

    let path = Path::new(path_str);
    let file = match std::fs::File::open(path) {
        Ok(f) => f,
        Err(_) => return to_c_string("{}"),
    };

    let reader = std::io::BufReader::new(file);
    let package = match privstack_ppk::PpkPackage::open(reader) {
        Ok(p) => p,
        Err(_) => return to_c_string("{}"),
    };

    let json = serde_json::to_string(&package.manifest).unwrap_or_else(|_| "{}".to_string());
    to_c_string(&json)
}}

/// Returns the content hash of a .ppk file for integrity verification.
/// Caller must free the returned string with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_ppk_content_hash(
    ppk_path: *const c_char,
) -> *mut c_char { unsafe {
    let path_str = match nullable_cstr_to_str(ppk_path) {
        Some(s) => s,
        None => return to_c_string(""),
    };

    let path = Path::new(path_str);
    let file = match std::fs::File::open(path) {
        Ok(f) => f,
        Err(_) => return to_c_string(""),
    };

    let reader = std::io::BufReader::new(file);
    let package = match privstack_ppk::PpkPackage::open(reader) {
        Ok(p) => p,
        Err(_) => return to_c_string(""),
    };

    to_c_string(&package.content_hash())
}}

// ========================================================================
// Database Maintenance
// ========================================================================

/// Runs database maintenance (checkpoint + vacuum) to reclaim space.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_db_maintenance() -> PrivStackError {
    let handle = HANDLE.lock().unwrap();
    match handle.as_ref() {
        Some(h) => match h.entity_store.run_maintenance() {
            Ok(_) => PrivStackError::Ok,
            Err(_) => PrivStackError::StorageError,
        },
        None => PrivStackError::NotInitialized,
    }
}

/// Helper: allocate a C string from a Rust &str. Caller must free with `privstack_free_string`.
fn to_c_string(s: &str) -> *mut c_char {
    CString::new(s).unwrap_or_default().into_raw()
}

/// Helper: convert a nullable C string to a &str, returning None if null or invalid.
unsafe fn nullable_cstr_to_str<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(feature = "wasm-plugins")]
    use privstack_plugin_host::{PolicyConfig, PolicyEngine};
    use serial_test::serial;
    use std::ffi::CString;
    use std::ptr;

    /// Initializes the runtime with an in-memory policy (no filesystem I/O).
    #[cfg(feature = "wasm-plugins")]
    fn test_init() -> PrivStackError {
        init_with_plugin_host_builder(":memory:", |es, ev| {
            PluginHostManager::with_policy(es, ev, PolicyEngine::with_config(PolicyConfig::default()))
        })
    }

    /// Initializes the runtime without plugin host (no wasm-plugins feature).
    #[cfg(not(feature = "wasm-plugins"))]
    fn test_init() -> PrivStackError {
        init_core(":memory:")
    }

    #[test]
    fn version_returns_valid_string() {
        let version = privstack_version();
        assert!(!version.is_null());
        let version_str = unsafe { CStr::from_ptr(version) }.to_str().unwrap();
        assert_eq!(version_str, env!("CARGO_PKG_VERSION"));
    }

    #[test]
    fn init_with_null_returns_error() {
        let result = unsafe { privstack_init(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    #[serial]
    fn init_and_shutdown() {
        let result = test_init();
        assert_eq!(result, PrivStackError::Ok);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn vault_init_unlock_lock_via_auth() {
        let result = test_init();
        assert_eq!(result, PrivStackError::Ok);

        assert!(!privstack_auth_is_initialized());
        assert!(!privstack_auth_is_unlocked());

        let password = CString::new("testpassword123").unwrap();
        let result = unsafe { privstack_auth_initialize(password.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        assert!(privstack_auth_is_initialized());
        assert!(privstack_auth_is_unlocked());

        let result = privstack_auth_lock();
        assert_eq!(result, PrivStackError::Ok);
        assert!(!privstack_auth_is_unlocked());

        let result = unsafe { privstack_auth_unlock(password.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(privstack_auth_is_unlocked());

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn vault_blob_store_read_delete() {
        test_init();

        let vault_id = CString::new("test_vault").unwrap();
        let password = CString::new("password123").unwrap();
        unsafe {
            privstack_vault_create(vault_id.as_ptr());
            privstack_vault_initialize(vault_id.as_ptr(), password.as_ptr());
        }

        let blob_id = CString::new("blob1").unwrap();
        let data = b"secret data";
        let result = unsafe {
            privstack_vault_blob_store(
                vault_id.as_ptr(),
                blob_id.as_ptr(),
                data.as_ptr(),
                data.len(),
            )
        };
        assert_eq!(result, PrivStackError::Ok);

        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let result = unsafe {
            privstack_vault_blob_read(
                vault_id.as_ptr(),
                blob_id.as_ptr(),
                &mut out_data,
                &mut out_len,
            )
        };
        assert_eq!(result, PrivStackError::Ok);
        assert_eq!(out_len, data.len());
        let read_data = unsafe { std::slice::from_raw_parts(out_data, out_len) };
        assert_eq!(read_data, data);
        unsafe { privstack_free_bytes(out_data, out_len) };

        let result =
            unsafe { privstack_vault_blob_delete(vault_id.as_ptr(), blob_id.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Null pointer checks ─────────────────────────────────────

    #[test]
    fn auth_initialize_null() {
        let result = unsafe { privstack_auth_initialize(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn auth_unlock_null() {
        let result = unsafe { privstack_auth_unlock(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn auth_change_password_null() {
        let result = unsafe { privstack_auth_change_password(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_create_null() {
        let result = unsafe { privstack_vault_create(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_initialize_null() {
        let result = unsafe { privstack_vault_initialize(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_unlock_null() {
        let result = unsafe { privstack_vault_unlock(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_lock_null() {
        let result = unsafe { privstack_vault_lock(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_is_initialized_null() {
        let result = unsafe { privstack_vault_is_initialized(ptr::null()) };
        assert!(!result);
    }

    #[test]
    fn vault_is_unlocked_null() {
        let result = unsafe { privstack_vault_is_unlocked(ptr::null()) };
        assert!(!result);
    }

    #[test]
    fn vault_change_password_null() {
        let result = unsafe { privstack_vault_change_password(ptr::null(), ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_blob_store_null() {
        let result = unsafe { privstack_vault_blob_store(ptr::null(), ptr::null(), ptr::null(), 0) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_blob_read_null() {
        let result = unsafe { privstack_vault_blob_read(ptr::null(), ptr::null(), ptr::null_mut(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_blob_delete_null() {
        let result = unsafe { privstack_vault_blob_delete(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn vault_blob_list_null() {
        let result = unsafe { privstack_vault_blob_list(ptr::null(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn blob_store_null() {
        let result = unsafe { privstack_blob_store(ptr::null(), ptr::null(), ptr::null(), 0, ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn blob_read_null() {
        let result = unsafe { privstack_blob_read(ptr::null(), ptr::null(), ptr::null_mut(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn blob_delete_null() {
        let result = unsafe { privstack_blob_delete(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn blob_list_null() {
        let result = unsafe { privstack_blob_list(ptr::null(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_generate_code_null() {
        let result = unsafe { privstack_pairing_generate_code(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_join_code_null() {
        let result = unsafe { privstack_pairing_join_code(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_get_code_null() {
        let result = unsafe { privstack_pairing_get_code(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_list_peers_null() {
        let result = unsafe { privstack_pairing_list_peers(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_trust_peer_null() {
        let result = unsafe { privstack_pairing_trust_peer(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_remove_peer_null() {
        let result = unsafe { privstack_pairing_remove_peer(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_save_state_null() {
        let result = unsafe { privstack_pairing_save_state(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_load_state_null() {
        let result = unsafe { privstack_pairing_load_state(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_get_device_name_null() {
        let result = unsafe { privstack_pairing_get_device_name(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn pairing_set_device_name_null() {
        let result = unsafe { privstack_pairing_set_device_name(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_init_google_drive_null() {
        let result = unsafe { privstack_cloud_init_google_drive(ptr::null(), ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_authenticate_null() {
        let result = unsafe { privstack_cloud_authenticate(CloudProvider::GoogleDrive, ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_complete_auth_null() {
        let result = unsafe { privstack_cloud_complete_auth(CloudProvider::GoogleDrive, ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_list_files_null() {
        let result = unsafe { privstack_cloud_list_files(CloudProvider::GoogleDrive, ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_upload_null() {
        let result = unsafe { privstack_cloud_upload(CloudProvider::GoogleDrive, ptr::null(), ptr::null(), 0, ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_download_null() {
        let result = unsafe { privstack_cloud_download(CloudProvider::GoogleDrive, ptr::null(), ptr::null_mut(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn cloud_delete_null() {
        let result = unsafe { privstack_cloud_delete(CloudProvider::GoogleDrive, ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn license_parse_null() {
        let result = unsafe { privstack_license_parse(ptr::null(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn license_get_plan_null() {
        let result = unsafe { privstack_license_get_plan(ptr::null(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn device_info_null() {
        let result = unsafe { privstack_device_info(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn device_fingerprint_null() {
        let result = unsafe { privstack_device_fingerprint(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn license_activate_null() {
        let result = unsafe { privstack_license_activate(ptr::null(), ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn license_check_null() {
        let result = unsafe { privstack_license_check(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn license_status_null() {
        let result = unsafe { privstack_license_status(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn license_activated_plan_null() {
        let result = unsafe { privstack_license_activated_plan(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    fn register_entity_type_null() {
        let result = unsafe { privstack_register_entity_type(ptr::null()) };
        assert_eq!(result, -1);
    }

    // ── Not-initialized checks ──────────────────────────────────

    #[test]
    #[serial]
    fn auth_not_initialized_returns_false() {
        privstack_shutdown(); // ensure clean state
        assert!(!privstack_auth_is_initialized());
        assert!(!privstack_auth_is_unlocked());
    }

    #[test]
    #[serial]
    fn vault_lock_all_not_initialized() {
        privstack_shutdown();
        let result = privstack_vault_lock_all();
        assert_eq!(result, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn auth_lock_not_initialized() {
        privstack_shutdown();
        let result = privstack_auth_lock();
        assert_eq!(result, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn sync_start_not_initialized() {
        privstack_shutdown();
        let result = privstack_sync_start();
        assert_eq!(result, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn sync_stop_not_initialized() {
        privstack_shutdown();
        let result = privstack_sync_stop();
        assert_eq!(result, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn cloud_is_authenticated_not_initialized() {
        privstack_shutdown();
        assert!(!privstack_cloud_is_authenticated(CloudProvider::GoogleDrive));
        assert!(!privstack_cloud_is_authenticated(CloudProvider::ICloud));
    }

    #[test]
    #[serial]
    fn license_is_valid_not_initialized() {
        privstack_shutdown();
        assert!(!privstack_license_is_valid());
    }

    #[test]
    #[serial]
    fn license_deactivate_not_initialized() {
        privstack_shutdown();
        let result = privstack_license_deactivate();
        assert_eq!(result, PrivStackError::NotInitialized);
    }

    // ── License utility functions ───────────────────────────────

    #[test]
    fn license_max_devices() {
        assert_eq!(privstack_license_max_devices(FfiLicensePlan::Trial), 1);
        assert_eq!(privstack_license_max_devices(FfiLicensePlan::Monthly), 3);
        assert_eq!(privstack_license_max_devices(FfiLicensePlan::Annual), 5);
        assert_eq!(privstack_license_max_devices(FfiLicensePlan::Perpetual), 5);
    }

    #[test]
    fn license_has_priority_support() {
        assert!(!privstack_license_has_priority_support(FfiLicensePlan::Trial));
        assert!(!privstack_license_has_priority_support(FfiLicensePlan::Monthly));
        assert!(privstack_license_has_priority_support(FfiLicensePlan::Annual));
        assert!(privstack_license_has_priority_support(FfiLicensePlan::Perpetual));
    }

    // ── Free functions ──────────────────────────────────────────

    #[test]
    fn free_null_string() {
        unsafe { privstack_free_string(ptr::null_mut()) };
    }

    #[test]
    fn free_null_bytes() {
        unsafe { privstack_free_bytes(ptr::null_mut(), 0) };
    }

    #[test]
    fn free_valid_string() {
        let s = CString::new("test").unwrap();
        unsafe { privstack_free_string(s.into_raw()) };
    }

    // ── SyncEventDto conversion ─────────────────────────────────

    #[test]
    fn sync_event_dto_peer_discovered() {
        let event = SyncEvent::PeerDiscovered {
            peer_id: PeerId::new(),
            device_name: Some("Laptop".to_string()),
        };
        let dto: SyncEventDto = event.into();
        assert_eq!(dto.event_type, "peer_discovered");
        assert!(dto.peer_id.is_some());
        assert_eq!(dto.device_name.as_deref(), Some("Laptop"));
    }

    #[test]
    fn sync_event_dto_sync_started() {
        let dto: SyncEventDto = SyncEvent::SyncStarted { peer_id: PeerId::new() }.into();
        assert_eq!(dto.event_type, "sync_started");
    }

    #[test]
    fn sync_event_dto_sync_completed() {
        let dto: SyncEventDto = SyncEvent::SyncCompleted {
            peer_id: PeerId::new(),
            events_sent: 10,
            events_received: 5,
        }.into();
        assert_eq!(dto.event_type, "sync_completed");
        assert_eq!(dto.events_sent, Some(10));
        assert_eq!(dto.events_received, Some(5));
    }

    #[test]
    fn sync_event_dto_sync_failed() {
        let dto: SyncEventDto = SyncEvent::SyncFailed {
            peer_id: PeerId::new(),
            error: "timeout".to_string(),
        }.into();
        assert_eq!(dto.event_type, "sync_failed");
        assert_eq!(dto.error.as_deref(), Some("timeout"));
    }

    #[test]
    fn sync_event_dto_entity_updated() {
        let dto: SyncEventDto = SyncEvent::EntityUpdated {
            entity_id: privstack_types::EntityId::new(),
        }.into();
        assert_eq!(dto.event_type, "entity_updated");
        assert!(dto.entity_id.is_some());
    }

    // ── Execute / Search null pointer ───────────────────────────

    #[test]
    fn execute_null() {
        let result = unsafe { privstack_execute(ptr::null()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null_pointer"));
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn search_null() {
        let result = unsafe { privstack_search(ptr::null()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null_pointer"));
        unsafe { privstack_free_string(result) };
    }

    // ── Full lifecycle: pairing ─────────────────────────────────

    #[test]
    #[serial]
    fn pairing_generate_and_get_code() {
        test_init();

        let mut out_code: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_pairing_generate_code(&mut out_code) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_code.is_null());

        let code_json = unsafe { CStr::from_ptr(out_code) }.to_str().unwrap();
        let parsed: serde_json::Value = serde_json::from_str(code_json).expect("should be valid JSON");
        assert!(parsed.get("code").is_some(), "JSON should contain 'code' field");
        assert!(parsed.get("hash").is_some(), "JSON should contain 'hash' field");
        unsafe { privstack_free_string(out_code) };

        // Get the code back
        let mut out_code2: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_pairing_get_code(&mut out_code2) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_code2.is_null());

        let get_json = unsafe { CStr::from_ptr(out_code2) }.to_str().unwrap();
        let parsed2: serde_json::Value = serde_json::from_str(get_json).expect("should be valid JSON");
        assert!(parsed2.get("code").is_some());
        unsafe { privstack_free_string(out_code2) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn pairing_list_peers_empty() {
        test_init();

        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_pairing_list_peers(&mut out_json) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_json.is_null());

        let json = unsafe { CStr::from_ptr(out_json) }.to_str().unwrap();
        assert!(json.contains("[]") || json.contains("{}"));
        unsafe { privstack_free_string(out_json) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn pairing_save_load_state() {
        test_init();

        // Save state
        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_pairing_save_state(&mut out_json) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_json.is_null());

        let state_json = unsafe { CStr::from_ptr(out_json) }.to_str().unwrap().to_string();
        unsafe { privstack_free_string(out_json) };

        // Load it back
        let c_json = CString::new(state_json).unwrap();
        let result = unsafe { privstack_pairing_load_state(c_json.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn device_name_get_set() {
        test_init();

        let name = CString::new("TestDevice").unwrap();
        let result = unsafe { privstack_pairing_set_device_name(name.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        let mut out_name: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_pairing_get_device_name(&mut out_name) };
        assert_eq!(result, PrivStackError::Ok);

        let got = unsafe { CStr::from_ptr(out_name) }.to_str().unwrap();
        assert_eq!(got, "TestDevice");
        unsafe { privstack_free_string(out_name) };

        privstack_shutdown();
    }

    // ── Full lifecycle: cloud init ──────────────────────────────

    #[test]
    #[serial]
    fn cloud_init_google_drive_lifecycle() {
        test_init();

        let client_id = CString::new("test_id").unwrap();
        let client_secret = CString::new("test_secret").unwrap();
        let result = unsafe { privstack_cloud_init_google_drive(client_id.as_ptr(), client_secret.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        assert!(!privstack_cloud_is_authenticated(CloudProvider::GoogleDrive));

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn cloud_init_icloud_default() {
        test_init();

        let result = unsafe { privstack_cloud_init_icloud(ptr::null()) };
        assert_eq!(result, PrivStackError::Ok);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn cloud_init_icloud_custom_bundle() {
        test_init();

        let bundle = CString::new("com.test.custom").unwrap();
        let result = unsafe { privstack_cloud_init_icloud(bundle.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Full lifecycle: device info ─────────────────────────────

    #[test]
    fn device_info_returns_json() {
        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_device_info(&mut out_json) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_json.is_null());

        let json = unsafe { CStr::from_ptr(out_json) }.to_str().unwrap();
        assert!(json.contains("os_name"));
        assert!(json.contains("fingerprint"));
        unsafe { privstack_free_string(out_json) };
    }

    #[test]
    fn device_fingerprint_returns_string() {
        let mut out_fp: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_device_fingerprint(&mut out_fp) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_fp.is_null());

        let fp = unsafe { CStr::from_ptr(out_fp) }.to_str().unwrap();
        assert!(!fp.is_empty());
        unsafe { privstack_free_string(out_fp) };
    }

    // ── Full lifecycle: unencrypted blobs ───────────────────────

    #[test]
    #[serial]
    fn blob_store_read_delete_lifecycle() {
        test_init();

        let ns = CString::new("test_ns").unwrap();
        let bid = CString::new("blob1").unwrap();
        let data = b"blob content";

        let result = unsafe {
            privstack_blob_store(ns.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len(), ptr::null())
        };
        assert_eq!(result, PrivStackError::Ok);

        // Read it back
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let result = unsafe {
            privstack_blob_read(ns.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len)
        };
        assert_eq!(result, PrivStackError::Ok);
        assert_eq!(out_len, data.len());
        let read = unsafe { std::slice::from_raw_parts(out_data, out_len) };
        assert_eq!(read, data);
        unsafe { privstack_free_bytes(out_data, out_len) };

        // List blobs
        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_blob_list(ns.as_ptr(), &mut out_json) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_json.is_null());
        unsafe { privstack_free_string(out_json) };

        // Delete
        let result = unsafe { privstack_blob_delete(ns.as_ptr(), bid.as_ptr()) };
        assert_eq!(result, PrivStackError::Ok);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn blob_store_with_metadata() {
        test_init();

        let ns = CString::new("ns").unwrap();
        let bid = CString::new("b1").unwrap();
        let data = b"data";
        let meta = CString::new(r#"{"key":"value"}"#).unwrap();

        let result = unsafe {
            privstack_blob_store(ns.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len(), meta.as_ptr())
        };
        assert_eq!(result, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Full lifecycle: vault blob list ─────────────────────────

    #[test]
    #[serial]
    fn vault_blob_list_lifecycle() {
        test_init();

        let vid = CString::new("list_vault").unwrap();
        let pwd = CString::new("password123").unwrap();
        unsafe {
            privstack_vault_create(vid.as_ptr());
            privstack_vault_initialize(vid.as_ptr(), pwd.as_ptr());
        }

        let bid = CString::new("b1").unwrap();
        let data = b"test";
        unsafe {
            privstack_vault_blob_store(vid.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len());
        }

        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_vault_blob_list(vid.as_ptr(), &mut out_json) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_json.is_null());

        let json = unsafe { CStr::from_ptr(out_json) }.to_str().unwrap();
        assert!(json.contains("b1"));
        unsafe { privstack_free_string(out_json) };

        privstack_shutdown();
    }

    // ── Execute endpoint ────────────────────────────────────────

    #[test]
    #[serial]
    fn execute_invalid_json() {
        test_init();

        let bad_json = CString::new("not json").unwrap();
        let result = unsafe { privstack_execute(bad_json.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("json_parse_error"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_unknown_entity_type() {
        test_init();

        let req = CString::new(r#"{"plugin_id":"test","action":"read","entity_type":"nonexistent","entity_id":"123"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("unknown_entity"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn search_invalid_json() {
        test_init();

        let bad = CString::new("not json").unwrap();
        let result = unsafe { privstack_search(bad.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("json_parse_error"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn search_valid_query() {
        test_init();

        let query = CString::new(r#"{"query":"test","limit":10}"#).unwrap();
        let result = unsafe { privstack_search(query.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("success"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn register_entity_type_valid() {
        test_init();

        let schema_json = CString::new(r#"{"entity_type":"test_note","indexed_fields":[{"field_path":"/title","field_type":"text","searchable":true}],"merge_strategy":"lww_document"}"#).unwrap();
        let result = unsafe { privstack_register_entity_type(schema_json.as_ptr()) };
        assert_eq!(result, 0);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn register_entity_type_invalid_json() {
        test_init();

        let bad = CString::new("not json").unwrap();
        let result = unsafe { privstack_register_entity_type(bad.as_ptr()) };
        assert_eq!(result, -3);

        privstack_shutdown();
    }

    // ── Sync status checks ──────────────────────────────────────

    #[test]
    #[serial]
    fn sync_is_running_after_init() {
        test_init();

        assert!(!privstack_sync_is_running());

        privstack_shutdown();
    }

    // ── PrivStackError enum ─────────────────────────────────────

    #[test]
    fn error_enum_values() {
        assert_eq!(PrivStackError::Ok as i32, 0);
        assert_eq!(PrivStackError::NullPointer as i32, 1);
        assert_eq!(PrivStackError::InvalidUtf8 as i32, 2);
        assert_eq!(PrivStackError::JsonError as i32, 3);
        assert_eq!(PrivStackError::StorageError as i32, 4);
        assert_eq!(PrivStackError::NotFound as i32, 5);
        assert_eq!(PrivStackError::NotInitialized as i32, 6);
        assert_eq!(PrivStackError::Unknown as i32, 99);
    }

    #[test]
    fn error_enum_debug() {
        let err = PrivStackError::NullPointer;
        assert_eq!(format!("{:?}", err), "NullPointer");
    }

    #[test]
    fn error_enum_clone_eq() {
        let err = PrivStackError::StorageError;
        let cloned = err;
        assert_eq!(err, cloned);
    }

    // ── CloudProvider enum ──────────────────────────────────────

    #[test]
    fn cloud_provider_values() {
        assert_eq!(CloudProvider::GoogleDrive as i32, 0);
        assert_eq!(CloudProvider::ICloud as i32, 1);
    }

    // ── FfiLicensePlan / FfiLicenseStatus ───────────────────────

    #[test]
    fn ffi_license_plan_from() {
        assert_eq!(FfiLicensePlan::from(LicensePlan::Trial), FfiLicensePlan::Trial);
        assert_eq!(FfiLicensePlan::from(LicensePlan::Monthly), FfiLicensePlan::Monthly);
        assert_eq!(FfiLicensePlan::from(LicensePlan::Annual), FfiLicensePlan::Annual);
        assert_eq!(FfiLicensePlan::from(LicensePlan::Perpetual), FfiLicensePlan::Perpetual);
    }

    #[test]
    fn ffi_license_status_from() {
        assert_eq!(FfiLicenseStatus::from(LicenseStatus::Active), FfiLicenseStatus::Active);
        assert_eq!(FfiLicenseStatus::from(LicenseStatus::Expired), FfiLicenseStatus::Expired);
        assert_eq!(FfiLicenseStatus::from(LicenseStatus::Grace { days_remaining: 14 }), FfiLicenseStatus::Grace);
        assert_eq!(FfiLicenseStatus::from(LicenseStatus::ReadOnly), FfiLicenseStatus::ReadOnly);
        assert_eq!(FfiLicenseStatus::from(LicenseStatus::NotActivated), FfiLicenseStatus::NotActivated);
    }

    // ── license_error_to_ffi mapping ────────────────────────────

    #[test]
    fn license_error_mapping() {
        assert_eq!(license_error_to_ffi(LicenseError::InvalidKeyFormat("bad".into())), PrivStackError::LicenseInvalidFormat);
        assert_eq!(license_error_to_ffi(LicenseError::InvalidSignature), PrivStackError::LicenseInvalidSignature);
        assert_eq!(license_error_to_ffi(LicenseError::InvalidPayload("bad".into())), PrivStackError::LicenseInvalidFormat);
        assert_eq!(license_error_to_ffi(LicenseError::NotActivated), PrivStackError::LicenseNotActivated);
        assert_eq!(license_error_to_ffi(LicenseError::Revoked), PrivStackError::LicenseExpired);
    }

    // ── Vault lock/unlock lifecycle via FFI ─────────────────────

    #[test]
    #[serial]
    fn vault_full_lifecycle() {
        test_init();

        let vid = CString::new("lifecycle_vault").unwrap();
        let pwd = CString::new("password123").unwrap();

        // Create
        let r = unsafe { privstack_vault_create(vid.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        // Not yet initialized
        assert!(!unsafe { privstack_vault_is_initialized(vid.as_ptr()) });

        // Initialize
        let r = unsafe { privstack_vault_initialize(vid.as_ptr(), pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(unsafe { privstack_vault_is_initialized(vid.as_ptr()) });
        assert!(unsafe { privstack_vault_is_unlocked(vid.as_ptr()) });

        // Lock
        let r = unsafe { privstack_vault_lock(vid.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!unsafe { privstack_vault_is_unlocked(vid.as_ptr()) });

        // Unlock
        let r = unsafe { privstack_vault_unlock(vid.as_ptr(), pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(unsafe { privstack_vault_is_unlocked(vid.as_ptr()) });

        // Change password
        let new_pwd = CString::new("newpassword1").unwrap();
        let r = unsafe { privstack_vault_change_password(vid.as_ptr(), pwd.as_ptr(), new_pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        // Lock all
        let r = privstack_vault_lock_all();
        assert_eq!(r, PrivStackError::Ok);
        assert!(!unsafe { privstack_vault_is_unlocked(vid.as_ptr()) });

        privstack_shutdown();
    }

    // ── Vault ID with dots (e.g. "privstack.files") ───────────

    #[test]
    #[serial]
    fn vault_dotted_id_lifecycle() {
        test_init();

        let vid = CString::new("privstack.files").unwrap();
        let pwd = CString::new("password123").unwrap();

        // Create with dotted ID
        let r = unsafe { privstack_vault_create(vid.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        // Not yet initialized
        assert!(!unsafe { privstack_vault_is_initialized(vid.as_ptr()) });

        // Initialize
        let r = unsafe { privstack_vault_initialize(vid.as_ptr(), pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(unsafe { privstack_vault_is_initialized(vid.as_ptr()) });
        assert!(unsafe { privstack_vault_is_unlocked(vid.as_ptr()) });

        // Store and read a blob
        let bid = CString::new("test_blob").unwrap();
        let data = b"hello vault with dots";
        let r = unsafe {
            privstack_vault_blob_store(vid.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len())
        };
        assert_eq!(r, PrivStackError::Ok);

        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe {
            privstack_vault_blob_read(vid.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len)
        };
        assert_eq!(r, PrivStackError::Ok);
        assert_eq!(out_len, data.len());

        let read_data = unsafe { std::slice::from_raw_parts(out_data, out_len) };
        assert_eq!(read_data, data);
        unsafe { privstack_free_bytes(out_data, out_len) };

        // Lock and unlock
        let r = unsafe { privstack_vault_lock(vid.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        let r = unsafe { privstack_vault_unlock(vid.as_ptr(), pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Locked vault blob operations ────────────────────────────

    #[test]
    #[serial]
    fn vault_blob_locked_returns_error() {
        test_init();

        let vid = CString::new("locked_vault").unwrap();
        let pwd = CString::new("password123").unwrap();
        unsafe {
            privstack_vault_create(vid.as_ptr());
            privstack_vault_initialize(vid.as_ptr(), pwd.as_ptr());
            privstack_vault_lock(vid.as_ptr());
        }

        let bid = CString::new("b1").unwrap();
        let data = b"test";
        let result = unsafe {
            privstack_vault_blob_store(vid.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len())
        };
        assert_eq!(result, PrivStackError::VaultLocked);

        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let result = unsafe {
            privstack_vault_blob_read(vid.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len)
        };
        assert_eq!(result, PrivStackError::VaultLocked);

        privstack_shutdown();
    }

    // ── Blob not found ──────────────────────────────────────────

    #[test]
    #[serial]
    fn vault_blob_read_not_found() {
        test_init();

        let vid = CString::new("nf_vault").unwrap();
        let pwd = CString::new("password123").unwrap();
        unsafe {
            privstack_vault_create(vid.as_ptr());
            privstack_vault_initialize(vid.as_ptr(), pwd.as_ptr());
        }

        let bid = CString::new("nonexistent").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let result = unsafe {
            privstack_vault_blob_read(vid.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len)
        };
        assert_eq!(result, PrivStackError::NotFound);

        let result = unsafe { privstack_vault_blob_delete(vid.as_ptr(), bid.as_ptr()) };
        assert_eq!(result, PrivStackError::NotFound);

        privstack_shutdown();
    }

    // ── Cloud provider name ─────────────────────────────────────

    #[test]
    #[serial]
    fn cloud_provider_name_google() {
        test_init();

        let cid = CString::new("id").unwrap();
        let csec = CString::new("sec").unwrap();
        unsafe { privstack_cloud_init_google_drive(cid.as_ptr(), csec.as_ptr()) };

        let name = privstack_cloud_provider_name(CloudProvider::GoogleDrive);
        assert!(!name.is_null());
        let name_str = unsafe { CStr::from_ptr(name) }.to_str().unwrap();
        assert_eq!(name_str, "Google Drive");

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn cloud_provider_name_icloud() {
        test_init();

        unsafe { privstack_cloud_init_icloud(ptr::null()) };

        let name = privstack_cloud_provider_name(CloudProvider::ICloud);
        assert!(!name.is_null());
        let name_str = unsafe { CStr::from_ptr(name) }.to_str().unwrap();
        assert_eq!(name_str, "iCloud Drive");

        privstack_shutdown();
    }

    // ── Sync poll event when no events ──────────────────────────

    #[test]
    #[serial]
    fn sync_poll_event_not_running() {
        test_init();

        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_sync_poll_event(&mut out_json) };
        assert_eq!(result, PrivStackError::SyncNotRunning);

        privstack_shutdown();
    }

    // ── Sync status ─────────────────────────────────────────────

    #[test]
    #[serial]
    fn sync_status_when_not_running() {
        test_init();

        let mut out_json: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_sync_status(&mut out_json) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_json.is_null());

        let json = unsafe { CStr::from_ptr(out_json) }.to_str().unwrap();
        assert!(json.contains("\"running\":false"));
        assert!(json.contains("local_peer_id"));
        assert!(json.contains("discovered_peers"));
        unsafe { privstack_free_string(out_json) };

        privstack_shutdown();
    }

    #[test]
    fn sync_status_null() {
        let result = unsafe { privstack_sync_status(ptr::null_mut()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    // ── Sync trigger ────────────────────────────────────────────

    #[test]
    #[serial]
    fn sync_trigger_not_running() {
        test_init();

        let result = privstack_sync_trigger();
        assert_eq!(result, PrivStackError::SyncNotRunning);

        privstack_shutdown();
    }

    // ── Sync publish event ──────────────────────────────────────

    #[test]
    fn sync_publish_event_null() {
        let result = unsafe { privstack_sync_publish_event(ptr::null()) };
        assert_eq!(result, PrivStackError::NullPointer);
    }

    #[test]
    #[serial]
    fn sync_publish_event_invalid_json() {
        test_init();

        let bad = CString::new("not json").unwrap();
        let result = unsafe { privstack_sync_publish_event(bad.as_ptr()) };
        assert_eq!(result, PrivStackError::JsonError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn sync_publish_event_no_orchestrator() {
        test_init();

        // The exact Event serialization format is complex; invalid JSON gives JsonError,
        // and even valid JSON without an orchestrator gives SyncNotRunning.
        // Test the invalid JSON path here (SyncNotRunning tested via sync_trigger).
        let event_json = CString::new(r#"{"invalid":"event"}"#).unwrap();
        let result = unsafe { privstack_sync_publish_event(event_json.as_ptr()) };
        assert_eq!(result, PrivStackError::JsonError);

        privstack_shutdown();
    }

    // ── Execute CRUD lifecycle ──────────────────────────────────

    #[test]
    #[serial]
    fn execute_create_read_update_delete() {
        test_init();

        // Register entity type
        let schema = CString::new(r#"{"entity_type":"test_item","indexed_fields":[{"field_path":"/title","field_type":"text","searchable":true}],"merge_strategy":"lww_document"}"#).unwrap();
        let r = unsafe { privstack_register_entity_type(schema.as_ptr()) };
        assert_eq!(r, 0);

        // Create
        let create_req = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"test_item","payload":"{\"title\":\"Hello World\"}"}"#).unwrap();
        let result = unsafe { privstack_execute(create_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap().to_string();
        assert!(json.contains("\"success\":true"), "Create failed: {json}");
        assert!(json.contains("Hello World"));

        // Extract entity ID
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        let entity_id = parsed["data"]["id"].as_str().unwrap().to_string();
        unsafe { privstack_free_string(result) };

        // Read
        let read_req = CString::new(format!(
            r#"{{"plugin_id":"test","action":"read","entity_type":"test_item","entity_id":"{}"}}"#,
            entity_id
        )).unwrap();
        let result = unsafe { privstack_execute(read_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Read failed: {json}");
        assert!(json.contains("Hello World"));
        unsafe { privstack_free_string(result) };

        // Update
        let update_req = CString::new(format!(
            r#"{{"plugin_id":"test","action":"update","entity_type":"test_item","entity_id":"{}","payload":"{{\"title\":\"Updated Title\"}}"}}"#,
            entity_id
        )).unwrap();
        let result = unsafe { privstack_execute(update_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Update failed: {json}");
        assert!(json.contains("Updated Title"));
        unsafe { privstack_free_string(result) };

        // Read List
        let list_req = CString::new(r#"{"plugin_id":"test","action":"read_list","entity_type":"test_item"}"#).unwrap();
        let result = unsafe { privstack_execute(list_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "List failed: {json}");
        assert!(json.contains("Updated Title"));
        unsafe { privstack_free_string(result) };

        // Trash
        let trash_req = CString::new(format!(
            r#"{{"plugin_id":"test","action":"trash","entity_type":"test_item","entity_id":"{}"}}"#,
            entity_id
        )).unwrap();
        let result = unsafe { privstack_execute(trash_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Trash failed: {json}");
        unsafe { privstack_free_string(result) };

        // Restore
        let restore_req = CString::new(format!(
            r#"{{"plugin_id":"test","action":"restore","entity_type":"test_item","entity_id":"{}"}}"#,
            entity_id
        )).unwrap();
        let result = unsafe { privstack_execute(restore_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Restore failed: {json}");
        unsafe { privstack_free_string(result) };

        // Delete
        let del_req = CString::new(format!(
            r#"{{"plugin_id":"test","action":"delete","entity_type":"test_item","entity_id":"{}"}}"#,
            entity_id
        )).unwrap();
        let result = unsafe { privstack_execute(del_req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Delete failed: {json}");
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_read_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"err_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"read","entity_type":"err_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_read_not_found() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"nf_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"read","entity_type":"nf_item","entity_id":"nonexistent"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("not_found"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_create_missing_payload() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"mp_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"mp_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_payload"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_unknown_action() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"ua_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"purge","entity_type":"ua_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("unknown_action"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_delete_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"dm_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"delete","entity_type":"dm_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_trash_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"tm_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"trash","entity_type":"tm_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_restore_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"rm_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"restore","entity_type":"rm_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_query() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"q_item","indexed_fields":[{"field_path":"/title","field_type":"text","searchable":true}],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        // Create item
        let create = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"q_item","payload":"{\"title\":\"Queryable\"}"}"#).unwrap();
        let r = unsafe { privstack_execute(create.as_ptr()) };
        unsafe { privstack_free_string(r) };

        // Query
        let query = CString::new(r#"{"plugin_id":"test","action":"query","entity_type":"q_item","payload":"[]"}"#).unwrap();
        let result = unsafe { privstack_execute(query.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Query failed: {json}");
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_link_unlink_get_links() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"link_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        // Create two items
        let create1 = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"link_item","entity_id":"item1","payload":"{\"title\":\"A\"}"}"#).unwrap();
        let r = unsafe { privstack_execute(create1.as_ptr()) };
        unsafe { privstack_free_string(r) };

        let create2 = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"link_item","entity_id":"item2","payload":"{\"title\":\"B\"}"}"#).unwrap();
        let r = unsafe { privstack_execute(create2.as_ptr()) };
        unsafe { privstack_free_string(r) };

        // Link
        let link = CString::new(r#"{"plugin_id":"test","action":"link","entity_type":"link_item","entity_id":"item1","parameters":{"target_type":"link_item","target_id":"item2"}}"#).unwrap();
        let result = unsafe { privstack_execute(link.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Link failed: {json}");
        unsafe { privstack_free_string(result) };

        // Get links
        let get_links = CString::new(r#"{"plugin_id":"test","action":"get_links","entity_type":"link_item","entity_id":"item1"}"#).unwrap();
        let result = unsafe { privstack_execute(get_links.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Get links failed: {json}");
        assert!(json.contains("item2"));
        unsafe { privstack_free_string(result) };

        // Unlink
        let unlink = CString::new(r#"{"plugin_id":"test","action":"unlink","entity_type":"link_item","entity_id":"item1","parameters":{"target_type":"link_item","target_id":"item2"}}"#).unwrap();
        let result = unsafe { privstack_execute(unlink.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Unlink failed: {json}");
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_link_missing_params() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"lp_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"link","entity_type":"lp_item","entity_id":"x"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_params"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_link_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"li_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"link","entity_type":"li_item","parameters":{"target_type":"li_item","target_id":"y"}}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_unlink_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"ui_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"unlink","entity_type":"ui_item","parameters":{"target_type":"x","target_id":"y"}}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_get_links_missing_id() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"gl_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"get_links","entity_type":"gl_item"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_id"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_read_list_with_params() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"rl_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        // Create items
        for i in 0..5 {
            let req = CString::new(format!(
                r#"{{"plugin_id":"test","action":"create","entity_type":"rl_item","payload":"{{\"n\":{i}}}"}}"#
            )).unwrap();
            let r = unsafe { privstack_execute(req.as_ptr()) };
            unsafe { privstack_free_string(r) };
        }

        // List with limit and offset
        let req = CString::new(r#"{"plugin_id":"test","action":"read_list","entity_type":"rl_item","parameters":{"limit":"2","offset":"1"}}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn execute_create_invalid_payload_json() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"bad_json_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"bad_json_item","payload":"not valid json"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("json_error"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ── Search with entity types filter ─────────────────────────

    #[test]
    #[serial]
    fn search_with_entity_types() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"search_item","indexed_fields":[{"field_path":"/title","field_type":"text","searchable":true}],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        // Create item with searchable content
        let create = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"search_item","payload":"{\"title\":\"UniqueSearchableTerm\"}"}"#).unwrap();
        let r = unsafe { privstack_execute(create.as_ptr()) };
        unsafe { privstack_free_string(r) };

        // Search with entity type filter
        let query = CString::new(r#"{"query":"UniqueSearchableTerm","entity_types":["search_item"],"limit":5}"#).unwrap();
        let result = unsafe { privstack_search(query.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"), "Search failed: {json}");
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ── Auth change password ────────────────────────────────────

    #[test]
    #[serial]
    fn auth_change_password_lifecycle() {
        test_init();

        let pwd = CString::new("password123").unwrap();
        unsafe { privstack_auth_initialize(pwd.as_ptr()) };
        assert!(privstack_auth_is_unlocked());

        let new_pwd = CString::new("newpassword1").unwrap();
        let r = unsafe { privstack_auth_change_password(pwd.as_ptr(), new_pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        // Lock and unlock with new password
        privstack_auth_lock();
        let r = unsafe { privstack_auth_unlock(new_pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(privstack_auth_is_unlocked());

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn auth_change_password_too_short() {
        test_init();

        let pwd = CString::new("password123").unwrap();
        unsafe { privstack_auth_initialize(pwd.as_ptr()) };

        let short = CString::new("short").unwrap();
        let r = unsafe { privstack_auth_change_password(pwd.as_ptr(), short.as_ptr()) };
        assert_eq!(r, PrivStackError::PasswordTooShort);

        privstack_shutdown();
    }

    // ── iCloud through FFI ──────────────────────────────────────

    #[test]
    #[serial]
    fn icloud_authenticate_via_ffi() {
        test_init();

        unsafe { privstack_cloud_init_icloud(ptr::null()) };

        let mut out_auth_url: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_cloud_authenticate(CloudProvider::ICloud, &mut out_auth_url) };
        // iCloud auth doesn't need URL — but may fail since container doesn't exist
        // Either way, we exercise the code path
        let _ = result;
        if !out_auth_url.is_null() {
            unsafe { privstack_free_string(out_auth_url) };
        }

        // complete_auth is a no-op for iCloud
        let code = CString::new("anything").unwrap();
        let result = unsafe { privstack_cloud_complete_auth(CloudProvider::ICloud, code.as_ptr()) };
        let _ = result;

        // is_authenticated
        let _ = privstack_cloud_is_authenticated(CloudProvider::ICloud);

        privstack_shutdown();
    }

    // ── Google Drive authenticate via FFI ────────────────────────

    #[test]
    #[serial]
    fn google_drive_authenticate_via_ffi() {
        test_init();

        let cid = CString::new("test_client_id").unwrap();
        let csec = CString::new("test_client_secret").unwrap();
        unsafe { privstack_cloud_init_google_drive(cid.as_ptr(), csec.as_ptr()) };

        let mut out_auth_url: *mut c_char = ptr::null_mut();
        let result = unsafe { privstack_cloud_authenticate(CloudProvider::GoogleDrive, &mut out_auth_url) };
        assert_eq!(result, PrivStackError::Ok);
        assert!(!out_auth_url.is_null());

        let url = unsafe { CStr::from_ptr(out_auth_url) }.to_str().unwrap();
        assert!(url.contains("test_client_id"));
        assert!(url.contains("accounts.google.com"));
        unsafe { privstack_free_string(out_auth_url) };

        privstack_shutdown();
    }

    // ── Cloud not initialized ───────────────────────────────────

    #[test]
    #[serial]
    fn cloud_operations_without_provider_init() {
        test_init();

        // Try operations without initializing provider
        let mut out_auth_url: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_cloud_authenticate(CloudProvider::GoogleDrive, &mut out_auth_url) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let code = CString::new("code").unwrap();
        let r = unsafe { privstack_cloud_complete_auth(CloudProvider::GoogleDrive, code.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_cloud_list_files(CloudProvider::GoogleDrive, &mut out_json) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let name = CString::new("f.txt").unwrap();
        let data = b"x";
        let r = unsafe { privstack_cloud_upload(CloudProvider::GoogleDrive, name.as_ptr(), data.as_ptr(), data.len(), &mut out_json) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let fid = CString::new("fid").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_cloud_download(CloudProvider::GoogleDrive, fid.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let r = unsafe { privstack_cloud_delete(CloudProvider::GoogleDrive, fid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);

        privstack_shutdown();
    }

    // ── License parse with valid key ────────────────────────────

    #[test]
    #[serial]
    fn license_parse_invalid_key() {
        test_init();

        let key = CString::new("invalid-key-format").unwrap();
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_license_parse(key.as_ptr(), &mut out_json) };
        assert_ne!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn license_get_plan_invalid_key() {
        test_init();

        let key = CString::new("bad-key").unwrap();
        let mut out_plan = FfiLicensePlan::Monthly;
        let r = unsafe { privstack_license_get_plan(key.as_ptr(), &mut out_plan) };
        assert_ne!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── License not activated checks ────────────────────────────

    #[test]
    #[serial]
    fn license_check_not_activated() {
        test_init();

        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_license_check(&mut out_json) };
        assert_eq!(r, PrivStackError::LicenseNotActivated);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn license_status_not_activated() {
        test_init();

        let mut out_status = FfiLicenseStatus::Active;
        let r = unsafe { privstack_license_status(&mut out_status) };
        assert_eq!(r, PrivStackError::Ok);
        assert_eq!(out_status, FfiLicenseStatus::NotActivated);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn license_activated_plan_not_activated() {
        test_init();

        let mut out_plan = FfiLicensePlan::Perpetual;
        let r = unsafe { privstack_license_activated_plan(&mut out_plan) };
        assert_eq!(r, PrivStackError::LicenseNotActivated);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn license_deactivate_when_none() {
        test_init();

        let r = privstack_license_deactivate();
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Pairing: trust and remove peer ──────────────────────────

    #[test]
    #[serial]
    fn pairing_trust_and_remove_peer() {
        test_init();

        let peer_id = CString::new("test-peer-id").unwrap();
        let device_name = CString::new("TestDevice").unwrap();

        // approve_peer only moves discovered peers to trusted; calling trust_peer
        // on an undiscovered peer is a no-op but exercises the FFI code path.
        let r = unsafe { privstack_pairing_trust_peer(peer_id.as_ptr(), device_name.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        // List peers (may be empty since peer wasn't discovered first)
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_list_peers(&mut out_json) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!out_json.is_null());
        unsafe { privstack_free_string(out_json) };

        // Remove (no-op for undiscovered peer)
        let r = unsafe { privstack_pairing_remove_peer(peer_id.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Pairing: join with invalid code ─────────────────────────

    #[test]
    #[serial]
    fn pairing_join_invalid_code() {
        test_init();

        let code = CString::new("bad").unwrap();
        let r = unsafe { privstack_pairing_join_code(code.as_ptr()) };
        assert_eq!(r, PrivStackError::InvalidSyncCode);

        privstack_shutdown();
    }

    // ── Pairing: trust peer with null device name ───────────────

    #[test]
    #[serial]
    fn pairing_trust_peer_null_device_name() {
        test_init();

        let peer_id = CString::new("peer-null-name").unwrap();
        let r = unsafe { privstack_pairing_trust_peer(peer_id.as_ptr(), ptr::null()) };
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Pairing: get code when none set ─────────────────────────

    #[test]
    #[serial]
    fn pairing_get_code_none_set() {
        test_init();

        let mut out_code: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_get_code(&mut out_code) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(out_code.is_null()); // No code set

        privstack_shutdown();
    }

    // ── Pairing: load invalid state ─────────────────────────────

    #[test]
    #[serial]
    fn pairing_load_invalid_state() {
        test_init();

        let bad = CString::new("not json").unwrap();
        let r = unsafe { privstack_pairing_load_state(bad.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);

        privstack_shutdown();
    }

    // ── License activate with invalid key ───────────────────────

    #[test]
    #[serial]
    fn license_activate_invalid_key() {
        test_init();

        let key = CString::new("not-a-valid-license").unwrap();
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_license_activate(key.as_ptr(), &mut out_json) };
        assert_ne!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ── Blob read not found ─────────────────────────────────────

    #[test]
    #[serial]
    fn blob_read_not_found() {
        test_init();

        let ns = CString::new("ns").unwrap();
        let bid = CString::new("nonexistent").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_blob_read(ns.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NotFound);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn blob_delete_not_found() {
        test_init();

        let ns = CString::new("ns").unwrap();
        let bid = CString::new("nonexistent").unwrap();
        let r = unsafe { privstack_blob_delete(ns.as_ptr(), bid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotFound);

        privstack_shutdown();
    }

    // ── Sync start/stop lifecycle ────────────────────────────────

    #[test]
    #[serial]
    fn sync_start_stop_lifecycle() {
        test_init();

        assert!(!privstack_sync_is_running());

        let r = privstack_sync_start();
        assert_eq!(r, PrivStackError::Ok);

        // Starting again should return AlreadyRunning
        let r = privstack_sync_start();
        assert_eq!(r, PrivStackError::SyncAlreadyRunning);

        // Poll event when running but empty queue
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_sync_poll_event(&mut out_json) };
        assert_eq!(r, PrivStackError::Ok);
        // out_json may be null (no events) or have content

        // Sync trigger should succeed now
        let r = privstack_sync_trigger();
        assert_eq!(r, PrivStackError::Ok);

        // Sync status when running
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_sync_status(&mut out_json) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!out_json.is_null());
        let json = unsafe { CStr::from_ptr(out_json) }.to_str().unwrap();
        assert!(json.contains("local_peer_id"));
        unsafe { privstack_free_string(out_json) };

        let r = privstack_sync_stop();
        assert_eq!(r, PrivStackError::Ok);

        assert!(!privstack_sync_is_running());

        privstack_shutdown();
    }

    // ── Execute: update existing entity ──────────────────────────

    #[test]
    #[serial]
    fn execute_update_existing_entity() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"upd_item","indexed_fields":[{"field_path":"/title","field_type":"text","searchable":true}],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        // Create
        let create = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"upd_item","payload":"{\"title\":\"Original\"}"}"#).unwrap();
        let result = unsafe { privstack_execute(create.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap().to_string();
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        let entity_id = parsed["data"]["id"].as_str().unwrap().to_string();
        unsafe { privstack_free_string(result) };

        // Update with explicit entity_id
        let update = CString::new(format!(
            r#"{{"plugin_id":"test","action":"update","entity_type":"upd_item","entity_id":"{}","payload":"{{\"title\":\"Updated\"}}"}}"#,
            entity_id
        )).unwrap();
        let result = unsafe { privstack_execute(update.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("Updated"));
        assert!(json.contains(&entity_id));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ── Execute: update nonexistent entity (creates new) ─────────

    #[test]
    #[serial]
    fn execute_update_missing_payload() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"ump_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"update","entity_type":"ump_item","entity_id":"x"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_payload"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ── Execute not initialized ──────────────────────────────────

    #[test]
    #[serial]
    fn execute_not_initialized() {
        privstack_shutdown();

        let req = CString::new(r#"{"plugin_id":"test","action":"read","entity_type":"x","entity_id":"1"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("not_initialized"));
        unsafe { privstack_free_string(result) };
    }

    // ── Search not initialized ───────────────────────────────────

    #[test]
    #[serial]
    fn search_not_initialized() {
        privstack_shutdown();

        let query = CString::new(r#"{"query":"test"}"#).unwrap();
        let result = unsafe { privstack_search(query.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("not_initialized"));
        unsafe { privstack_free_string(result) };
    }

    // ── License error mapping: additional variants ───────────────

    #[test]
    fn license_error_mapping_expired() {
        assert_eq!(
            license_error_to_ffi(LicenseError::Expired("expired".into())),
            PrivStackError::LicenseExpired
        );
    }

    #[test]
    fn license_error_mapping_activation_failed() {
        assert_eq!(
            license_error_to_ffi(LicenseError::ActivationFailed("fail".into())),
            PrivStackError::LicenseActivationFailed
        );
    }

    #[test]
    fn license_error_mapping_device_limit() {
        assert_eq!(
            license_error_to_ffi(LicenseError::DeviceLimitExceeded(3)),
            PrivStackError::LicenseActivationFailed
        );
    }

    #[test]
    fn license_error_mapping_network() {
        assert_eq!(
            license_error_to_ffi(LicenseError::Network("timeout".into())),
            PrivStackError::SyncError
        );
    }

    #[test]
    fn license_error_mapping_storage() {
        assert_eq!(
            license_error_to_ffi(LicenseError::Storage("io".into())),
            PrivStackError::StorageError
        );
    }

    #[test]
    fn license_error_mapping_serialization() {
        assert_eq!(
            license_error_to_ffi(LicenseError::Serialization(serde_json::from_str::<serde_json::Value>("bad").unwrap_err())),
            PrivStackError::JsonError
        );
    }

    // ── Execute: read_list with include_trashed ──────────────────

    #[test]
    #[serial]
    fn execute_read_list_include_trashed() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"trash_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        // Create and trash
        let create = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"trash_item","payload":"{\"title\":\"Trashable\"}"}"#).unwrap();
        let result = unsafe { privstack_execute(create.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap().to_string();
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        let eid = parsed["data"]["id"].as_str().unwrap().to_string();
        unsafe { privstack_free_string(result) };

        let trash = CString::new(format!(
            r#"{{"plugin_id":"test","action":"trash","entity_type":"trash_item","entity_id":"{}"}}"#, eid
        )).unwrap();
        let r = unsafe { privstack_execute(trash.as_ptr()) };
        unsafe { privstack_free_string(r) };

        // List without trashed
        let list = CString::new(r#"{"plugin_id":"test","action":"read_list","entity_type":"trash_item"}"#).unwrap();
        let result = unsafe { privstack_execute(list.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"));
        unsafe { privstack_free_string(result) };

        // List with trashed
        let list = CString::new(r#"{"plugin_id":"test","action":"read_list","entity_type":"trash_item","parameters":{"include_trashed":"true"}}"#).unwrap();
        let result = unsafe { privstack_execute(list.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ── Execute: unlink missing params ───────────────────────────

    #[test]
    #[serial]
    fn execute_unlink_missing_params() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"ulp_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"unlink","entity_type":"ulp_item","entity_id":"x"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("missing_params"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ── Auth operations not initialized ──────────────────────────

    #[test]
    #[serial]
    fn auth_initialize_not_initialized() {
        privstack_shutdown();
        let pwd = CString::new("password123").unwrap();
        let r = unsafe { privstack_auth_initialize(pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn auth_unlock_not_initialized() {
        privstack_shutdown();
        let pwd = CString::new("password123").unwrap();
        let r = unsafe { privstack_auth_unlock(pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn auth_change_password_not_initialized() {
        privstack_shutdown();
        let old = CString::new("oldpass12").unwrap();
        let new = CString::new("newpass12").unwrap();
        let r = unsafe { privstack_auth_change_password(old.as_ptr(), new.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ── Vault operations not initialized ─────────────────────────

    #[test]
    #[serial]
    fn vault_create_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let r = unsafe { privstack_vault_create(vid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn vault_initialize_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let pwd = CString::new("password123").unwrap();
        let r = unsafe { privstack_vault_initialize(vid.as_ptr(), pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn vault_unlock_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let pwd = CString::new("password123").unwrap();
        let r = unsafe { privstack_vault_unlock(vid.as_ptr(), pwd.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn vault_lock_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let r = unsafe { privstack_vault_lock(vid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ── Blob operations not initialized ──────────────────────────

    #[test]
    #[serial]
    fn blob_store_not_initialized() {
        privstack_shutdown();
        let ns = CString::new("ns").unwrap();
        let bid = CString::new("b1").unwrap();
        let data = b"x";
        let r = unsafe { privstack_blob_store(ns.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len(), ptr::null()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn blob_read_not_initialized() {
        privstack_shutdown();
        let ns = CString::new("ns").unwrap();
        let bid = CString::new("b1").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_blob_read(ns.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn blob_delete_not_initialized() {
        privstack_shutdown();
        let ns = CString::new("ns").unwrap();
        let bid = CString::new("b1").unwrap();
        let r = unsafe { privstack_blob_delete(ns.as_ptr(), bid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn blob_list_not_initialized() {
        privstack_shutdown();
        let ns = CString::new("ns").unwrap();
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_blob_list(ns.as_ptr(), &mut out_json) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ── Vault blob operations not initialized ────────────────────

    #[test]
    #[serial]
    fn vault_blob_store_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let bid = CString::new("b1").unwrap();
        let data = b"x";
        let r = unsafe { privstack_vault_blob_store(vid.as_ptr(), bid.as_ptr(), data.as_ptr(), data.len()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn vault_blob_read_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let bid = CString::new("b1").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_vault_blob_read(vid.as_ptr(), bid.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn vault_blob_delete_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let bid = CString::new("b1").unwrap();
        let r = unsafe { privstack_vault_blob_delete(vid.as_ptr(), bid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn vault_blob_list_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_vault_blob_list(vid.as_ptr(), &mut out_json) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ── Sync publish event with valid event (no orchestrator) ────

    #[test]
    #[serial]
    fn sync_publish_valid_event_no_orchestrator() {
        test_init();

        // Build a valid Event JSON
        let event = privstack_types::Event::new(
            privstack_types::EntityId::new(),
            PeerId::new(),
            privstack_types::HybridTimestamp::now(),
            privstack_types::EventPayload::EntityCreated {
                entity_type: "test".to_string(),
                json_data: serde_json::json!({"test": true}).to_string(),
            },
        );
        let event_json_str = serde_json::to_string(&event).unwrap();
        let event_json = CString::new(event_json_str).unwrap();
        let r = unsafe { privstack_sync_publish_event(event_json.as_ptr()) };
        assert_eq!(r, PrivStackError::SyncNotRunning);

        privstack_shutdown();
    }

    // ── Register entity type: not initialized ────────────────────

    #[test]
    #[serial]
    fn register_entity_type_not_initialized() {
        privstack_shutdown();
        let schema = CString::new(r#"{"entity_type":"x","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        let r = unsafe { privstack_register_entity_type(schema.as_ptr()) };
        assert_eq!(r, -4);
    }

    // ── Pairing operations not initialized ───────────────────────

    #[test]
    #[serial]
    fn pairing_generate_code_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_generate_code(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_join_code_not_initialized() {
        privstack_shutdown();
        let code = CString::new("ALPHA-BETA-GAMMA-DELTA").unwrap();
        let r = unsafe { privstack_pairing_join_code(code.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_list_peers_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_list_peers(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_save_state_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_save_state(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_load_state_not_initialized() {
        privstack_shutdown();
        let json = CString::new("{}").unwrap();
        let r = unsafe { privstack_pairing_load_state(json.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_get_device_name_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_get_device_name(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_set_device_name_not_initialized() {
        privstack_shutdown();
        let name = CString::new("test").unwrap();
        let r = unsafe { privstack_pairing_set_device_name(name.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_trust_peer_not_initialized() {
        privstack_shutdown();
        let pid = CString::new("peer1").unwrap();
        let name = CString::new("dev").unwrap();
        let r = unsafe { privstack_pairing_trust_peer(pid.as_ptr(), name.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_remove_peer_not_initialized() {
        privstack_shutdown();
        let pid = CString::new("peer1").unwrap();
        let r = unsafe { privstack_pairing_remove_peer(pid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn pairing_get_code_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_get_code(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: EntityRegistry unit tests
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn entity_registry_new_is_empty() {
        let reg = EntityRegistry::new();
        assert!(!reg.has_schema("anything"));
        assert!(reg.get_schema("anything").is_none());
        assert!(reg.get_handler("anything").is_none());
    }

    #[test]
    fn entity_registry_register_and_get_schema() {
        let mut reg = EntityRegistry::new();
        let schema = EntitySchema {
            entity_type: "note".to_string(),
            indexed_fields: vec![],
            merge_strategy: privstack_model::MergeStrategy::LwwDocument,
        };
        reg.register_schema(schema);

        assert!(reg.has_schema("note"));
        assert!(!reg.has_schema("task"));

        let fetched = reg.get_schema("note").unwrap();
        assert_eq!(fetched.entity_type, "note");
    }

    #[test]
    fn entity_registry_overwrite_schema() {
        let mut reg = EntityRegistry::new();
        let schema1 = EntitySchema {
            entity_type: "note".to_string(),
            indexed_fields: vec![],
            merge_strategy: privstack_model::MergeStrategy::LwwDocument,
        };
        reg.register_schema(schema1);

        // Overwrite with new schema of same type
        let schema2 = EntitySchema {
            entity_type: "note".to_string(),
            indexed_fields: vec![privstack_model::IndexedField {
                field_path: "/title".to_string(),
                field_type: privstack_model::FieldType::Text,
                searchable: true,
                vector_dim: None,
                enum_options: None,
            }],
            merge_strategy: privstack_model::MergeStrategy::LwwDocument,
        };
        reg.register_schema(schema2);

        let fetched = reg.get_schema("note").unwrap();
        assert_eq!(fetched.indexed_fields.len(), 1);
    }

    #[test]
    fn entity_registry_multiple_schemas() {
        let mut reg = EntityRegistry::new();
        for name in ["note", "task", "calendar_event"] {
            reg.register_schema(EntitySchema {
                entity_type: name.to_string(),
                indexed_fields: vec![],
                merge_strategy: privstack_model::MergeStrategy::LwwDocument,
            });
        }
        assert!(reg.has_schema("note"));
        assert!(reg.has_schema("task"));
        assert!(reg.has_schema("calendar_event"));
        assert!(!reg.has_schema("contact"));
    }

    #[test]
    fn entity_registry_get_handler_none() {
        let reg = EntityRegistry::new();
        assert!(reg.get_handler("note").is_none());
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: PrivStackError exhaustive enum values
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn error_enum_all_values() {
        assert_eq!(PrivStackError::Ok as i32, 0);
        assert_eq!(PrivStackError::NullPointer as i32, 1);
        assert_eq!(PrivStackError::InvalidUtf8 as i32, 2);
        assert_eq!(PrivStackError::JsonError as i32, 3);
        assert_eq!(PrivStackError::StorageError as i32, 4);
        assert_eq!(PrivStackError::NotFound as i32, 5);
        assert_eq!(PrivStackError::NotInitialized as i32, 6);
        assert_eq!(PrivStackError::SyncNotRunning as i32, 7);
        assert_eq!(PrivStackError::SyncAlreadyRunning as i32, 8);
        assert_eq!(PrivStackError::SyncError as i32, 9);
        assert_eq!(PrivStackError::PeerNotFound as i32, 10);
        assert_eq!(PrivStackError::AuthError as i32, 11);
        assert_eq!(PrivStackError::CloudError as i32, 12);
        assert_eq!(PrivStackError::LicenseInvalidFormat as i32, 13);
        assert_eq!(PrivStackError::LicenseInvalidSignature as i32, 14);
        assert_eq!(PrivStackError::LicenseExpired as i32, 15);
        assert_eq!(PrivStackError::LicenseNotActivated as i32, 16);
        assert_eq!(PrivStackError::LicenseActivationFailed as i32, 17);
        assert_eq!(PrivStackError::InvalidSyncCode as i32, 18);
        assert_eq!(PrivStackError::PeerNotTrusted as i32, 19);
        assert_eq!(PrivStackError::PairingError as i32, 20);
        assert_eq!(PrivStackError::VaultLocked as i32, 21);
        assert_eq!(PrivStackError::VaultNotFound as i32, 22);
        assert_eq!(PrivStackError::PluginError as i32, 23);
        assert_eq!(PrivStackError::PluginNotFound as i32, 24);
        assert_eq!(PrivStackError::PluginPermissionDenied as i32, 25);
        assert_eq!(PrivStackError::Unknown as i32, 99);
    }

    #[test]
    fn error_enum_copy_clone_eq() {
        let a = PrivStackError::CloudError;
        let b = a; // Copy
        let c = a.clone(); // Clone
        assert_eq!(a, b);
        assert_eq!(a, c);
        assert_ne!(a, PrivStackError::Ok);
    }

    #[test]
    fn error_enum_debug_all_variants() {
        // Ensure Debug is derived for every variant
        let variants = [
            PrivStackError::Ok,
            PrivStackError::NullPointer,
            PrivStackError::InvalidUtf8,
            PrivStackError::JsonError,
            PrivStackError::StorageError,
            PrivStackError::NotFound,
            PrivStackError::NotInitialized,
            PrivStackError::SyncNotRunning,
            PrivStackError::SyncAlreadyRunning,
            PrivStackError::SyncError,
            PrivStackError::PeerNotFound,
            PrivStackError::AuthError,
            PrivStackError::CloudError,
            PrivStackError::LicenseInvalidFormat,
            PrivStackError::LicenseInvalidSignature,
            PrivStackError::LicenseExpired,
            PrivStackError::LicenseNotActivated,
            PrivStackError::LicenseActivationFailed,
            PrivStackError::InvalidSyncCode,
            PrivStackError::PeerNotTrusted,
            PrivStackError::PairingError,
            PrivStackError::VaultLocked,
            PrivStackError::VaultNotFound,
            PrivStackError::PluginError,
            PrivStackError::PluginNotFound,
            PrivStackError::PluginPermissionDenied,
            PrivStackError::Unknown,
        ];
        for v in &variants {
            let dbg = format!("{:?}", v);
            assert!(!dbg.is_empty());
        }
        // Verify count matches all declared variants (27 total)
        assert_eq!(variants.len(), 27);
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: CloudProvider enum
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn cloud_provider_copy_clone_eq_debug() {
        let a = CloudProvider::GoogleDrive;
        let b = a;
        let c = a.clone();
        assert_eq!(a, b);
        assert_eq!(a, c);
        assert_ne!(a, CloudProvider::ICloud);
        assert_eq!(format!("{:?}", a), "GoogleDrive");
        assert_eq!(format!("{:?}", CloudProvider::ICloud), "ICloud");
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: FfiLicensePlan / FfiLicenseStatus enum values
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn ffi_license_plan_all_values() {
        assert_eq!(FfiLicensePlan::Monthly as i32, 0);
        assert_eq!(FfiLicensePlan::Annual as i32, 1);
        assert_eq!(FfiLicensePlan::Perpetual as i32, 2);
        assert_eq!(FfiLicensePlan::Trial as i32, 3);
    }

    #[test]
    fn ffi_license_plan_debug_clone_eq() {
        let a = FfiLicensePlan::Perpetual;
        let b = a;
        assert_eq!(a, b);
        assert_eq!(format!("{:?}", a), "Perpetual");
    }

    #[test]
    fn ffi_license_status_all_values() {
        assert_eq!(FfiLicenseStatus::Active as i32, 0);
        assert_eq!(FfiLicenseStatus::Expired as i32, 1);
        assert_eq!(FfiLicenseStatus::Grace as i32, 2);
        assert_eq!(FfiLicenseStatus::ReadOnly as i32, 3);
        assert_eq!(FfiLicenseStatus::NotActivated as i32, 4);
    }

    #[test]
    fn ffi_license_status_debug_clone_eq() {
        let a = FfiLicenseStatus::Expired;
        let b = a;
        assert_eq!(a, b);
        assert_ne!(a, FfiLicenseStatus::Active);
        assert_eq!(format!("{:?}", a), "Expired");
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: JSON serialization of all FFI DTOs
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn discovered_peer_info_json_serialization() {
        let info = DiscoveredPeerInfo {
            peer_id: "peer-abc".to_string(),
            device_name: Some("MyLaptop".to_string()),
            discovery_method: "mdns".to_string(),
            addresses: vec!["192.168.1.1:9000".to_string()],
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(json.contains("\"peer_id\":\"peer-abc\""));
        assert!(json.contains("\"device_name\":\"MyLaptop\""));
        assert!(json.contains("\"discovery_method\":\"mdns\""));
        assert!(json.contains("192.168.1.1:9000"));

        // Without device name
        let info2 = DiscoveredPeerInfo {
            peer_id: "peer-xyz".to_string(),
            device_name: None,
            discovery_method: "dht".to_string(),
            addresses: vec![],
        };
        let json2 = serde_json::to_string(&info2).unwrap();
        assert!(json2.contains("\"device_name\":null"));
    }

    #[test]
    fn sync_status_json_serialization() {
        let status = SyncStatus {
            running: true,
            local_peer_id: "local-id".to_string(),
            discovered_peers: vec![
                DiscoveredPeerInfo {
                    peer_id: "p1".to_string(),
                    device_name: Some("Phone".to_string()),
                    discovery_method: "mdns".to_string(),
                    addresses: vec!["10.0.0.1:8080".to_string()],
                },
            ],
        };
        let json = serde_json::to_string(&status).unwrap();
        assert!(json.contains("\"running\":true"));
        assert!(json.contains("\"local_peer_id\":\"local-id\""));
        assert!(json.contains("\"discovered_peers\":[{"));
        assert!(json.contains("Phone"));
    }

    #[test]
    fn sync_status_empty_peers_json() {
        let status = SyncStatus {
            running: false,
            local_peer_id: "id".to_string(),
            discovered_peers: vec![],
        };
        let json = serde_json::to_string(&status).unwrap();
        assert!(json.contains("\"running\":false"));
        assert!(json.contains("\"discovered_peers\":[]"));
    }

    #[test]
    fn sync_event_dto_json_serialization_all_variants() {
        // PeerDiscovered
        let dto = SyncEventDto::from(SyncEvent::PeerDiscovered {
            peer_id: PeerId::new(),
            device_name: Some("Dev".to_string()),
        });
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.contains("\"event_type\":\"peer_discovered\""));
        assert!(json.contains("\"device_name\":\"Dev\""));

        // SyncStarted
        let dto = SyncEventDto::from(SyncEvent::SyncStarted { peer_id: PeerId::new() });
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.contains("\"event_type\":\"sync_started\""));
        assert!(json.contains("\"device_name\":null"));

        // SyncCompleted
        let dto = SyncEventDto::from(SyncEvent::SyncCompleted {
            peer_id: PeerId::new(),
            events_sent: 42,
            events_received: 7,
        });
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.contains("\"events_sent\":42"));
        assert!(json.contains("\"events_received\":7"));

        // SyncFailed
        let dto = SyncEventDto::from(SyncEvent::SyncFailed {
            peer_id: PeerId::new(),
            error: "connection lost".to_string(),
        });
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.contains("\"error\":\"connection lost\""));

        // EntityUpdated
        let eid = privstack_types::EntityId::new();
        let dto = SyncEventDto::from(SyncEvent::EntityUpdated { entity_id: eid });
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.contains("\"event_type\":\"entity_updated\""));
        assert!(json.contains("\"entity_id\":\""));
    }

    #[test]
    fn sync_event_dto_peer_discovered_no_device_name() {
        let dto = SyncEventDto::from(SyncEvent::PeerDiscovered {
            peer_id: PeerId::new(),
            device_name: None,
        });
        assert_eq!(dto.event_type, "peer_discovered");
        assert!(dto.device_name.is_none());
        assert!(dto.entity_id.is_none());
        assert!(dto.events_sent.is_none());
        assert!(dto.error.is_none());
        assert!(dto.entity_type.is_none());
        assert!(dto.json_data.is_none());
    }

    #[test]
    fn cloud_file_info_json_serialization() {
        let info = CloudFileInfo {
            id: "file-123".to_string(),
            name: "notes.txt".to_string(),
            path: "/docs/notes.txt".to_string(),
            size: 1024,
            modified_at_ms: 1700000000000,
            content_hash: Some("abc123".to_string()),
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(json.contains("\"id\":\"file-123\""));
        assert!(json.contains("\"name\":\"notes.txt\""));
        assert!(json.contains("\"size\":1024"));
        assert!(json.contains("\"modified_at_ms\":1700000000000"));
        assert!(json.contains("\"content_hash\":\"abc123\""));

        // Without content hash
        let info2 = CloudFileInfo {
            id: "f2".to_string(),
            name: "data.bin".to_string(),
            path: "/data.bin".to_string(),
            size: 0,
            modified_at_ms: 0,
            content_hash: None,
        };
        let json2 = serde_json::to_string(&info2).unwrap();
        assert!(json2.contains("\"content_hash\":null"));
    }

    #[test]
    fn license_info_json_serialization() {
        let info = LicenseInfo {
            raw: "payload.sig".to_string(),
            plan: "monthly".to_string(),
            email: "user@example.com".to_string(),
            sub: 42,
            status: "active".to_string(),
            issued_at_ms: 1700000000000,
            expires_at_ms: Some(1730000000000),
            grace_days_remaining: None,
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(json.contains("\"raw\":\"payload.sig\""));
        assert!(json.contains("\"plan\":\"monthly\""));
        assert!(json.contains("\"email\":\"user@example.com\""));
        assert!(json.contains("\"sub\":42"));
        assert!(json.contains("\"issued_at_ms\":1700000000000"));
        assert!(json.contains("\"expires_at_ms\":1730000000000"));

        // With no expiry (perpetual)
        let info2 = LicenseInfo {
            raw: "payload2.sig2".to_string(),
            plan: "perpetual".to_string(),
            email: "admin@example.com".to_string(),
            sub: 1,
            status: "active".to_string(),
            issued_at_ms: 0,
            expires_at_ms: None,
            grace_days_remaining: None,
        };
        let json2 = serde_json::to_string(&info2).unwrap();
        assert!(json2.contains("\"expires_at_ms\":null"));
    }

    #[test]
    fn activation_info_json_serialization() {
        let info = ActivationInfo {
            license_key: "payload.sig".to_string(),
            plan: "annual".to_string(),
            email: "user@example.com".to_string(),
            sub: 42,
            activated_at_ms: 1700000000000,
            expires_at_ms: Some(1730000000000),
            device_fingerprint: "fp-123".to_string(),
            status: "active".to_string(),
            is_valid: true,
            grace_days_remaining: None,
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(json.contains("\"license_key\":\"payload.sig\""));
        assert!(json.contains("\"plan\":\"annual\""));
        assert!(json.contains("\"device_fingerprint\":\"fp-123\""));
        assert!(json.contains("\"status\":\"active\""));
        assert!(json.contains("\"is_valid\":true"));
    }

    #[test]
    fn ffi_device_info_json_serialization() {
        let info = FfiDeviceInfo {
            os_name: "macOS".to_string(),
            os_version: "14.0".to_string(),
            hostname: "macbook-pro".to_string(),
            arch: "aarch64".to_string(),
            fingerprint: "fp-abc-123".to_string(),
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(json.contains("\"os_name\":\"macOS\""));
        assert!(json.contains("\"os_version\":\"14.0\""));
        assert!(json.contains("\"hostname\":\"macbook-pro\""));
        assert!(json.contains("\"arch\":\"aarch64\""));
        assert!(json.contains("\"fingerprint\":\"fp-abc-123\""));
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: SdkResponse helper methods
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn sdk_response_ok_with_data() {
        let resp = SdkResponse::ok(serde_json::json!({"id": "123", "title": "Test"}));
        assert!(resp.success);
        assert!(resp.error_code.is_none());
        assert!(resp.error_message.is_none());
        assert!(resp.data.is_some());

        let json = serde_json::to_string(&resp).unwrap();
        assert!(json.contains("\"success\":true"));
        assert!(json.contains("\"id\":\"123\""));
        // error_code/error_message should be absent (skip_serializing_if)
        assert!(!json.contains("error_code"));
        assert!(!json.contains("error_message"));
    }

    #[test]
    fn sdk_response_ok_empty() {
        let resp = SdkResponse::ok_empty();
        assert!(resp.success);
        assert!(resp.error_code.is_none());
        assert!(resp.error_message.is_none());
        assert!(resp.data.is_none());

        let json = serde_json::to_string(&resp).unwrap();
        assert!(json.contains("\"success\":true"));
        assert!(!json.contains("\"data\""));
    }

    #[test]
    fn sdk_response_err() {
        let resp = SdkResponse::err("not_found", "Entity not found");
        assert!(!resp.success);
        assert_eq!(resp.error_code.as_deref(), Some("not_found"));
        assert_eq!(resp.error_message.as_deref(), Some("Entity not found"));
        assert!(resp.data.is_none());

        let json = serde_json::to_string(&resp).unwrap();
        assert!(json.contains("\"success\":false"));
        assert!(json.contains("\"error_code\":\"not_found\""));
        assert!(json.contains("\"error_message\":\"Entity not found\""));
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: to_c_string and nullable_cstr_to_str helpers
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn to_c_string_roundtrip() {
        let s = "hello world";
        let ptr = to_c_string(s);
        assert!(!ptr.is_null());
        let recovered = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap();
        assert_eq!(recovered, s);
        unsafe { privstack_free_string(ptr) };
    }

    #[test]
    fn to_c_string_empty() {
        let ptr = to_c_string("");
        assert!(!ptr.is_null());
        let recovered = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap();
        assert_eq!(recovered, "");
        unsafe { privstack_free_string(ptr) };
    }

    #[test]
    fn to_c_string_unicode() {
        let s = "cafe\u{0301} \u{1f600}";
        let ptr = to_c_string(s);
        assert!(!ptr.is_null());
        let recovered = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap();
        assert_eq!(recovered, s);
        unsafe { privstack_free_string(ptr) };
    }

    #[test]
    fn nullable_cstr_to_str_null() {
        let result = unsafe { nullable_cstr_to_str(ptr::null()) };
        assert!(result.is_none());
    }

    #[test]
    fn nullable_cstr_to_str_valid() {
        let s = CString::new("test string").unwrap();
        let result = unsafe { nullable_cstr_to_str(s.as_ptr()) };
        assert_eq!(result, Some("test string"));
    }

    #[test]
    fn nullable_cstr_to_str_empty() {
        let s = CString::new("").unwrap();
        let result = unsafe { nullable_cstr_to_str(s.as_ptr()) };
        assert_eq!(result, Some(""));
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: SyncEventDto field completeness checks
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn sync_event_dto_sync_started_fields() {
        let pid = PeerId::new();
        let dto = SyncEventDto::from(SyncEvent::SyncStarted { peer_id: pid });
        assert_eq!(dto.event_type, "sync_started");
        assert!(dto.peer_id.is_some());
        assert!(dto.device_name.is_none());
        assert!(dto.entity_id.is_none());
        assert!(dto.events_sent.is_none());
        assert!(dto.events_received.is_none());
        assert!(dto.error.is_none());
        assert!(dto.entity_type.is_none());
        assert!(dto.json_data.is_none());
    }

    #[test]
    fn sync_event_dto_sync_completed_fields() {
        let dto = SyncEventDto::from(SyncEvent::SyncCompleted {
            peer_id: PeerId::new(),
            events_sent: 0,
            events_received: 0,
        });
        assert_eq!(dto.event_type, "sync_completed");
        assert!(dto.peer_id.is_some());
        assert_eq!(dto.events_sent, Some(0));
        assert_eq!(dto.events_received, Some(0));
        assert!(dto.device_name.is_none());
        assert!(dto.entity_id.is_none());
        assert!(dto.error.is_none());
    }

    #[test]
    fn sync_event_dto_sync_failed_fields() {
        let dto = SyncEventDto::from(SyncEvent::SyncFailed {
            peer_id: PeerId::new(),
            error: "".to_string(),
        });
        assert_eq!(dto.event_type, "sync_failed");
        assert!(dto.peer_id.is_some());
        assert_eq!(dto.error.as_deref(), Some(""));
        assert!(dto.device_name.is_none());
        assert!(dto.entity_id.is_none());
        assert!(dto.events_sent.is_none());
    }

    #[test]
    fn sync_event_dto_entity_updated_fields() {
        let eid = privstack_types::EntityId::new();
        let eid_str = eid.to_string();
        let dto = SyncEventDto::from(SyncEvent::EntityUpdated { entity_id: eid });
        assert_eq!(dto.event_type, "entity_updated");
        assert!(dto.peer_id.is_none());
        assert_eq!(dto.entity_id.as_deref(), Some(eid_str.as_str()));
        assert!(dto.device_name.is_none());
        assert!(dto.events_sent.is_none());
        assert!(dto.error.is_none());
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: SdkRequest deserialization
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn sdk_request_deserialize_full() {
        let json = r#"{"plugin_id":"notes","action":"create","entity_type":"note","entity_id":"123","payload":"{\"title\":\"hi\"}","parameters":{"limit":"10"}}"#;
        let req: SdkRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.plugin_id, "notes");
        assert_eq!(req.action, "create");
        assert_eq!(req.entity_type, "note");
        assert_eq!(req.entity_id.as_deref(), Some("123"));
        assert_eq!(req.payload.as_deref(), Some("{\"title\":\"hi\"}"));
        assert!(req.parameters.is_some());
        assert_eq!(req.parameters.as_ref().unwrap().get("limit").unwrap(), "10");
    }

    #[test]
    fn sdk_request_deserialize_minimal() {
        let json = r#"{"plugin_id":"test","action":"read_list","entity_type":"task"}"#;
        let req: SdkRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.action, "read_list");
        assert!(req.entity_id.is_none());
        assert!(req.payload.is_none());
        assert!(req.parameters.is_none());
    }

    #[test]
    fn sdk_request_deserialize_invalid() {
        let result = serde_json::from_str::<SdkRequest>("not json");
        assert!(result.is_err());
    }

    #[test]
    fn sdk_request_deserialize_missing_fields() {
        // Missing required fields
        let result = serde_json::from_str::<SdkRequest>(r#"{"action":"read"}"#);
        assert!(result.is_err());
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: SdkResponse JSON round-trip
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn sdk_response_ok_data_json_roundtrip() {
        let resp = SdkResponse::ok(serde_json::json!({
            "items": [{"id": "1"}, {"id": "2"}],
            "count": 2
        }));
        let json = serde_json::to_string(&resp).unwrap();
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(parsed["success"], true);
        assert_eq!(parsed["data"]["count"], 2);
        assert_eq!(parsed["data"]["items"].as_array().unwrap().len(), 2);
    }

    #[test]
    fn sdk_response_err_json_roundtrip() {
        let resp = SdkResponse::err("storage_error", "Disk full");
        let json = serde_json::to_string(&resp).unwrap();
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(parsed["success"], false);
        assert_eq!(parsed["error_code"], "storage_error");
        assert_eq!(parsed["error_message"], "Disk full");
        assert!(parsed.get("data").is_none());
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: Free bytes with valid allocation
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn free_bytes_zero_len() {
        // Should be a no-op (len == 0 guard)
        unsafe { privstack_free_bytes(ptr::null_mut(), 0) };
    }

    #[test]
    fn free_bytes_valid_allocation() {
        let data = vec![1u8, 2, 3, 4, 5];
        let len = data.len();
        let boxed = data.into_boxed_slice();
        let ptr = Box::into_raw(boxed) as *mut u8;
        unsafe { privstack_free_bytes(ptr, len) };
    }

    // ══════════════════════════════════════════════════════════════
    // Phase 7: ActivationInfo with no expiry
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn activation_info_no_expiry() {
        let info = ActivationInfo {
            license_key: "payload.sig".to_string(),
            plan: "perpetual".to_string(),
            email: "test@example.com".to_string(),
            sub: 1,
            activated_at_ms: 0,
            expires_at_ms: None,
            device_fingerprint: "fp".to_string(),
            status: "active".to_string(),
            is_valid: true,
            grace_days_remaining: None,
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(json.contains("\"expires_at_ms\":null"));
        assert!(json.contains("\"is_valid\":true"));
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Personal Sharing Functions
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn share_entity_null_entity_id() {
        let r = unsafe { privstack_share_entity_with_peer(ptr::null(), ptr::null()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    fn share_entity_null_peer_id() {
        let eid = CString::new("some-id").unwrap();
        let r = unsafe { privstack_share_entity_with_peer(eid.as_ptr(), ptr::null()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    fn unshare_entity_null() {
        let r = unsafe { privstack_unshare_entity_with_peer(ptr::null(), ptr::null()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    fn unshare_entity_null_peer_id() {
        let eid = CString::new("some-id").unwrap();
        let r = unsafe { privstack_unshare_entity_with_peer(eid.as_ptr(), ptr::null()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    fn list_shared_peers_null() {
        let r = unsafe { privstack_list_shared_peers(ptr::null(), ptr::null_mut()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    fn list_shared_peers_null_out() {
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let r = unsafe { privstack_list_shared_peers(eid.as_ptr(), ptr::null_mut()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    #[serial]
    fn share_entity_not_initialized() {
        privstack_shutdown();
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let pid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let r = unsafe { privstack_share_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn unshare_entity_not_initialized() {
        privstack_shutdown();
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let pid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let r = unsafe { privstack_unshare_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn list_shared_peers_not_initialized() {
        privstack_shutdown();
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_list_shared_peers(eid.as_ptr(), &mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn share_entity_invalid_uuid() {
        let eid = CString::new("not-a-uuid").unwrap();
        let pid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let r = unsafe { privstack_share_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);
    }

    #[test]
    fn share_entity_invalid_peer_uuid() {
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let pid = CString::new("not-a-uuid").unwrap();
        let r = unsafe { privstack_share_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);
    }

    #[test]
    fn unshare_entity_invalid_uuid() {
        let eid = CString::new("not-a-uuid").unwrap();
        let pid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let r = unsafe { privstack_unshare_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);
    }

    #[test]
    fn unshare_entity_invalid_peer_uuid() {
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let pid = CString::new("not-a-uuid").unwrap();
        let r = unsafe { privstack_unshare_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);
    }

    #[test]
    fn list_shared_peers_invalid_uuid() {
        let eid = CString::new("not-a-uuid").unwrap();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_list_shared_peers(eid.as_ptr(), &mut out) };
        assert_eq!(r, PrivStackError::JsonError);
    }

    #[test]
    #[serial]
    fn share_unshare_list_lifecycle() {
        test_init();

        // No personal_policy set (sync not started), so share/unshare are no-ops but succeed
        let eid = CString::new(Uuid::new_v4().to_string()).unwrap();
        let pid = CString::new(Uuid::new_v4().to_string()).unwrap();

        let r = unsafe { privstack_share_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        let r = unsafe { privstack_unshare_entity_with_peer(eid.as_ptr(), pid.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_list_shared_peers(eid.as_ptr(), &mut out) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!out.is_null());
        let json = unsafe { CStr::from_ptr(out) }.to_str().unwrap();
        assert!(json.contains("[]"));
        unsafe { privstack_free_string(out) };

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Plugin FFI Functions (requires wasm-plugins feature)
    // ══════════════════════════════════════════════════════════════

    #[cfg(feature = "wasm-plugins")]
    mod plugin_tests {
    use super::*;
    use crate::plugin_ffi::*;

    #[test]
    #[serial]
    fn plugin_list_not_initialized() {
        privstack_shutdown();
        let result = privstack_plugin_list();
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "[]");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_list_empty() {
        test_init();

        let result = privstack_plugin_list();
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("[]"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_get_nav_items_not_initialized() {
        privstack_shutdown();
        let result = privstack_plugin_get_nav_items();
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "[]");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_get_nav_items_empty() {
        test_init();

        let result = privstack_plugin_get_nav_items();
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_count_not_initialized() {
        privstack_shutdown();
        assert_eq!(privstack_plugin_count(), 0);
    }

    #[test]
    #[serial]
    fn plugin_count_zero() {
        test_init();
        assert_eq!(privstack_plugin_count(), 0);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_is_loaded_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test-plugin").unwrap();
        assert!(!unsafe { privstack_plugin_is_loaded(id.as_ptr()) });
    }

    #[test]
    fn plugin_is_loaded_null() {
        assert!(!unsafe { privstack_plugin_is_loaded(ptr::null()) });
    }

    #[test]
    #[serial]
    fn plugin_is_loaded_false() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        assert!(!unsafe { privstack_plugin_is_loaded(id.as_ptr()) });

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_get_commands_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let result = unsafe { privstack_plugin_get_commands(id.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "[]");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn plugin_get_commands_null() {
        let result = unsafe { privstack_plugin_get_commands(ptr::null()) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_get_commands_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let result = unsafe { privstack_plugin_get_commands(id.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "[]");
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_get_link_providers_not_initialized() {
        privstack_shutdown();
        let result = privstack_plugin_get_link_providers();
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "[]");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_get_link_providers_empty() {
        test_init();

        let result = privstack_plugin_get_link_providers();
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_search_items_not_initialized() {
        privstack_shutdown();
        let q = CString::new("test").unwrap();
        let result = unsafe { privstack_plugin_search_items(q.as_ptr(), 10) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "[]");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn plugin_search_items_null_query() {
        let result = unsafe { privstack_plugin_search_items(ptr::null(), 10) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_search_items_empty() {
        test_init();

        let q = CString::new("test").unwrap();
        let result = unsafe { privstack_plugin_search_items(q.as_ptr(), 5) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_not_initialized() {
        privstack_shutdown();
        let meta = CString::new("{}").unwrap();
        let schemas = CString::new("[]").unwrap();
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_load_null_metadata() {
        test_init();
        let schemas = CString::new("[]").unwrap();
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_load(ptr::null(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::NullPointer);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_null_schemas() {
        test_init();
        let meta = CString::new("{}").unwrap();
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), ptr::null(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::NullPointer);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_null_permissions() {
        test_init();
        let meta = CString::new("{}").unwrap();
        let schemas = CString::new("[]").unwrap();
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), ptr::null()) };
        assert_eq!(r, PrivStackError::NullPointer);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_invalid_metadata_json() {
        test_init();

        let meta = CString::new("not json").unwrap();
        let schemas = CString::new("[]").unwrap();
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_invalid_schemas_json() {
        test_init();

        let meta = CString::new(r#"{"id":"test","name":"Test","description":"d","version":"1.0","author":"a","icon":"i","navigation_order":100,"category":"utility","can_disable":true,"is_experimental":false}"#).unwrap();
        let schemas = CString::new("not json").unwrap();
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_invalid_permissions_json() {
        test_init();

        let meta = CString::new(r#"{"id":"test","name":"Test","description":"d","version":"1.0","author":"a","icon":"i","navigation_order":100,"category":"utility","can_disable":true,"is_experimental":false}"#).unwrap();
        let schemas = CString::new("[]").unwrap();
        let perms = CString::new("not json").unwrap();
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_unload_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let r = unsafe { privstack_plugin_unload(id.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_unload_null() {
        let r = unsafe { privstack_plugin_unload(ptr::null()) };
        // Will get NullPointer or NotInitialized depending on HANDLE state
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_unload_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let r = unsafe { privstack_plugin_unload(id.as_ptr()) };
        assert_eq!(r, PrivStackError::PluginNotFound);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_route_sdk_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let msg = CString::new("{}").unwrap();
        let result = unsafe { privstack_plugin_route_sdk(id.as_ptr(), msg.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("not_initialized"));
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_route_sdk_null_plugin_id() {
        test_init();
        let msg = CString::new("{}").unwrap();
        let result = unsafe { privstack_plugin_route_sdk(ptr::null(), msg.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null_plugin_id"));
        unsafe { privstack_free_string(result) };
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_route_sdk_null_message() {
        test_init();
        let id = CString::new("test").unwrap();
        let result = unsafe { privstack_plugin_route_sdk(id.as_ptr(), ptr::null()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null_message"));
        unsafe { privstack_free_string(result) };
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_route_sdk_invalid_json() {
        test_init();

        let id = CString::new("test").unwrap();
        let msg = CString::new("not json").unwrap();
        let result = unsafe { privstack_plugin_route_sdk(id.as_ptr(), msg.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("invalid_json"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_send_command_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let cmd = CString::new("do_thing").unwrap();
        let args = CString::new("{}").unwrap();
        let result = unsafe { privstack_plugin_send_command(id.as_ptr(), cmd.as_ptr(), args.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("not_initialized"));
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_send_command_null_plugin_id() {
        test_init();
        let cmd = CString::new("do_thing").unwrap();
        let args = CString::new("{}").unwrap();
        let result = unsafe { privstack_plugin_send_command(ptr::null(), cmd.as_ptr(), args.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null_plugin_id"));
        unsafe { privstack_free_string(result) };
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_send_command_null_command() {
        test_init();
        let id = CString::new("test").unwrap();
        let args = CString::new("{}").unwrap();
        let result = unsafe { privstack_plugin_send_command(id.as_ptr(), ptr::null(), args.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null_command"));
        unsafe { privstack_free_string(result) };
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_send_command_null_args() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let cmd = CString::new("do_thing").unwrap();
        let result = unsafe { privstack_plugin_send_command(id.as_ptr(), cmd.as_ptr(), ptr::null()) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_fetch_url_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let url = CString::new("https://example.com").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_plugin_fetch_url(id.as_ptr(), url.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_fetch_url_null_plugin_id() {
        test_init();
        let url = CString::new("https://example.com").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_plugin_fetch_url(ptr::null(), url.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NullPointer);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_fetch_url_null_url() {
        test_init();
        let id = CString::new("test").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_plugin_fetch_url(id.as_ptr(), ptr::null(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NullPointer);
        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_get_view_state_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let result = unsafe { privstack_plugin_get_view_state(id.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("Core not initialized"));
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn plugin_get_view_state_null() {
        let result = unsafe { privstack_plugin_get_view_state(ptr::null()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("null") || json.contains("error"));
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_get_view_state_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let result = unsafe { privstack_plugin_get_view_state(id.as_ptr()) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_get_view_data_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let result = unsafe { privstack_plugin_get_view_data(id.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "{}");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn plugin_get_view_data_null() {
        let result = unsafe { privstack_plugin_get_view_data(ptr::null()) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };
    }

    #[test]
    #[serial]
    fn plugin_get_view_data_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let result = unsafe { privstack_plugin_get_view_data(id.as_ptr()) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_activate_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let r = unsafe { privstack_plugin_activate(id.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_activate_null() {
        let r = unsafe { privstack_plugin_activate(ptr::null()) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_activate_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let r = unsafe { privstack_plugin_activate(id.as_ptr()) };
        assert_eq!(r, PrivStackError::PluginError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_navigated_to_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let r = unsafe { privstack_plugin_navigated_to(id.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_navigated_to_null() {
        let r = unsafe { privstack_plugin_navigated_to(ptr::null()) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_navigated_to_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let r = unsafe { privstack_plugin_navigated_to(id.as_ptr()) };
        assert_eq!(r, PrivStackError::PluginError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_navigated_from_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let r = unsafe { privstack_plugin_navigated_from(id.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_navigated_from_null() {
        let r = unsafe { privstack_plugin_navigated_from(ptr::null()) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_navigated_from_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let r = unsafe { privstack_plugin_navigated_from(id.as_ptr()) };
        assert_eq!(r, PrivStackError::PluginError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_update_permissions_not_initialized() {
        privstack_shutdown();
        let id = CString::new("test").unwrap();
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_update_permissions(id.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_update_permissions_null_id() {
        let perms = CString::new("{}").unwrap();
        let r = unsafe { privstack_plugin_update_permissions(ptr::null(), perms.as_ptr()) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_update_permissions_null_perms() {
        let id = CString::new("test").unwrap();
        let r = unsafe { privstack_plugin_update_permissions(id.as_ptr(), ptr::null()) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_update_permissions_invalid_json() {
        test_init();

        let id = CString::new("test").unwrap();
        let perms = CString::new("not json").unwrap();
        let r = unsafe { privstack_plugin_update_permissions(id.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::JsonError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_update_permissions_not_found() {
        test_init();

        let id = CString::new("nonexistent").unwrap();
        let perms = CString::new(r#"{"granted":["sdk"],"denied":[],"pending_jit":[]}"#).unwrap();
        let r = unsafe { privstack_plugin_update_permissions(id.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::PluginNotFound);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_install_ppk_not_initialized() {
        privstack_shutdown();
        let path = CString::new("/nonexistent.ppk").unwrap();
        let r = unsafe { privstack_plugin_install_ppk(path.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_install_ppk_null() {
        let r = unsafe { privstack_plugin_install_ppk(ptr::null()) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_install_ppk_file_not_found() {
        test_init();

        let ppk_path = CString::new("/tmp/nonexistent_test.ppk").unwrap();
        let r = unsafe { privstack_plugin_install_ppk(ppk_path.as_ptr()) };
        assert_eq!(r, PrivStackError::NotFound);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_wasm_not_initialized() {
        privstack_shutdown();
        let path = CString::new("/nonexistent.wasm").unwrap();
        let perms = CString::new("{}").unwrap();
        let mut out_id: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_plugin_load_wasm(path.as_ptr(), perms.as_ptr(), &mut out_id) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn plugin_load_wasm_null_path() {
        let perms = CString::new("{}").unwrap();
        let mut out_id: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_plugin_load_wasm(ptr::null(), perms.as_ptr(), &mut out_id) };
        assert!(r == PrivStackError::NullPointer || r == PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn plugin_load_wasm_file_not_found() {
        test_init();

        let wasm_path = CString::new("/tmp/nonexistent_test.wasm").unwrap();
        let perms = CString::new("{}").unwrap();
        let mut out_id: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_plugin_load_wasm(wasm_path.as_ptr(), perms.as_ptr(), &mut out_id) };
        assert_eq!(r, PrivStackError::PluginError);

        privstack_shutdown();
    }

    #[test]
    #[serial]
    fn plugin_load_wasm_null_permissions() {
        test_init();

        let wasm_path = CString::new("/tmp/nonexistent_test.wasm").unwrap();
        let mut out_id: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_plugin_load_wasm(wasm_path.as_ptr(), ptr::null(), &mut out_id) };
        // Null permissions should use default_first_party, then fail on file not found
        assert_eq!(r, PrivStackError::PluginError);

        privstack_shutdown();
    }

    #[test]
    fn ppk_inspect_null() {
        let result = unsafe { privstack_ppk_inspect(ptr::null()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "{}");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn ppk_inspect_file_not_found() {
        let path = CString::new("/tmp/nonexistent_test.ppk").unwrap();
        let result = unsafe { privstack_ppk_inspect(path.as_ptr()) };
        assert!(!result.is_null());
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(json, "{}");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn ppk_content_hash_null() {
        let result = unsafe { privstack_ppk_content_hash(ptr::null()) };
        assert!(!result.is_null());
        let s = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(s, "");
        unsafe { privstack_free_string(result) };
    }

    #[test]
    fn ppk_content_hash_file_not_found() {
        let path = CString::new("/tmp/nonexistent_test.ppk").unwrap();
        let result = unsafe { privstack_ppk_content_hash(path.as_ptr()) };
        assert!(!result.is_null());
        let s = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert_eq!(s, "");
        unsafe { privstack_free_string(result) };
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Cloud iCloud operations not initialized
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn cloud_icloud_operations_without_provider_init() {
        test_init();

        let mut out_auth_url: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_cloud_authenticate(CloudProvider::ICloud, &mut out_auth_url) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let code = CString::new("code").unwrap();
        let r = unsafe { privstack_cloud_complete_auth(CloudProvider::ICloud, code.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let mut out_json: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_cloud_list_files(CloudProvider::ICloud, &mut out_json) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let name = CString::new("f.txt").unwrap();
        let data = b"x";
        let r = unsafe { privstack_cloud_upload(CloudProvider::ICloud, name.as_ptr(), data.as_ptr(), data.len(), &mut out_json) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let fid = CString::new("fid").unwrap();
        let mut out_data: *mut u8 = ptr::null_mut();
        let mut out_len: usize = 0;
        let r = unsafe { privstack_cloud_download(CloudProvider::ICloud, fid.as_ptr(), &mut out_data, &mut out_len) };
        assert_eq!(r, PrivStackError::NotInitialized);

        let r = unsafe { privstack_cloud_delete(CloudProvider::ICloud, fid.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Sync status/trigger/poll not initialized
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn sync_is_running_not_initialized() {
        privstack_shutdown();
        assert!(!privstack_sync_is_running());
    }

    #[test]
    #[serial]
    fn sync_status_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_sync_status(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn sync_trigger_not_initialized() {
        privstack_shutdown();
        let r = privstack_sync_trigger();
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn sync_poll_event_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_sync_poll_event(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    fn sync_poll_event_null() {
        let r = unsafe { privstack_sync_poll_event(ptr::null_mut()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    #[serial]
    fn sync_publish_event_not_initialized() {
        privstack_shutdown();
        let event = privstack_types::Event::new(
            privstack_types::EntityId::new(),
            PeerId::new(),
            privstack_types::HybridTimestamp::now(),
            privstack_types::EventPayload::EntityCreated {
                entity_type: "test".to_string(),
                json_data: "{}".to_string(),
            },
        );
        let json = CString::new(serde_json::to_string(&event).unwrap()).unwrap();
        let r = unsafe { privstack_sync_publish_event(json.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Cloud not-initialized paths
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn cloud_init_google_drive_not_initialized() {
        privstack_shutdown();
        let cid = CString::new("id").unwrap();
        let csec = CString::new("sec").unwrap();
        let r = unsafe { privstack_cloud_init_google_drive(cid.as_ptr(), csec.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn cloud_init_icloud_not_initialized() {
        privstack_shutdown();
        let r = unsafe { privstack_cloud_init_icloud(ptr::null()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: License checks not initialized
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn license_check_not_initialized() {
        privstack_shutdown();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_license_check(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn license_status_not_initialized() {
        privstack_shutdown();
        let mut out = FfiLicenseStatus::Active;
        let r = unsafe { privstack_license_status(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn license_activated_plan_not_initialized() {
        privstack_shutdown();
        let mut out = FfiLicensePlan::Monthly;
        let r = unsafe { privstack_license_activated_plan(&mut out) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    #[test]
    #[serial]
    fn license_activate_not_initialized() {
        privstack_shutdown();
        let key = CString::new("PS-KEY-1234").unwrap();
        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_license_activate(key.as_ptr(), &mut out) };
        // Will fail on parse before reaching NotInitialized, or hit NotInitialized
        assert_ne!(r, PrivStackError::Ok);
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Vault is_initialized/is_unlocked not initialized
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn vault_is_initialized_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        assert!(!unsafe { privstack_vault_is_initialized(vid.as_ptr()) });
    }

    #[test]
    #[serial]
    fn vault_is_unlocked_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        assert!(!unsafe { privstack_vault_is_unlocked(vid.as_ptr()) });
    }

    #[test]
    #[serial]
    fn vault_change_password_not_initialized() {
        privstack_shutdown();
        let vid = CString::new("v").unwrap();
        let old = CString::new("old123456").unwrap();
        let new = CString::new("new123456").unwrap();
        let r = unsafe { privstack_vault_change_password(vid.as_ptr(), old.as_ptr(), new.as_ptr()) };
        assert_eq!(r, PrivStackError::NotInitialized);
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Plugin load + unload lifecycle
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn plugin_load_and_unload_lifecycle() {
        test_init();

        let meta = CString::new(r#"{"id":"test-plugin","name":"Test Plugin","description":"A test","version":"1.0.0","author":"Test","icon":"plug","navigation_order":100,"category":"utility","can_disable":true,"is_experimental":false}"#).unwrap();
        let schemas = CString::new("[]").unwrap();
        let perms = CString::new(r#"{"granted":["sdk","settings","logger","navigation","state-notify"],"denied":[],"pending_jit":[]}"#).unwrap();

        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        assert_eq!(privstack_plugin_count(), 1);

        let id = CString::new("test-plugin").unwrap();
        assert!(unsafe { privstack_plugin_is_loaded(id.as_ptr()) });

        // List plugins
        let result = privstack_plugin_list();
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("test-plugin"));
        unsafe { privstack_free_string(result) };

        // Nav items
        let result = privstack_plugin_get_nav_items();
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        // Link providers
        let result = privstack_plugin_get_link_providers();
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        // Get commands
        let result = unsafe { privstack_plugin_get_commands(id.as_ptr()) };
        assert!(!result.is_null());
        unsafe { privstack_free_string(result) };

        // Loading same plugin again should fail
        let r = unsafe { privstack_plugin_load(meta.as_ptr(), schemas.as_ptr(), perms.as_ptr()) };
        assert_eq!(r, PrivStackError::PluginError);

        // Unload
        let r = unsafe { privstack_plugin_unload(id.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!unsafe { privstack_plugin_is_loaded(id.as_ptr()) });
        assert_eq!(privstack_plugin_count(), 0);

        privstack_shutdown();
    }

    } // mod plugin_tests (cfg wasm-plugins)

    // ══════════════════════════════════════════════════════════════
    // Coverage: Trusted peers via pairing_get_trusted_peers
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn pairing_get_trusted_peers_null() {
        let r = unsafe { privstack_pairing_get_trusted_peers(ptr::null_mut()) };
        assert_eq!(r, PrivStackError::NullPointer);
    }

    #[test]
    #[serial]
    fn pairing_get_trusted_peers_lifecycle() {
        test_init();

        let mut out: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_get_trusted_peers(&mut out) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!out.is_null());
        unsafe { privstack_free_string(out) };

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Cloud provider name without init
    // ══════════════════════════════════════════════════════════════

    #[test]
    fn cloud_provider_name_standalone() {
        let name = privstack_cloud_provider_name(CloudProvider::GoogleDrive);
        let s = unsafe { CStr::from_ptr(name) }.to_str().unwrap();
        assert_eq!(s, "Google Drive");

        let name = privstack_cloud_provider_name(CloudProvider::ICloud);
        let s = unsafe { CStr::from_ptr(name) }.to_str().unwrap();
        assert_eq!(s, "iCloud Drive");
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Execute query with filters payload
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn execute_query_with_limit_param() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"qlimit_item","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let create = CString::new(r#"{"plugin_id":"test","action":"create","entity_type":"qlimit_item","payload":"{\"n\":1}"}"#).unwrap();
        let r = unsafe { privstack_execute(create.as_ptr()) };
        unsafe { privstack_free_string(r) };

        let query = CString::new(r#"{"plugin_id":"test","action":"query","entity_type":"qlimit_item","parameters":{"limit":"1"}}"#).unwrap();
        let result = unsafe { privstack_execute(query.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Execute update with nonexistent entity_id
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn execute_update_nonexistent_entity() {
        test_init();

        let schema = CString::new(r#"{"entity_type":"upd_ne","indexed_fields":[],"merge_strategy":"lww_document"}"#).unwrap();
        unsafe { privstack_register_entity_type(schema.as_ptr()) };

        let req = CString::new(r#"{"plugin_id":"test","action":"update","entity_type":"upd_ne","entity_id":"nonexistent-id","payload":"{\"title\":\"New\"}"}"#).unwrap();
        let result = unsafe { privstack_execute(req.as_ptr()) };
        let json = unsafe { CStr::from_ptr(result) }.to_str().unwrap();
        assert!(json.contains("\"success\":true"));
        unsafe { privstack_free_string(result) };

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Sync start with pairing code set
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn sync_start_with_pairing_code() {
        test_init();

        // Generate pairing code first
        let mut out_code: *mut c_char = ptr::null_mut();
        unsafe { privstack_pairing_generate_code(&mut out_code) };
        if !out_code.is_null() {
            unsafe { privstack_free_string(out_code) };
        }

        // Start sync with code set
        let r = privstack_sync_start();
        assert_eq!(r, PrivStackError::Ok);

        // Check running
        assert!(privstack_sync_is_running());

        // Stop
        let r = privstack_sync_stop();
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Sync stop when not running
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn sync_stop_when_not_running() {
        test_init();

        // Stop without starting should be OK (no-op for None handles)
        let r = privstack_sync_stop();
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }

    // ══════════════════════════════════════════════════════════════
    // Coverage: Pairing join with valid code
    // ══════════════════════════════════════════════════════════════

    #[test]
    #[serial]
    fn pairing_join_with_generated_code() {
        test_init();

        // Generate a code
        let mut out_code: *mut c_char = ptr::null_mut();
        let r = unsafe { privstack_pairing_generate_code(&mut out_code) };
        assert_eq!(r, PrivStackError::Ok);
        assert!(!out_code.is_null());
        let code_json = unsafe { CStr::from_ptr(out_code) }.to_str().unwrap();
        let parsed: serde_json::Value = serde_json::from_str(code_json).expect("should be valid JSON");
        let code_str = parsed["code"].as_str().expect("should have code field").to_string();
        unsafe { privstack_free_string(out_code) };

        // Join with that code
        let code = CString::new(code_str).unwrap();
        let r = unsafe { privstack_pairing_join_code(code.as_ptr()) };
        assert_eq!(r, PrivStackError::Ok);

        privstack_shutdown();
    }
}
