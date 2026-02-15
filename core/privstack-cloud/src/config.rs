//! Cloud sync configuration.

use serde::{Deserialize, Serialize};

/// Configuration for the cloud sync engine.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct CloudConfig {
    /// Base URL for the PrivStack API (e.g., "https://api.privstack.io").
    pub api_base_url: String,

    /// S3 bucket name.
    pub s3_bucket: String,

    /// AWS region for S3.
    pub s3_region: String,

    /// Optional S3 endpoint override (for MinIO in testing).
    pub s3_endpoint_override: Option<String>,

    /// Credential refresh margin in seconds (refresh before expiry).
    pub credential_refresh_margin_secs: i64,

    /// Poll interval for checking new data from other devices (seconds).
    pub poll_interval_secs: u64,
}

impl Default for CloudConfig {
    fn default() -> Self {
        Self {
            api_base_url: "https://api.privstack.io".to_string(),
            s3_bucket: "privstack-cloud".to_string(),
            s3_region: "us-east-1".to_string(),
            s3_endpoint_override: None,
            credential_refresh_margin_secs: 300, // 5 minutes before expiry
            poll_interval_secs: 30,
        }
    }
}

impl CloudConfig {
    /// Creates a config for testing with MinIO.
    #[cfg(test)]
    pub fn test() -> Self {
        Self {
            api_base_url: "http://localhost:3002".to_string(),
            s3_bucket: "privstack-cloud".to_string(),
            s3_region: "us-east-1".to_string(),
            s3_endpoint_override: Some("http://localhost:9000".to_string()),
            credential_refresh_margin_secs: 60,
            poll_interval_secs: 5,
        }
    }
}
