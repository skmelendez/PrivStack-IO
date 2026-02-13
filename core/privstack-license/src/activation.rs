//! License activation and storage.
//!
//! Handles the one-time activation process and stores the activation
//! record locally for offline verification.

use crate::device::DeviceFingerprint;
use crate::error::{LicenseError, LicenseResult};
use crate::key::{LicenseKey, LicensePlan, LicenseStatus, GRACE_PERIOD_SECS};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::path::Path;

/// A stored activation record.
///
/// This is saved locally after successful activation and used for
/// offline license verification.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Activation {
    /// The license key.
    license_key: String,
    /// License plan.
    #[serde(alias = "license_type")]
    license_plan: LicensePlan,
    /// User email from the license payload.
    #[serde(default)]
    email: String,
    /// User ID from the license payload.
    #[serde(default)]
    sub: i64,
    /// When the license was activated.
    activated_at: DateTime<Utc>,
    /// Device fingerprint at activation time.
    device_fingerprint: DeviceFingerprint,
    /// Server-signed activation token (for verification).
    activation_token: String,
    /// When the activation expires (if applicable).
    expires_at: Option<DateTime<Utc>>,
    /// Number of remaining activations for this license.
    remaining_activations: Option<u32>,
}

impl Activation {
    /// Creates a new activation record.
    ///
    /// In production, this would be returned by the activation server.
    pub fn new(
        license_key: &LicenseKey,
        device_fingerprint: DeviceFingerprint,
        activation_token: String,
    ) -> Self {
        let payload = license_key.payload();
        let expires_at = license_key
            .expires_at_secs()
            .and_then(|ts| DateTime::from_timestamp(ts, 0));

        Self {
            license_key: license_key.raw().to_string(),
            license_plan: license_key.license_plan(),
            email: payload.email.clone(),
            sub: payload.sub,
            activated_at: Utc::now(),
            device_fingerprint,
            activation_token,
            expires_at,
            remaining_activations: None,
        }
    }

    /// Re-verifies the embedded license key and overwrites mutable fields
    /// (`license_plan`, `email`, `sub`, `expires_at`) from the verified payload.
    ///
    /// This prevents tamper attacks where a user edits `activation.json` to
    /// extend expiry or change plan while offline.
    ///
    /// Pass `None` for `verify_key` to use the embedded production public key.
    fn revalidate_from_key(&mut self, verify_key: Option<&[u8; 32]>) -> LicenseResult<()> {
        let parsed = match verify_key {
            Some(key) => LicenseKey::parse_with_key(&self.license_key, key)?,
            None => LicenseKey::parse(&self.license_key)?,
        };

        self.license_plan = parsed.license_plan();
        self.email = parsed.payload().email.clone();
        self.sub = parsed.payload().sub;
        self.expires_at = parsed.expires_at_secs().and_then(|ts| DateTime::from_timestamp(ts, 0));

        Ok(())
    }

    /// Returns the license key.
    #[must_use]
    pub fn license_key(&self) -> &str {
        &self.license_key
    }

    /// Returns the license plan.
    #[must_use]
    pub fn license_plan(&self) -> LicensePlan {
        self.license_plan
    }

    /// Returns the user email.
    #[must_use]
    pub fn email(&self) -> &str {
        &self.email
    }

    /// Returns the user ID.
    #[must_use]
    pub fn sub(&self) -> i64 {
        self.sub
    }

    /// Returns when the license was activated.
    #[must_use]
    pub fn activated_at(&self) -> DateTime<Utc> {
        self.activated_at
    }

    /// Returns the device fingerprint.
    #[must_use]
    pub fn device_fingerprint(&self) -> &DeviceFingerprint {
        &self.device_fingerprint
    }

    /// Returns the current license status with grace period support.
    #[must_use]
    pub fn status(&self) -> LicenseStatus {
        match self.expires_at {
            None => LicenseStatus::Active, // Perpetual
            Some(expires_at) => {
                let now = Utc::now();
                if now < expires_at {
                    LicenseStatus::Active
                } else {
                    let secs_past = (now - expires_at).num_seconds();
                    if secs_past < GRACE_PERIOD_SECS {
                        let days_remaining =
                            ((GRACE_PERIOD_SECS - secs_past) / (24 * 60 * 60)) as u32;
                        LicenseStatus::Grace { days_remaining }
                    } else {
                        LicenseStatus::ReadOnly
                    }
                }
            }
        }
    }

    /// Verifies the activation is valid for the current device.
    /// Returns true for Active and Grace statuses.
    #[must_use]
    pub fn is_valid(&self) -> bool {
        // Check device matches
        if !self.device_fingerprint.matches_current() {
            return false;
        }

        self.status().is_usable()
    }
}

/// Manages activation storage and retrieval.
pub struct ActivationStore {
    /// Path to the activation file.
    path: std::path::PathBuf,
}

impl ActivationStore {
    /// Creates a new activation store at the given path.
    pub fn new(path: impl AsRef<Path>) -> Self {
        Self {
            path: path.as_ref().to_path_buf(),
        }
    }

    /// Returns the default activation store path.
    #[must_use]
    pub fn default_path() -> std::path::PathBuf {
        // Use platform-specific app data directory
        #[cfg(target_os = "macos")]
        {
            dirs::data_local_dir()
                .unwrap_or_else(|| std::path::PathBuf::from("."))
                .join("PrivStack")
                .join("activation.json")
        }

        #[cfg(target_os = "windows")]
        {
            dirs::data_local_dir()
                .unwrap_or_else(|| std::path::PathBuf::from("."))
                .join("PrivStack")
                .join("activation.json")
        }

        #[cfg(target_os = "linux")]
        {
            dirs::data_local_dir()
                .unwrap_or_else(|| std::path::PathBuf::from("."))
                .join("privstack")
                .join("activation.json")
        }

        #[cfg(not(any(target_os = "macos", target_os = "windows", target_os = "linux")))]
        {
            std::path::PathBuf::from("activation.json")
        }
    }

    /// Saves an activation record.
    pub fn save(&self, activation: &Activation) -> LicenseResult<()> {
        // Ensure parent directory exists
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| LicenseError::Storage(e.to_string()))?;
        }

        let json = serde_json::to_string_pretty(activation)?;
        std::fs::write(&self.path, json).map_err(|e| LicenseError::Storage(e.to_string()))?;

        Ok(())
    }

    /// Loads the stored activation record, re-verifying the Ed25519 signature
    /// and overwriting any tampered fields from the verified payload.
    pub fn load(&self) -> LicenseResult<Option<Activation>> {
        self.load_with_key(None)
    }

    /// Loads the stored activation record with an optional custom verify key.
    /// Used by tests with a generated keypair. Pass `None` for production.
    pub fn load_with_key(
        &self,
        verify_key: Option<&[u8; 32]>,
    ) -> LicenseResult<Option<Activation>> {
        if !self.path.exists() {
            return Ok(None);
        }

        let json = std::fs::read_to_string(&self.path)
            .map_err(|e| LicenseError::Storage(e.to_string()))?;

        let mut activation: Activation = serde_json::from_str(&json)?;
        activation.revalidate_from_key(verify_key)?;
        Ok(Some(activation))
    }

    /// Checks if an activation exists.
    #[must_use]
    pub fn has_activation(&self) -> bool {
        self.path.exists()
    }

    /// Clears the stored activation (for logout/deactivation).
    pub fn clear(&self) -> LicenseResult<()> {
        if self.path.exists() {
            std::fs::remove_file(&self.path).map_err(|e| LicenseError::Storage(e.to_string()))?;
        }
        Ok(())
    }
}

/// Activates a license key (offline validation only).
///
/// For online activation, use the `activate_online` function with the `online` feature.
pub fn activate_offline(key: &LicenseKey) -> LicenseResult<Activation> {
    let fingerprint = DeviceFingerprint::generate();

    // Generate a local activation token (not server-signed)
    let token = format!("offline-{}-{}", key.raw(), fingerprint.id());

    Ok(Activation::new(key, fingerprint, token))
}

/// Activates a license key with the activation server.
#[cfg(feature = "online")]
pub async fn activate_online(key: &LicenseKey, server_url: &str) -> LicenseResult<Activation> {
    use crate::device::DeviceInfo;

    let fingerprint = DeviceFingerprint::generate();
    let device_info = DeviceInfo::collect();

    #[derive(Serialize)]
    struct ActivationRequest {
        license_key: String,
        device_fingerprint: String,
        device_info: DeviceInfo,
    }

    #[derive(Deserialize)]
    struct ActivationResponse {
        success: bool,
        activation_token: Option<String>,
        error: Option<String>,
        remaining_activations: Option<u32>,
    }

    let client = reqwest::Client::new();
    let request = ActivationRequest {
        license_key: key.raw().to_string(),
        device_fingerprint: fingerprint.id().to_string(),
        device_info,
    };

    let response = client
        .post(&format!("{server_url}/api/activate"))
        .json(&request)
        .send()
        .await
        .map_err(|e| LicenseError::Network(e.to_string()))?;

    let result: ActivationResponse = response
        .json()
        .await
        .map_err(|e| LicenseError::Network(e.to_string()))?;

    if !result.success {
        return Err(LicenseError::ActivationFailed(
            result.error.unwrap_or_else(|| "unknown error".to_string()),
        ));
    }

    let token = result
        .activation_token
        .ok_or_else(|| LicenseError::ActivationFailed("no token received".to_string()))?;

    let mut activation = Activation::new(key, fingerprint, token);
    activation.remaining_activations = result.remaining_activations;

    Ok(activation)
}
