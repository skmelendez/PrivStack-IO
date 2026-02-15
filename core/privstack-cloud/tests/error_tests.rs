use privstack_cloud::CloudError;

#[test]
fn s3_error_display() {
    let err = CloudError::S3("bucket not found".into());
    assert_eq!(err.to_string(), "S3 operation failed: bucket not found");
}

#[test]
fn api_error_display() {
    let err = CloudError::Api("connection refused".into());
    assert_eq!(err.to_string(), "API request failed: connection refused");
}

#[test]
fn quota_exceeded_display() {
    let err = CloudError::QuotaExceeded { used: 100, quota: 50 };
    assert_eq!(err.to_string(), "storage quota exceeded: used 100 of 50 bytes");
}

#[test]
fn credential_expired_display() {
    let err = CloudError::CredentialExpired;
    assert_eq!(err.to_string(), "STS credentials expired or invalid");
}

#[test]
fn lock_contention_display() {
    let err = CloudError::LockContention("device-other".into());
    assert_eq!(err.to_string(), "entity lock contention: device-other");
}

#[test]
fn share_denied_display() {
    let err = CloudError::ShareDenied("not owner".into());
    assert_eq!(err.to_string(), "share operation denied: not owner");
}

#[test]
fn envelope_error_display() {
    let err = CloudError::Envelope("decryption failed".into());
    assert_eq!(err.to_string(), "envelope encryption error: decryption failed");
}

#[test]
fn auth_required_display() {
    let err = CloudError::AuthRequired;
    assert_eq!(err.to_string(), "authentication required");
}

#[test]
fn auth_failed_display() {
    let err = CloudError::AuthFailed("bad credentials".into());
    assert_eq!(err.to_string(), "authentication failed: bad credentials");
}

#[test]
fn not_found_display() {
    let err = CloudError::NotFound("workspace xyz".into());
    assert_eq!(err.to_string(), "not found: workspace xyz");
}

#[test]
fn config_error_display() {
    let err = CloudError::Config("missing api_base_url".into());
    assert_eq!(err.to_string(), "invalid configuration: missing api_base_url");
}

#[test]
fn from_serde_json_error() {
    let json_err = serde_json::from_str::<serde_json::Value>("not valid json").unwrap_err();
    let cloud_err: CloudError = json_err.into();
    assert!(cloud_err.to_string().contains("serialization error"));
}
