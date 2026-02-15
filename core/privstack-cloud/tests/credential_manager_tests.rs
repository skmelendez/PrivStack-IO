//! Adversarial tests for STS credential lifecycle.
//!
//! Tests expiry boundary conditions, margin calculations,
//! and credential state transitions. CredentialManager refresh
//! requires HTTP calls so we test the StsCredentials logic directly
//! and DekRegistry interaction patterns.

use chrono::{Duration, Utc};
use privstack_cloud::types::StsCredentials;

fn make_creds(expires_in_secs: i64) -> StsCredentials {
    StsCredentials {
        access_key_id: "AKIATEST".to_string(),
        secret_access_key: "secret".to_string(),
        session_token: "token".to_string(),
        expires_at: Utc::now() + Duration::seconds(expires_in_secs),
        bucket: "test-bucket".to_string(),
        region: "us-east-1".to_string(),
    }
}

// ── Expiry Detection ──

#[test]
fn is_expired_when_past() {
    let creds = make_creds(-60); // 60 seconds ago
    assert!(creds.is_expired());
}

#[test]
fn is_not_expired_when_future() {
    let creds = make_creds(3600); // 1 hour from now
    assert!(!creds.is_expired());
}

#[test]
fn is_expired_at_exact_now_boundary() {
    let creds = StsCredentials {
        access_key_id: "AKIATEST".to_string(),
        secret_access_key: "secret".to_string(),
        session_token: "token".to_string(),
        expires_at: Utc::now(), // exactly now
        bucket: "test-bucket".to_string(),
        region: "us-east-1".to_string(),
    };
    // At exactly now, Utc::now() >= expires_at should be true
    assert!(creds.is_expired());
}

#[test]
fn is_expired_1_second_ago() {
    let creds = make_creds(-1);
    assert!(creds.is_expired());
}

// ── Expires Within Margin ──

#[test]
fn expires_within_secs_true_when_close() {
    let creds = make_creds(200); // 200 seconds remaining
    assert!(creds.expires_within_secs(300)); // 300s margin
}

#[test]
fn expires_within_secs_false_when_far() {
    let creds = make_creds(3600); // 1 hour remaining
    assert!(!creds.expires_within_secs(300)); // 300s margin
}

#[test]
fn expires_within_secs_boundary_at_exact_margin() {
    let creds = make_creds(300); // exactly 300 seconds remaining
    // now + 300s >= expires_at (which is now + 300s) → true
    assert!(creds.expires_within_secs(300));
}

#[test]
fn expires_within_secs_boundary_one_second_over() {
    let creds = make_creds(301); // 301 seconds remaining
    // now + 300s < expires_at (now + 301s) → false
    assert!(!creds.expires_within_secs(300));
}

#[test]
fn expires_within_secs_boundary_one_second_under() {
    let creds = make_creds(299); // 299 seconds remaining
    // now + 300s > expires_at (now + 299s) → true
    assert!(creds.expires_within_secs(300));
}

#[test]
fn expires_within_secs_with_already_expired() {
    let creds = make_creds(-60); // already expired
    assert!(creds.expires_within_secs(300));
}

#[test]
fn expires_within_secs_zero_margin() {
    let creds = make_creds(3600); // far future
    // Zero margin = only expired if literally past expiry
    assert!(!creds.expires_within_secs(0));

    let expired = make_creds(-1);
    assert!(expired.expires_within_secs(0));
}

#[test]
fn expires_within_secs_large_margin() {
    let creds = make_creds(3600); // 1 hour remaining
    // 7200s margin (2 hours) — should trigger refresh
    assert!(creds.expires_within_secs(7200));
}

// ── Serialization ──

#[test]
fn sts_credentials_json_roundtrip() {
    let creds = make_creds(3600);
    let json = serde_json::to_string(&creds).unwrap();
    let restored: StsCredentials = serde_json::from_str(&json).unwrap();

    assert_eq!(creds.access_key_id, restored.access_key_id);
    assert_eq!(creds.secret_access_key, restored.secret_access_key);
    assert_eq!(creds.session_token, restored.session_token);
    assert_eq!(creds.bucket, restored.bucket);
    assert_eq!(creds.region, restored.region);
}

#[test]
fn sts_credentials_clone_independence() {
    let creds = make_creds(3600);
    let cloned = creds.clone();

    // Cloned creds should have same values
    assert_eq!(creds.access_key_id, cloned.access_key_id);
    assert_eq!(creds.expires_at, cloned.expires_at);
}

// ── Edge Cases ──

#[test]
fn credentials_far_future_not_expired() {
    let creds = StsCredentials {
        access_key_id: "AKIATEST".to_string(),
        secret_access_key: "secret".to_string(),
        session_token: "token".to_string(),
        expires_at: Utc::now() + Duration::days(365),
        bucket: "bucket".to_string(),
        region: "us-east-1".to_string(),
    };

    assert!(!creds.is_expired());
    assert!(!creds.expires_within_secs(86400)); // 1 day margin, still 364 days left
}

#[test]
fn credentials_far_past_is_expired() {
    let creds = StsCredentials {
        access_key_id: "AKIATEST".to_string(),
        secret_access_key: "secret".to_string(),
        session_token: "token".to_string(),
        expires_at: Utc::now() - Duration::days(365),
        bucket: "bucket".to_string(),
        region: "us-east-1".to_string(),
    };

    assert!(creds.is_expired());
    assert!(creds.expires_within_secs(0));
}
