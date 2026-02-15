//! Shared types for cloud sync operations.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// STS temporary credentials for S3 access.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct StsCredentials {
    pub access_key_id: String,
    pub secret_access_key: String,
    pub session_token: String,
    pub expires_at: DateTime<Utc>,
    pub bucket: String,
    pub region: String,
}

impl StsCredentials {
    /// Returns true if credentials will expire within the given seconds.
    pub fn expires_within_secs(&self, secs: i64) -> bool {
        Utc::now() + chrono::Duration::seconds(secs) >= self.expires_at
    }

    pub fn is_expired(&self) -> bool {
        Utc::now() >= self.expires_at
    }
}

/// A cloud-registered workspace.
///
/// The register endpoint returns a minimal `{ workspace_id, s3_prefix, id }`,
/// while the list endpoint returns all columns. Missing fields use defaults.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct CloudWorkspace {
    #[serde(default)]
    pub id: i64,
    #[serde(default)]
    pub user_id: i64,
    pub workspace_id: String,
    #[serde(default)]
    pub workspace_name: String,
    pub s3_prefix: String,
    #[serde(default)]
    pub storage_used_bytes: u64,
    #[serde(default)]
    pub storage_quota_bytes: u64,
    #[serde(default = "Utc::now")]
    pub created_at: DateTime<Utc>,
}

/// Per-device cursor position for an entity.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SyncCursor {
    pub entity_id: String,
    pub cursor_position: i64,
    pub last_batch_key: Option<String>,
}

/// Metadata for an uploaded batch on S3.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct BatchMeta {
    pub s3_key: String,
    pub cursor_start: i64,
    pub cursor_end: i64,
    pub size_bytes: u64,
    pub event_count: u32,
    pub is_snapshot: bool,
}

/// Sharing info for an entity.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ShareInfo {
    pub share_id: i64,
    pub entity_id: String,
    pub entity_type: String,
    pub entity_name: Option<String>,
    pub recipient_email: String,
    pub permission: SharePermission,
    pub status: ShareStatus,
    pub created_at: DateTime<Utc>,
    pub accepted_at: Option<DateTime<Utc>>,
}

/// Storage quota info.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct QuotaInfo {
    pub storage_used_bytes: u64,
    pub storage_quota_bytes: u64,
    /// The API returns this as a string (e.g. `"10.00"` via `.toFixed(2)`).
    #[serde(deserialize_with = "deserialize_f64_from_str_or_num")]
    pub usage_percent: f64,
}

/// Accepts either a JSON number or a string-encoded number (e.g. `"10.00"`).
fn deserialize_f64_from_str_or_num<'de, D>(deserializer: D) -> Result<f64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    use serde::de;

    struct F64Visitor;
    impl<'de> de::Visitor<'de> for F64Visitor {
        type Value = f64;
        fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
            f.write_str("a number or string-encoded number")
        }
        fn visit_f64<E: de::Error>(self, v: f64) -> Result<f64, E> { Ok(v) }
        fn visit_u64<E: de::Error>(self, v: u64) -> Result<f64, E> { Ok(v as f64) }
        fn visit_i64<E: de::Error>(self, v: i64) -> Result<f64, E> { Ok(v as f64) }
        fn visit_str<E: de::Error>(self, v: &str) -> Result<f64, E> {
            v.parse().map_err(de::Error::custom)
        }
    }
    deserializer.deserialize_any(F64Visitor)
}

/// An entity shared with the current user.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SharedEntity {
    pub entity_id: String,
    pub entity_type: String,
    pub entity_name: Option<String>,
    pub owner_user_id: i64,
    pub workspace_id: String,
    pub permission: SharePermission,
}

/// Share permission level.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SharePermission {
    Read,
    Write,
}

/// Share status.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ShareStatus {
    Pending,
    Accepted,
    Revoked,
}

/// Blob metadata.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct BlobMeta {
    pub blob_id: String,
    pub entity_id: Option<String>,
    pub s3_key: String,
    pub size_bytes: u64,
    pub content_hash: Option<String>,
}

/// Authentication tokens from the API.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AuthTokens {
    pub access_token: String,
    pub refresh_token: String,
    pub user_id: i64,
    pub email: String,
}

/// Device registration info.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceInfo {
    pub device_id: String,
    pub device_name: Option<String>,
    pub platform: Option<String>,
    pub last_seen_at: Option<DateTime<Utc>>,
}

/// Entities with pending changes for a device.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingChanges {
    pub entities: Vec<PendingEntity>,
}

/// An entity with batches to download.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingEntity {
    pub entity_id: String,
    pub current_cursor: i64,
    pub device_cursor: i64,
    pub batches: Vec<BatchMeta>,
}

/// Request to advance a cursor after batch upload.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AdvanceCursorRequest {
    pub workspace_id: String,
    pub device_id: String,
    pub entity_id: String,
    pub cursor_position: i64,
    pub batch_key: String,
    pub size_bytes: u64,
    pub event_count: u32,
}

/// Request to create a share.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct CreateShareRequest {
    pub entity_id: String,
    pub entity_type: String,
    pub entity_name: Option<String>,
    pub workspace_id: String,
    pub recipient_email: String,
    pub permission: SharePermission,
}

/// Request to register a blob.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RegisterBlobRequest {
    pub workspace_id: String,
    pub blob_id: String,
    pub entity_id: Option<String>,
    pub s3_key: String,
    pub size_bytes: u64,
    pub content_hash: Option<String>,
}

/// Cloud sync status reported to the UI.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct CloudSyncStatus {
    pub is_syncing: bool,
    pub is_authenticated: bool,
    pub active_workspace: Option<String>,
    pub pending_upload_count: usize,
    pub last_sync_at: Option<DateTime<Utc>>,
    pub connected_devices: usize,
}

/// Commands sent to the cloud sync engine.
#[derive(Debug)]
pub enum CloudCommand {
    Stop,
    ForceFlush,
    ShareEntity {
        entity_id: String,
        entity_type: String,
        recipient_email: String,
        permission: SharePermission,
    },
}
