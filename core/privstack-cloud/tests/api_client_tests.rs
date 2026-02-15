use privstack_cloud::api_client::CloudApiClient;
use privstack_cloud::config::CloudConfig;
use privstack_cloud::error::CloudError;
use privstack_cloud::types::*;
use wiremock::matchers::{method, path};
use wiremock::{Mock, MockServer, ResponseTemplate};

async fn setup(server: &MockServer) -> CloudApiClient {
    let config = CloudConfig {
        api_base_url: server.uri(),
        s3_bucket: "test-bucket".into(),
        s3_region: "us-east-1".into(),
        s3_endpoint_override: None,
        credential_refresh_margin_secs: 60,
        poll_interval_secs: 5,
    };
    CloudApiClient::new(config)
}

fn auth_response() -> serde_json::Value {
    serde_json::json!({
        "access_token": "at-new",
        "refresh_token": "rt-new",
        "user": { "id": 1, "email": "test@example.com" }
    })
}

// --- Auth State ---

#[tokio::test]
async fn not_authenticated_initially() {
    let server = MockServer::start().await;
    let client = setup(&server).await;
    assert!(!client.is_authenticated().await);
}

#[tokio::test]
async fn set_tokens_makes_authenticated() {
    let server = MockServer::start().await;
    let client = setup(&server).await;
    client
        .set_tokens("at".into(), "rt".into(), 1)
        .await;
    assert!(client.is_authenticated().await);
}

#[tokio::test]
async fn user_id_after_set_tokens() {
    let server = MockServer::start().await;
    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 42).await;
    assert_eq!(client.user_id().await, Some(42));
}

#[tokio::test]
async fn logout_clears_auth() {
    let server = MockServer::start().await;
    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.logout().await;
    assert!(!client.is_authenticated().await);
    assert_eq!(client.user_id().await, None);
}

#[tokio::test]
async fn authenticate_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/auth/login"))
        .respond_with(ResponseTemplate::new(200).set_body_json(auth_response()))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    let tokens = client.authenticate("test@example.com", "password").await.unwrap();
    assert_eq!(tokens.user_id, 1);
    assert_eq!(tokens.email, "test@example.com");
    assert!(client.is_authenticated().await);
}

#[tokio::test]
async fn authenticate_bad_credentials() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/auth/login"))
        .respond_with(ResponseTemplate::new(401).set_body_json(serde_json::json!({"error": "Invalid credentials"})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    let result = client.authenticate("bad@example.com", "wrong").await;
    assert!(result.is_err());
    assert!(matches!(result.unwrap_err(), CloudError::AuthFailed(_)));
}

// --- Workspaces ---

#[tokio::test]
async fn register_workspace_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/workspaces"))
        .respond_with(ResponseTemplate::new(201).set_body_json(serde_json::json!({
            "id": 1,
            "user_id": 1,
            "workspace_id": "ws-uuid",
            "workspace_name": "Test",
            "s3_prefix": "users/1/workspaces/ws-uuid",
            "storage_used_bytes": 0,
            "storage_quota_bytes": 10737418240u64,
            "created_at": "2025-01-01T00:00:00Z"
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let ws = client.register_workspace("ws-uuid", "Test").await.unwrap();
    assert_eq!(ws.workspace_id, "ws-uuid");
}

#[tokio::test]
async fn list_workspaces_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/cloud/workspaces"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "workspaces": [{
                "id": 1,
                "user_id": 1,
                "workspace_id": "ws-uuid",
                "workspace_name": "Test",
                "s3_prefix": "prefix",
                "storage_used_bytes": 0,
                "storage_quota_bytes": 10737418240u64,
                "created_at": "2025-01-01T00:00:00Z"
            }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let workspaces = client.list_workspaces().await.unwrap();
    assert_eq!(workspaces.len(), 1);
}

#[tokio::test]
async fn delete_workspace_success() {
    let server = MockServer::start().await;
    Mock::given(method("DELETE"))
        .and(path("/api/cloud/workspaces/ws-uuid"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"success": true})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.delete_workspace("ws-uuid").await.unwrap();
}

// --- Auth Retry on 401 ---

#[tokio::test]
async fn auth_retry_on_401_get() {
    let server = MockServer::start().await;

    // First call: 401, second call (after refresh): 200
    Mock::given(method("GET"))
        .and(path("/api/cloud/workspaces"))
        .respond_with(ResponseTemplate::new(401))
        .up_to_n_times(1)
        .mount(&server)
        .await;

    Mock::given(method("POST"))
        .and(path("/api/auth/refresh"))
        .respond_with(ResponseTemplate::new(200).set_body_json(auth_response()))
        .mount(&server)
        .await;

    Mock::given(method("GET"))
        .and(path("/api/cloud/workspaces"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"workspaces": []})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("expired-at".into(), "rt".into(), 1).await;
    let result = client.list_workspaces().await.unwrap();
    assert_eq!(result.len(), 0);
}

// --- STS Credentials ---

#[tokio::test]
async fn get_sts_credentials_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/credentials"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "access_key_id": "AKIA_TEST",
            "secret_access_key": "secret",
            "session_token": "token",
            "expires_at": "2099-01-01T00:00:00Z",
            "bucket": "test-bucket",
            "region": "us-east-1"
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let creds = client.get_sts_credentials("ws-uuid").await.unwrap();
    assert_eq!(creds.access_key_id, "AKIA_TEST");
}

// --- Cursor/Lock/Quota ---

#[tokio::test]
async fn advance_cursor_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/cursors/advance"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"cursor_position": 10})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let req = AdvanceCursorRequest {
        workspace_id: "ws".into(),
        device_id: "dev".into(),
        entity_id: "ent".into(),
        cursor_position: 10,
        batch_key: "batch.enc".into(),
        size_bytes: 1024,
        event_count: 5,
    };
    client.advance_cursor(&req).await.unwrap();
}

#[tokio::test]
async fn get_pending_changes_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/cloud/cursors/pending"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "entities": [{ "entity_id": "e-1", "current_cursor": 10, "device_cursor": 5, "batches": [] }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let pending = client.get_pending_changes("ws", "dev").await.unwrap();
    assert_eq!(pending.entities.len(), 1);
}

#[tokio::test]
async fn acquire_lock_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/locks/acquire"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"acquired": true})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.acquire_lock("ent-1", "ws-1", "dev-1").await.unwrap();
}

#[tokio::test]
async fn acquire_lock_conflict() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/locks/acquire"))
        .respond_with(ResponseTemplate::new(409).set_body_json(serde_json::json!({"error": "Lock held"})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let result = client.acquire_lock("ent-1", "ws-1", "dev-1").await;
    assert!(matches!(result.unwrap_err(), CloudError::LockContention(_)));
}

#[tokio::test]
async fn release_lock_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/locks/release"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"success": true})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.release_lock("ent-1", "ws-1", "dev-1").await.unwrap();
}

#[tokio::test]
async fn get_quota_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/cloud/quota"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "storage_used_bytes": 500,
            "storage_quota_bytes": 1000,
            "usage_percent": 50.0
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let quota = client.get_quota("ws-1").await.unwrap();
    assert_eq!(quota.storage_used_bytes, 500);
}

// --- Sharing ---

#[tokio::test]
async fn create_share_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/share/create"))
        .respond_with(ResponseTemplate::new(201).set_body_json(serde_json::json!({
            "share_id": 1,
            "entity_id": "e-1",
            "entity_type": "note",
            "entity_name": "My Note",
            "recipient_email": "bob@example.com",
            "permission": "read",
            "status": "pending",
            "created_at": "2025-01-01T00:00:00Z",
            "accepted_at": null
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let req = CreateShareRequest {
        entity_id: "e-1".into(),
        entity_type: "note".into(),
        entity_name: Some("My Note".into()),
        workspace_id: "ws-1".into(),
        recipient_email: "bob@example.com".into(),
        permission: SharePermission::Read,
    };
    let share = client.create_share(&req).await.unwrap();
    assert_eq!(share.entity_id, "e-1");
}

#[tokio::test]
async fn accept_share_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/share/accept"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"ok": true})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.accept_share("token-abc").await.unwrap();
}

#[tokio::test]
async fn revoke_share_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/share/revoke"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"revoked": true})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.revoke_share("e-1", "bob@example.com").await.unwrap();
}

#[tokio::test]
async fn get_entity_shares_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/share/entity/e-1"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "shares": [{
                "share_id": 1,
                "entity_id": "e-1",
                "entity_type": "note",
                "entity_name": null,
                "recipient_email": "bob@example.com",
                "permission": "read",
                "status": "accepted",
                "created_at": "2025-01-01T00:00:00Z",
                "accepted_at": "2025-01-02T00:00:00Z"
            }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let shares = client.get_entity_shares("e-1").await.unwrap();
    assert_eq!(shares.len(), 1);
}

#[tokio::test]
async fn get_shared_with_me_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/share/received"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "shares": [{
                "entity_id": "e-1",
                "entity_type": "note",
                "entity_name": null,
                "owner_user_id": 5,
                "workspace_id": "ws-1",
                "permission": "read"
            }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let shared = client.get_shared_with_me().await.unwrap();
    assert_eq!(shared.len(), 1);
}

// --- Public Key ---

#[tokio::test]
async fn get_public_key_valid() {
    let server = MockServer::start().await;
    use base64::{engine::general_purpose::STANDARD, Engine};
    let key_bytes = [42u8; 32];
    let encoded = STANDARD.encode(key_bytes);

    Mock::given(method("GET"))
        .and(path("/api/cloud/keys/public/5"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "public_key": encoded
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let key = client.get_public_key(5).await.unwrap();
    assert_eq!(key, key_bytes);
}

#[tokio::test]
async fn get_public_key_invalid_length() {
    let server = MockServer::start().await;
    use base64::{engine::general_purpose::STANDARD, Engine};
    let short = STANDARD.encode([1u8; 16]); // 16 bytes, not 32

    Mock::given(method("GET"))
        .and(path("/api/cloud/keys/public/5"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "public_key": short
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let result = client.get_public_key(5).await;
    assert!(result.is_err());
    assert!(result.unwrap_err().to_string().contains("invalid public key length"));
}

// --- Devices ---

#[tokio::test]
async fn register_device_success() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api/cloud/devices/register"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"device_id": "dev-1"})))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    client.register_device("MacBook", "macos", "dev-1").await.unwrap();
}

#[tokio::test]
async fn list_devices_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/cloud/devices"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "devices": [{ "device_id": "dev-1", "device_name": "MacBook", "platform": "macos", "last_seen_at": null }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let devices = client.list_devices().await.unwrap();
    assert_eq!(devices.len(), 1);
}

// --- Blobs ---

#[tokio::test]
async fn get_entity_blobs_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/cloud/blobs/e-1"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "blobs": [{ "blob_id": "b-1", "entity_id": "e-1", "s3_key": "blob.enc", "size_bytes": 256, "content_hash": null }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let blobs = client.get_entity_blobs("e-1").await.unwrap();
    assert_eq!(blobs.len(), 1);
}

// --- Batches ---

#[tokio::test]
async fn get_batches_success() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api/cloud/batches/e-1"))
        .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
            "batches": [{ "s3_key": "batch.enc", "cursor_start": 0, "cursor_end": 10, "size_bytes": 512, "event_count": 5, "is_snapshot": false }]
        })))
        .mount(&server)
        .await;

    let client = setup(&server).await;
    client.set_tokens("at".into(), "rt".into(), 1).await;
    let batches = client.get_batches("ws-1", "e-1", 0).await.unwrap();
    assert_eq!(batches.len(), 1);
}

// --- Auth required ---

#[tokio::test]
async fn unauthenticated_request_returns_error() {
    let server = MockServer::start().await;
    let client = setup(&server).await;
    // No tokens set
    let result = client.list_workspaces().await;
    assert!(matches!(result.unwrap_err(), CloudError::AuthRequired));
}
