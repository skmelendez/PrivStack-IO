use chrono::{Duration, Utc};
use privstack_cloud::*;

// --- StsCredentials ---

fn make_creds(expires_in_secs: i64) -> StsCredentials {
    StsCredentials {
        access_key_id: "AKIA_TEST".into(),
        secret_access_key: "secret".into(),
        session_token: "token".into(),
        expires_at: Utc::now() + Duration::seconds(expires_in_secs),
        bucket: "test-bucket".into(),
        region: "us-east-1".into(),
    }
}

#[test]
fn sts_is_expired_when_past() {
    let creds = make_creds(-10);
    assert!(creds.is_expired());
}

#[test]
fn sts_not_expired_when_future() {
    let creds = make_creds(3600);
    assert!(!creds.is_expired());
}

#[test]
fn sts_expires_within_secs_true() {
    let creds = make_creds(100);
    assert!(creds.expires_within_secs(200));
}

#[test]
fn sts_expires_within_secs_false() {
    let creds = make_creds(3600);
    assert!(!creds.expires_within_secs(300));
}

#[test]
fn sts_expires_within_secs_exact_boundary() {
    // Just within the margin
    let creds = make_creds(299);
    assert!(creds.expires_within_secs(300));
}

// --- Serialization roundtrips ---

#[test]
fn sts_credentials_roundtrip() {
    let creds = make_creds(3600);
    let json = serde_json::to_string(&creds).unwrap();
    let de: StsCredentials = serde_json::from_str(&json).unwrap();
    assert_eq!(de.access_key_id, "AKIA_TEST");
    assert_eq!(de.bucket, "test-bucket");
}

#[test]
fn cloud_workspace_roundtrip() {
    let ws = CloudWorkspace {
        id: 1,
        user_id: 42,
        workspace_id: "ws-uuid".into(),
        workspace_name: "My WS".into(),
        s3_prefix: "users/42/workspaces/ws-uuid".into(),
        storage_used_bytes: 1024,
        storage_quota_bytes: 1_000_000,
        created_at: Utc::now(),
    };
    let json = serde_json::to_string(&ws).unwrap();
    let de: CloudWorkspace = serde_json::from_str(&json).unwrap();
    assert_eq!(de.workspace_id, "ws-uuid");
    assert_eq!(de.storage_used_bytes, 1024);
}

#[test]
fn sync_cursor_roundtrip() {
    let cursor = SyncCursor {
        entity_id: "e-1".into(),
        cursor_position: 42,
        last_batch_key: Some("batch.enc".into()),
    };
    let json = serde_json::to_string(&cursor).unwrap();
    let de: SyncCursor = serde_json::from_str(&json).unwrap();
    assert_eq!(de.cursor_position, 42);
    assert_eq!(de.last_batch_key, Some("batch.enc".into()));
}

#[test]
fn batch_meta_roundtrip() {
    let meta = BatchMeta {
        s3_key: "key.enc".into(),
        cursor_start: 0,
        cursor_end: 10,
        size_bytes: 512,
        event_count: 5,
        is_snapshot: false,
    };
    let json = serde_json::to_string(&meta).unwrap();
    let de: BatchMeta = serde_json::from_str(&json).unwrap();
    assert_eq!(de.s3_key, "key.enc");
    assert!(!de.is_snapshot);
}

#[test]
fn share_info_roundtrip() {
    let info = ShareInfo {
        share_id: 1,
        entity_id: "e-1".into(),
        entity_type: "note".into(),
        entity_name: Some("My Note".into()),
        recipient_email: "bob@example.com".into(),
        permission: SharePermission::Read,
        status: ShareStatus::Pending,
        created_at: Utc::now(),
        accepted_at: None,
    };
    let json = serde_json::to_string(&info).unwrap();
    let de: ShareInfo = serde_json::from_str(&json).unwrap();
    assert_eq!(de.entity_id, "e-1");
    assert_eq!(de.permission, SharePermission::Read);
}

#[test]
fn quota_info_roundtrip() {
    let qi = QuotaInfo {
        storage_used_bytes: 500,
        storage_quota_bytes: 1000,
        usage_percent: 50.0,
    };
    let json = serde_json::to_string(&qi).unwrap();
    let de: QuotaInfo = serde_json::from_str(&json).unwrap();
    assert!((de.usage_percent - 50.0).abs() < f64::EPSILON);
}

#[test]
fn shared_entity_roundtrip() {
    let se = SharedEntity {
        entity_id: "e-1".into(),
        entity_type: "note".into(),
        entity_name: None,
        owner_user_id: 5,
        workspace_id: "ws-1".into(),
        permission: SharePermission::Write,
    };
    let json = serde_json::to_string(&se).unwrap();
    let de: SharedEntity = serde_json::from_str(&json).unwrap();
    assert_eq!(de.permission, SharePermission::Write);
    assert!(de.entity_name.is_none());
}

#[test]
fn share_permission_serde_lowercase() {
    let json = serde_json::to_string(&SharePermission::Read).unwrap();
    assert_eq!(json, "\"read\"");
    let json = serde_json::to_string(&SharePermission::Write).unwrap();
    assert_eq!(json, "\"write\"");
}

#[test]
fn share_status_serde_lowercase() {
    let json = serde_json::to_string(&ShareStatus::Pending).unwrap();
    assert_eq!(json, "\"pending\"");
    let json = serde_json::to_string(&ShareStatus::Accepted).unwrap();
    assert_eq!(json, "\"accepted\"");
    let json = serde_json::to_string(&ShareStatus::Revoked).unwrap();
    assert_eq!(json, "\"revoked\"");
}

#[test]
fn share_permission_equality() {
    assert_eq!(SharePermission::Read, SharePermission::Read);
    assert_ne!(SharePermission::Read, SharePermission::Write);
}

#[test]
fn share_status_equality() {
    assert_eq!(ShareStatus::Pending, ShareStatus::Pending);
    assert_ne!(ShareStatus::Pending, ShareStatus::Accepted);
}

#[test]
fn blob_meta_roundtrip() {
    let bm = BlobMeta {
        blob_id: "b-1".into(),
        entity_id: Some("e-1".into()),
        s3_key: "blob.enc".into(),
        size_bytes: 256,
        content_hash: Some("abc123".into()),
    };
    let json = serde_json::to_string(&bm).unwrap();
    let de: BlobMeta = serde_json::from_str(&json).unwrap();
    assert_eq!(de.blob_id, "b-1");
}

#[test]
fn blob_meta_optional_fields() {
    let bm = BlobMeta {
        blob_id: "b-1".into(),
        entity_id: None,
        s3_key: "blob.enc".into(),
        size_bytes: 0,
        content_hash: None,
    };
    let json = serde_json::to_string(&bm).unwrap();
    let de: BlobMeta = serde_json::from_str(&json).unwrap();
    assert!(de.entity_id.is_none());
    assert!(de.content_hash.is_none());
}

#[test]
fn auth_tokens_roundtrip() {
    let tokens = AuthTokens {
        access_token: "at".into(),
        refresh_token: "rt".into(),
        user_id: 1,
        email: "test@example.com".into(),
    };
    let json = serde_json::to_string(&tokens).unwrap();
    let de: AuthTokens = serde_json::from_str(&json).unwrap();
    assert_eq!(de.email, "test@example.com");
}

#[test]
fn device_info_roundtrip() {
    let di = DeviceInfo {
        device_id: "dev-1".into(),
        device_name: Some("MacBook".into()),
        platform: Some("macos".into()),
        last_seen_at: Some(Utc::now()),
    };
    let json = serde_json::to_string(&di).unwrap();
    let de: DeviceInfo = serde_json::from_str(&json).unwrap();
    assert_eq!(de.device_id, "dev-1");
}

#[test]
fn device_info_all_optional_null() {
    let di = DeviceInfo {
        device_id: "dev-1".into(),
        device_name: None,
        platform: None,
        last_seen_at: None,
    };
    let json = serde_json::to_string(&di).unwrap();
    let de: DeviceInfo = serde_json::from_str(&json).unwrap();
    assert!(de.device_name.is_none());
    assert!(de.platform.is_none());
    assert!(de.last_seen_at.is_none());
}

#[test]
fn cloud_sync_status_roundtrip() {
    let status = CloudSyncStatus {
        is_syncing: true,
        is_authenticated: true,
        active_workspace: Some("ws-1".into()),
        pending_upload_count: 5,
        last_sync_at: Some(Utc::now()),
        connected_devices: 2,
    };
    let json = serde_json::to_string(&status).unwrap();
    let de: CloudSyncStatus = serde_json::from_str(&json).unwrap();
    assert!(de.is_syncing);
    assert_eq!(de.pending_upload_count, 5);
}

#[test]
fn advance_cursor_request_roundtrip() {
    let req = AdvanceCursorRequest {
        workspace_id: "ws-1".into(),
        device_id: "dev-1".into(),
        entity_id: "e-1".into(),
        cursor_position: 10,
        batch_key: "batch.enc".into(),
        size_bytes: 1024,
        event_count: 5,
    };
    let json = serde_json::to_string(&req).unwrap();
    let de: AdvanceCursorRequest = serde_json::from_str(&json).unwrap();
    assert_eq!(de.cursor_position, 10);
}

#[test]
fn create_share_request_roundtrip() {
    let req = CreateShareRequest {
        entity_id: "e-1".into(),
        entity_type: "note".into(),
        entity_name: Some("My Note".into()),
        workspace_id: "ws-1".into(),
        recipient_email: "bob@example.com".into(),
        permission: SharePermission::Write,
    };
    let json = serde_json::to_string(&req).unwrap();
    let de: CreateShareRequest = serde_json::from_str(&json).unwrap();
    assert_eq!(de.permission, SharePermission::Write);
}

#[test]
fn register_blob_request_roundtrip() {
    let req = RegisterBlobRequest {
        workspace_id: "ws-1".into(),
        blob_id: "b-1".into(),
        entity_id: Some("e-1".into()),
        s3_key: "blob.enc".into(),
        size_bytes: 256,
        content_hash: Some("abc".into()),
    };
    let json = serde_json::to_string(&req).unwrap();
    let de: RegisterBlobRequest = serde_json::from_str(&json).unwrap();
    assert_eq!(de.blob_id, "b-1");
}

#[test]
fn pending_changes_roundtrip() {
    let pc = PendingChanges {
        entities: vec![PendingEntity {
            entity_id: "e-1".into(),
            current_cursor: 10,
            device_cursor: 5,
            batches: vec![],
        }],
    };
    let json = serde_json::to_string(&pc).unwrap();
    let de: PendingChanges = serde_json::from_str(&json).unwrap();
    assert_eq!(de.entities.len(), 1);
    assert_eq!(de.entities[0].current_cursor, 10);
}
