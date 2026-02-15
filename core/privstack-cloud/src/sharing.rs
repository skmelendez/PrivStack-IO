//! Share workflow: create, accept, revoke, DEK rotation, project cascade.
//!
//! Orchestrates the sharing lifecycle by coordinating between the API client
//! (share CRUD, limits) and the envelope manager (DEK encryption).

use crate::api_client::CloudApiClient;
use crate::envelope::EnvelopeManager;
use crate::error::CloudResult;
use crate::types::*;
use std::sync::Arc;
use tracing::info;

/// Orchestrates entity sharing workflows.
pub struct ShareManager {
    api: Arc<CloudApiClient>,
}

impl ShareManager {
    pub fn new(api: Arc<CloudApiClient>) -> Self {
        Self { api }
    }

    /// Creates a share and sends invitation email (via API).
    pub async fn create_share(&self, req: &CreateShareRequest) -> CloudResult<ShareInfo> {
        let share = self.api.create_share(req).await?;
        info!(
            "created share for entity {} with {}",
            req.entity_id, req.recipient_email
        );
        Ok(share)
    }

    /// Creates envelope-encrypted DEK for a share recipient.
    ///
    /// Call this after creating the share, once you have the entity DEK.
    pub async fn create_envelope_for_share(
        &self,
        envelope_mgr: &EnvelopeManager,
        entity_id: &str,
        entity_dek: &[u8],
        recipient_user_id: i64,
    ) -> CloudResult<()> {
        envelope_mgr
            .create_and_store_envelope(entity_id, entity_dek, recipient_user_id)
            .await
    }

    /// Accepts a share invitation by token.
    pub async fn accept_share(&self, token: &str) -> CloudResult<()> {
        self.api.accept_share(token).await?;
        info!("accepted share invitation");
        Ok(())
    }

    /// Revokes a share. The caller must then rotate the entity DEK.
    pub async fn revoke_share(
        &self,
        entity_id: &str,
        recipient_email: &str,
    ) -> CloudResult<()> {
        self.api.revoke_share(entity_id, recipient_email).await?;
        info!("revoked share for entity {entity_id} from {recipient_email}");
        Ok(())
    }

    /// Gets all shares for an entity owned by the current user.
    pub async fn get_entity_shares(&self, entity_id: &str) -> CloudResult<Vec<ShareInfo>> {
        self.api.get_entity_shares(entity_id).await
    }

    /// Gets entities shared with the current user.
    pub async fn get_shared_with_me(&self) -> CloudResult<Vec<SharedEntity>> {
        self.api.get_shared_with_me().await
    }
}
