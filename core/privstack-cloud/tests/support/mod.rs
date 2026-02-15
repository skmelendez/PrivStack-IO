//! Shared test helpers for integration tests against real MinIO.

use chrono::{Duration, Utc};
use privstack_cloud::StsCredentials;
use privstack_cloud::s3_transport::S3Transport;
use privstack_crypto::{DerivedKey, EncryptedData, encrypt, generate_random_key};
use uuid::Uuid;

/// MinIO root credentials masquerading as STS tokens.
/// MinIO accepts any session_token value with the root access/secret pair.
pub fn fake_minio_creds() -> StsCredentials {
    StsCredentials {
        access_key_id: "privstack-test".into(),
        secret_access_key: "privstack-test-secret".into(),
        session_token: "integration-test-token".into(),
        expires_at: Utc::now() + Duration::hours(1),
        bucket: "privstack-cloud".into(),
        region: "us-east-1".into(),
    }
}

/// Already-expired credentials for negative tests.
pub fn expired_minio_creds() -> StsCredentials {
    StsCredentials {
        access_key_id: "privstack-test".into(),
        secret_access_key: "privstack-test-secret".into(),
        session_token: "expired-token".into(),
        expires_at: Utc::now() - Duration::seconds(10),
        bucket: "privstack-cloud".into(),
        region: "us-east-1".into(),
    }
}

/// S3Transport pointing at local MinIO (docker-compose.test.yml).
pub fn test_transport() -> S3Transport {
    S3Transport::new(
        "privstack-cloud".into(),
        "us-east-1".into(),
        Some("http://localhost:9000".into()),
    )
}

/// Per-test unique S3 prefix to prevent collisions.
pub fn unique_prefix() -> String {
    format!("test-runs/{}", Uuid::new_v4())
}

/// Encrypt a plaintext payload with a fresh DEK, returning both.
pub fn encrypt_test_payload(plaintext: &[u8]) -> (DerivedKey, Vec<u8>) {
    let dek = generate_random_key();
    let encrypted: EncryptedData = encrypt(&dek, plaintext).expect("encryption must succeed");
    let bytes = serde_json::to_vec(&encrypted).expect("serialization must succeed");
    (dek, bytes)
}
