//! STS credential lifecycle management with auto-refresh.
//!
//! Manages a single set of STS credentials per workspace, refreshing
//! them before expiry via the API client.

use crate::api_client::CloudApiClient;
use crate::error::CloudResult;
use crate::types::StsCredentials;
use std::sync::Arc;
use tokio::sync::RwLock;
use tracing::{debug, warn};

/// Manages STS credentials with auto-refresh.
pub struct CredentialManager {
    api: Arc<CloudApiClient>,
    workspace_id: String,
    credentials: Arc<RwLock<Option<StsCredentials>>>,
    refresh_margin_secs: i64,
}

impl CredentialManager {
    pub fn new(api: Arc<CloudApiClient>, workspace_id: String, refresh_margin_secs: i64) -> Self {
        Self {
            api,
            workspace_id,
            credentials: Arc::new(RwLock::new(None)),
            refresh_margin_secs,
        }
    }

    /// Gets valid credentials, refreshing if needed.
    pub async fn get_credentials(&self) -> CloudResult<StsCredentials> {
        // Fast path: check if existing credentials are still valid
        {
            let creds = self.credentials.read().await;
            if let Some(ref c) = *creds {
                if !c.expires_within_secs(self.refresh_margin_secs) {
                    return Ok(c.clone());
                }
                debug!("credentials expiring within {}s, refreshing", self.refresh_margin_secs);
            }
        }

        // Slow path: fetch new credentials
        self.refresh().await
    }

    /// Forces a credential refresh.
    pub async fn refresh(&self) -> CloudResult<StsCredentials> {
        let new_creds = self
            .api
            .get_sts_credentials(&self.workspace_id)
            .await
            .map_err(|e| {
                warn!("STS credential refresh failed: {e}");
                e
            })?;

        debug!(
            "refreshed STS credentials for workspace {}, expires at {}",
            self.workspace_id, new_creds.expires_at
        );

        let mut creds = self.credentials.write().await;
        *creds = Some(new_creds.clone());

        Ok(new_creds)
    }

    /// Clears cached credentials (on logout or workspace change).
    pub async fn clear(&self) {
        let mut creds = self.credentials.write().await;
        *creds = None;
    }

    /// Returns true if credentials are currently cached and valid.
    pub async fn has_valid_credentials(&self) -> bool {
        let creds = self.credentials.read().await;
        creds
            .as_ref()
            .is_some_and(|c| !c.expires_within_secs(self.refresh_margin_secs))
    }
}
