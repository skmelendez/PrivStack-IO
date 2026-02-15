//! Share envelope orchestration.
//!
//! Wraps privstack-crypto envelope primitives to provide high-level
//! share key management: creating envelopes for recipients, opening
//! received envelopes, and coordinating DEK rotation on revocation.

use crate::api_client::CloudApiClient;
use crate::error::{CloudError, CloudResult};
use crypto_box::PublicKey;
use privstack_crypto::envelope::{self as crypto_env, CloudKeyPair};
use privstack_crypto::SealedEnvelope;
use std::sync::Arc;
use tracing::debug;

/// Manages envelope encryption for entity sharing.
pub struct EnvelopeManager {
    api: Arc<CloudApiClient>,
    keypair: Option<CloudKeyPair>,
}

impl EnvelopeManager {
    pub fn new(api: Arc<CloudApiClient>) -> Self {
        Self { api, keypair: None }
    }

    /// Sets the local keypair (after passphrase unlock or mnemonic recovery).
    pub fn set_keypair(&mut self, keypair: CloudKeyPair) {
        self.keypair = Some(keypair);
    }

    /// Returns whether a keypair is loaded.
    pub fn has_keypair(&self) -> bool {
        self.keypair.is_some()
    }

    /// Returns the public key bytes, if a keypair is loaded.
    pub fn public_key_bytes(&self) -> Option<[u8; 32]> {
        self.keypair.as_ref().map(|kp| kp.public_bytes())
    }

    /// Seals a DEK for a recipient by their user ID.
    ///
    /// Fetches the recipient's public key from the API, then encrypts
    /// the DEK with an ephemeral X25519 keypair.
    pub async fn seal_dek_for_user(
        &self,
        dek: &[u8],
        recipient_user_id: i64,
    ) -> CloudResult<SealedEnvelope> {
        let pk_bytes = self.api.get_public_key(recipient_user_id).await?;
        let recipient_pk = PublicKey::from(pk_bytes);

        crypto_env::seal_dek(dek, &recipient_pk)
            .map_err(|e| CloudError::Envelope(e.to_string()))
    }

    /// Opens a sealed DEK envelope using the local keypair.
    pub fn open_dek(&self, envelope: &SealedEnvelope) -> CloudResult<Vec<u8>> {
        let kp = self
            .keypair
            .as_ref()
            .ok_or(CloudError::Envelope("no keypair loaded".to_string()))?;

        crypto_env::open_dek(envelope, &kp.secret)
            .map_err(|e| CloudError::Envelope(e.to_string()))
    }

    /// Creates and stores an envelope for a specific entity and recipient.
    pub async fn create_and_store_envelope(
        &self,
        entity_id: &str,
        dek: &[u8],
        recipient_user_id: i64,
    ) -> CloudResult<()> {
        let envelope = self.seal_dek_for_user(dek, recipient_user_id).await?;
        self.api
            .store_share_key(entity_id, recipient_user_id, &envelope)
            .await?;
        debug!(
            "stored share key envelope for entity {entity_id} -> user {recipient_user_id}"
        );
        Ok(())
    }

    /// Retrieves and opens a DEK envelope for an entity shared with us.
    pub async fn retrieve_and_open_dek(&self, entity_id: &str) -> CloudResult<Vec<u8>> {
        let envelope = self.api.get_share_key(entity_id).await?;
        self.open_dek(&envelope)
    }
}
