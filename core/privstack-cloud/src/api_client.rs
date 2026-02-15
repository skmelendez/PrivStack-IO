//! HTTP client for the Express control plane API.
//!
//! Handles JWT authentication, token refresh on 401, and all cloud/share
//! API endpoints. Uses reqwest with JSON serialization.

use crate::config::CloudConfig;
use crate::error::{CloudError, CloudResult};
use crate::types::*;
use privstack_crypto::SealedEnvelope;
use reqwest::Client;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tokio::sync::RwLock;
use tracing::debug;

/// State shared across API client clones.
struct AuthState {
    access_token: Option<String>,
    refresh_token: Option<String>,
    user_id: Option<i64>,
    /// Monotonically increasing counter bumped on every successful refresh.
    /// Used to detect when a concurrent refresh has already updated tokens.
    refresh_generation: u64,
}

/// HTTP client for the PrivStack cloud control plane.
pub struct CloudApiClient {
    client: Client,
    config: CloudConfig,
    auth: Arc<RwLock<AuthState>>,
    /// Serializes refresh operations to prevent rotation race conditions.
    /// Without this, concurrent 401s all read the same old refresh token;
    /// the server rotates on the first call, and subsequent calls fail.
    refresh_lock: Arc<tokio::sync::Mutex<()>>,
}

#[derive(Deserialize)]
struct TokenResponse {
    access_token: String,
    refresh_token: String,
    user: TokenUser,
}

#[derive(Deserialize)]
struct TokenUser {
    id: i64,
    email: String,
}

impl CloudApiClient {
    pub fn new(config: CloudConfig) -> Self {
        let client = Client::builder()
            .timeout(std::time::Duration::from_secs(30))
            .build()
            .expect("failed to build HTTP client");

        Self {
            client,
            config,
            auth: Arc::new(RwLock::new(AuthState {
                access_token: None,
                refresh_token: None,
                user_id: None,
                refresh_generation: 0,
            })),
            refresh_lock: Arc::new(tokio::sync::Mutex::new(())),
        }
    }

    /// Sets auth tokens directly (for restoring saved session).
    pub async fn set_tokens(&self, access_token: String, refresh_token: String, user_id: i64) {
        let mut auth = self.auth.write().await;
        auth.access_token = Some(access_token);
        auth.refresh_token = Some(refresh_token);
        auth.user_id = Some(user_id);
    }

    pub async fn is_authenticated(&self) -> bool {
        self.auth.read().await.access_token.is_some()
    }

    pub async fn user_id(&self) -> Option<i64> {
        self.auth.read().await.user_id
    }

    pub async fn logout(&self) {
        let mut auth = self.auth.write().await;
        auth.access_token = None;
        auth.refresh_token = None;
        auth.user_id = None;
    }

    /// Returns current auth tokens for persistence.
    pub async fn get_current_tokens(&self) -> Option<AuthTokens> {
        let auth = self.auth.read().await;
        Some(AuthTokens {
            access_token: auth.access_token.clone()?,
            refresh_token: auth.refresh_token.clone()?,
            user_id: auth.user_id?,
            email: String::new(),
        })
    }

    // ── Auth ──

    pub async fn authenticate(&self, email: &str, password: &str) -> CloudResult<AuthTokens> {
        let url = format!("{}/api/auth/login", self.config.api_base_url);
        let resp: TokenResponse = self
            .client
            .post(&url)
            .json(&serde_json::json!({ "email": email, "password": password }))
            .send()
            .await?
            .error_for_status()
            .map_err(|e| CloudError::AuthFailed(e.to_string()))?
            .json()
            .await?;

        let tokens = AuthTokens {
            access_token: resp.access_token.clone(),
            refresh_token: resp.refresh_token.clone(),
            user_id: resp.user.id,
            email: resp.user.email,
        };

        self.set_tokens(resp.access_token, resp.refresh_token, resp.user.id)
            .await;
        Ok(tokens)
    }

    pub async fn refresh_access_token(&self) -> CloudResult<String> {
        // Capture the generation before acquiring the lock so we can
        // detect if a concurrent refresh already completed.
        let pre_gen = self.auth.read().await.refresh_generation;

        // Serialize all refresh operations — only one HTTP refresh at a time.
        let _guard = self.refresh_lock.lock().await;

        // Double-check: if the generation advanced while we waited,
        // a concurrent refresh already succeeded. Use its token.
        {
            let auth = self.auth.read().await;
            if auth.refresh_generation > pre_gen {
                return auth
                    .access_token
                    .clone()
                    .ok_or(CloudError::AuthRequired);
            }
        }

        let refresh_token = {
            let auth = self.auth.read().await;
            auth.refresh_token
                .clone()
                .ok_or(CloudError::AuthRequired)?
        };

        let url = format!("{}/api/auth/refresh", self.config.api_base_url);
        let resp = self
            .client
            .post(&url)
            .json(&serde_json::json!({ "refresh_token": refresh_token }))
            .send()
            .await?;

        if resp.status() == reqwest::StatusCode::UNAUTHORIZED
            || resp.status() == reqwest::StatusCode::FORBIDDEN
        {
            // Refresh token is expired/revoked — clear stale session
            self.logout().await;
            return Err(CloudError::AuthFailed(
                "token refresh failed: session expired, re-authentication required".to_string(),
            ));
        }

        let resp: TokenResponse = resp
            .error_for_status()
            .map_err(|e| CloudError::AuthFailed(format!("token refresh failed: {e}")))?
            .json()
            .await?;

        let mut auth = self.auth.write().await;
        auth.access_token = Some(resp.access_token.clone());
        auth.refresh_token = Some(resp.refresh_token);
        auth.user_id = Some(resp.user.id);
        auth.refresh_generation += 1;

        Ok(resp.access_token)
    }

    /// Makes an authenticated GET request, retrying once on 401.
    async fn auth_get(&self, path: &str) -> CloudResult<reqwest::Response> {
        let url = format!("{}{}", self.config.api_base_url, path);
        let token = self.get_token().await?;

        let resp = self
            .client
            .get(&url)
            .bearer_auth(&token)
            .send()
            .await?;

        if resp.status() == reqwest::StatusCode::UNAUTHORIZED {
            debug!("401 on GET {path}, refreshing token");
            let new_token = self.refresh_access_token().await?;
            return Ok(self.client.get(&url).bearer_auth(&new_token).send().await?);
        }

        Ok(resp)
    }

    /// Makes an authenticated POST request, retrying once on 401.
    async fn auth_post(
        &self,
        path: &str,
        body: &impl Serialize,
    ) -> CloudResult<reqwest::Response> {
        let url = format!("{}{}", self.config.api_base_url, path);
        let token = self.get_token().await?;

        let resp = self
            .client
            .post(&url)
            .bearer_auth(&token)
            .json(body)
            .send()
            .await?;

        if resp.status() == reqwest::StatusCode::UNAUTHORIZED {
            debug!("401 on POST {path}, refreshing token");
            let new_token = self.refresh_access_token().await?;
            return Ok(self
                .client
                .post(&url)
                .bearer_auth(&new_token)
                .json(body)
                .send()
                .await?);
        }

        Ok(resp)
    }

    /// Makes an authenticated DELETE request, retrying once on 401.
    async fn auth_delete(&self, path: &str) -> CloudResult<reqwest::Response> {
        let url = format!("{}{}", self.config.api_base_url, path);
        let token = self.get_token().await?;

        let resp = self
            .client
            .delete(&url)
            .bearer_auth(&token)
            .send()
            .await?;

        if resp.status() == reqwest::StatusCode::UNAUTHORIZED {
            debug!("401 on DELETE {path}, refreshing token");
            let new_token = self.refresh_access_token().await?;
            return Ok(self
                .client
                .delete(&url)
                .bearer_auth(&new_token)
                .send()
                .await?);
        }

        Ok(resp)
    }

    async fn get_token(&self) -> CloudResult<String> {
        self.auth
            .read()
            .await
            .access_token
            .clone()
            .ok_or(CloudError::AuthRequired)
    }

    // ── Workspaces ──

    pub async fn register_workspace(
        &self,
        workspace_id: &str,
        name: &str,
    ) -> CloudResult<CloudWorkspace> {
        let resp = self
            .auth_post(
                "/api/cloud/workspaces",
                &serde_json::json!({ "workspace_id": workspace_id, "workspace_name": name }),
            )
            .await?;

        let status = resp.status();

        // 409 Conflict = workspace already registered — fetch existing instead of failing
        if status == reqwest::StatusCode::CONFLICT {
            debug!("Workspace {workspace_id} already registered, fetching existing");
            let existing = self.list_workspaces().await?;
            return existing
                .into_iter()
                .find(|ws| ws.workspace_id == workspace_id)
                .ok_or_else(|| CloudError::Api(
                    "workspace conflict but not found in list".to_string(),
                ));
        }

        let resp = resp
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        Ok(resp.json().await?)
    }

    pub async fn list_workspaces(&self) -> CloudResult<Vec<CloudWorkspace>> {
        let resp = self
            .auth_get("/api/cloud/workspaces")
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            workspaces: Vec<CloudWorkspace>,
        }
        let data: Resp = resp.json().await?;
        Ok(data.workspaces)
    }

    pub async fn delete_workspace(&self, workspace_id: &str) -> CloudResult<()> {
        self.auth_delete(&format!("/api/cloud/workspaces/{workspace_id}"))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    // ── STS Credentials ──

    pub async fn get_sts_credentials(&self, workspace_id: &str) -> CloudResult<StsCredentials> {
        let resp = self
            .auth_post(
                "/api/cloud/credentials",
                &serde_json::json!({ "workspace_id": workspace_id }),
            )
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        Ok(resp.json().await?)
    }

    // ── Cursors ──

    pub async fn advance_cursor(&self, req: &AdvanceCursorRequest) -> CloudResult<()> {
        self.auth_post("/api/cloud/cursors/advance", req)
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn get_pending_changes(
        &self,
        workspace_id: &str,
        device_id: &str,
    ) -> CloudResult<PendingChanges> {
        let resp = self
            .auth_get(&format!(
                "/api/cloud/cursors/pending?workspace_id={workspace_id}&device_id={device_id}"
            ))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        Ok(resp.json().await?)
    }

    // ── Locks ──

    pub async fn acquire_lock(&self, entity_id: &str, workspace_id: &str, device_id: &str) -> CloudResult<()> {
        let resp = self
            .auth_post(
                "/api/cloud/locks/acquire",
                &serde_json::json!({
                    "entity_id": entity_id,
                    "workspace_id": workspace_id,
                    "device_id": device_id,
                }),
            )
            .await?;

        if resp.status() == reqwest::StatusCode::CONFLICT {
            return Err(CloudError::LockContention(format!(
                "entity {entity_id} is locked by another device"
            )));
        }
        resp.error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn release_lock(&self, entity_id: &str, workspace_id: &str, device_id: &str) -> CloudResult<()> {
        self.auth_post(
            "/api/cloud/locks/release",
            &serde_json::json!({
                "entity_id": entity_id,
                "workspace_id": workspace_id,
                "device_id": device_id,
            }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    // ── Quota ──

    pub async fn get_quota(&self, workspace_id: &str) -> CloudResult<QuotaInfo> {
        let resp = self
            .auth_get(&format!("/api/cloud/quota?workspace_id={workspace_id}"))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        Ok(resp.json().await?)
    }

    // ── Rate Limits ──

    pub async fn get_rate_limits(&self) -> CloudResult<RateLimitConfig> {
        let resp = self
            .auth_get("/api/cloud/rate-limits")
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(resp.json().await?)
    }

    // ── Sharing ──

    pub async fn create_share(&self, req: &CreateShareRequest) -> CloudResult<ShareInfo> {
        let resp = self
            .auth_post("/api/share/create", req)
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        Ok(resp.json().await?)
    }

    pub async fn accept_share(&self, token: &str) -> CloudResult<()> {
        self.auth_post(
            "/api/share/accept",
            &serde_json::json!({ "invitation_token": token }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn revoke_share(
        &self,
        entity_id: &str,
        recipient_email: &str,
    ) -> CloudResult<()> {
        self.auth_post(
            "/api/share/revoke",
            &serde_json::json!({
                "entity_id": entity_id,
                "recipient_email": recipient_email,
            }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn get_share_key(&self, entity_id: &str) -> CloudResult<SealedEnvelope> {
        let resp = self
            .auth_get(&format!("/api/share/keys/{entity_id}"))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        Ok(resp.json().await?)
    }

    pub async fn store_share_key(
        &self,
        entity_id: &str,
        recipient_id: i64,
        envelope: &SealedEnvelope,
    ) -> CloudResult<()> {
        self.auth_post(
            "/api/share/keys/store",
            &serde_json::json!({
                "entity_id": entity_id,
                "recipient_user_id": recipient_id,
                "encrypted_dek": serde_json::to_string(envelope)?,
            }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn get_entity_shares(&self, entity_id: &str) -> CloudResult<Vec<ShareInfo>> {
        let resp = self
            .auth_get(&format!("/api/share/entity/{entity_id}"))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            shares: Vec<ShareInfo>,
        }
        let data: Resp = resp.json().await?;
        Ok(data.shares)
    }

    pub async fn get_shared_with_me(&self) -> CloudResult<Vec<SharedEntity>> {
        let resp = self
            .auth_get("/api/share/received")
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            shares: Vec<SharedEntity>,
        }
        let data: Resp = resp.json().await?;
        Ok(data.shares)
    }

    pub async fn get_public_key(&self, user_id: i64) -> CloudResult<[u8; 32]> {
        let resp = self
            .auth_get(&format!("/api/cloud/keys/public/{user_id}"))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            public_key: String,
        }
        let data: Resp = resp.json().await?;

        use base64::{engine::general_purpose::STANDARD, Engine};
        let bytes = STANDARD
            .decode(&data.public_key)
            .map_err(|e| CloudError::Api(format!("invalid public key encoding: {e}")))?;

        if bytes.len() != 32 {
            return Err(CloudError::Api(format!(
                "invalid public key length: expected 32, got {}",
                bytes.len()
            )));
        }

        let mut key = [0u8; 32];
        key.copy_from_slice(&bytes);
        Ok(key)
    }

    pub async fn upload_public_key(&self, key: &[u8; 32]) -> CloudResult<()> {
        use base64::{engine::general_purpose::STANDARD, Engine};
        use sha2::{Digest, Sha256};

        let encoded = STANDARD.encode(key);
        let fingerprint = hex::encode(Sha256::digest(key));

        self.auth_post(
            "/api/cloud/keys/public",
            &serde_json::json!({
                "public_key": encoded,
                "fingerprint": fingerprint,
            }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    // ── Devices ──

    pub async fn register_device(&self, name: &str, platform: &str, device_id: &str) -> CloudResult<()> {
        self.auth_post(
            "/api/cloud/devices/register",
            &serde_json::json!({
                "device_id": device_id,
                "device_name": name,
                "platform": platform,
            }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn list_devices(&self) -> CloudResult<Vec<DeviceInfo>> {
        let resp = self
            .auth_get("/api/cloud/devices")
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            devices: Vec<DeviceInfo>,
        }
        let data: Resp = resp.json().await?;
        Ok(data.devices)
    }

    // ── Compaction ──

    pub async fn notify_snapshot(
        &self,
        entity_id: &str,
        workspace_id: &str,
        s3_key: &str,
        cursor: i64,
    ) -> CloudResult<()> {
        self.auth_post(
            "/api/cloud/compaction/request",
            &serde_json::json!({
                "entity_id": entity_id,
                "workspace_id": workspace_id,
                "snapshot_s3_key": s3_key,
                "cursor_position": cursor,
            }),
        )
        .await?
        .error_for_status()
        .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    // ── Blobs ──

    pub async fn register_blob(&self, req: &RegisterBlobRequest) -> CloudResult<()> {
        self.auth_post("/api/cloud/blobs/register", req)
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;
        Ok(())
    }

    pub async fn get_entity_blobs(&self, entity_id: &str) -> CloudResult<Vec<BlobMeta>> {
        let resp = self
            .auth_get(&format!("/api/cloud/blobs/{entity_id}"))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            blobs: Vec<BlobMeta>,
        }
        let data: Resp = resp.json().await?;
        Ok(data.blobs)
    }

    // ── Batches ──

    pub async fn get_batches(
        &self,
        workspace_id: &str,
        entity_id: &str,
        since_cursor: i64,
    ) -> CloudResult<Vec<BatchMeta>> {
        let resp = self
            .auth_get(&format!(
                "/api/cloud/batches/{entity_id}?workspace_id={workspace_id}&since_cursor={since_cursor}"
            ))
            .await?
            .error_for_status()
            .map_err(|e| CloudError::Api(e.to_string()))?;

        #[derive(Deserialize)]
        struct Resp {
            batches: Vec<BatchMeta>,
        }
        let data: Resp = resp.json().await?;
        Ok(data.batches)
    }
}
