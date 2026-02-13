mod common;

use common::{make_monthly_key_at, make_perpetual_key, test_keypair};
use privstack_license::{activate_offline, Activation, ActivationStore, LicenseKey, LicensePlan, LicenseStatus};
use tempfile::tempdir;

// ── Offline activation ───────────────────────────────────────────

#[test]
fn offline_activation_perpetual() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    assert!(activation.is_valid());
    assert_eq!(activation.license_plan(), LicensePlan::Perpetual);
    assert_eq!(activation.status(), LicenseStatus::Active);
    assert!(!activation.license_key().is_empty());
}

#[test]
fn offline_activation_all_plans() {
    let (sk, pk) = test_keypair();
    let now = chrono::Utc::now().timestamp();

    for (plan_str, expected_plan) in [
        ("monthly", LicensePlan::Monthly),
        ("annual", LicensePlan::Annual),
        ("perpetual", LicensePlan::Perpetual),
    ] {
        let payload = format!(
            r#"{{"sub":1,"email":"test@example.com","plan":"{plan_str}","iat":{now}}}"#
        );
        let key_str = common::sign_key(&sk, &payload);
        let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
        let activation = activate_offline(&parsed).unwrap();
        assert_eq!(activation.license_plan(), expected_plan);
        assert!(activation.is_valid());
    }
}

#[test]
fn activation_device_fingerprint() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    assert!(!activation.device_fingerprint().id().is_empty());
    assert!(activation.device_fingerprint().matches_current());
}

#[test]
fn activation_activated_at() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    assert!(activation.activated_at() <= chrono::Utc::now());
}

#[test]
fn activation_email_and_sub() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    assert_eq!(activation.email(), "test@example.com");
    assert_eq!(activation.sub(), 1);
}

// ── Activation serde ─────────────────────────────────────────────

#[test]
fn activation_serialization_roundtrip() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let json = serde_json::to_string(&activation).unwrap();
    let restored: Activation = serde_json::from_str(&json).unwrap();

    assert_eq!(activation.license_key(), restored.license_key());
    assert_eq!(activation.license_plan(), restored.license_plan());
}

// ── ActivationStore ──────────────────────────────────────────────

#[test]
fn activation_store_save_load() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("activation.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    assert!(store.has_activation());

    let loaded = store.load_with_key(Some(&pk)).unwrap().unwrap();
    assert_eq!(loaded.license_key(), activation.license_key());
    assert_eq!(loaded.license_plan(), LicensePlan::Perpetual);
}

#[test]
fn activation_store_clear() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("activation.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    assert!(store.has_activation());

    store.clear().unwrap();
    assert!(!store.has_activation());
}

#[test]
fn activation_store_load_empty() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("no-file.json"));
    assert!(!store.has_activation());
    let loaded = store.load().unwrap();
    assert!(loaded.is_none());
}

#[test]
fn activation_store_clear_nonexistent() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("no-file.json"));
    store.clear().unwrap(); // should not error
}

#[test]
fn activation_store_default_path() {
    let path = ActivationStore::default_path();
    assert!(
        path.to_str().unwrap().contains("privstack")
            || path.to_str().unwrap().contains("PrivStack")
    );
}

#[test]
fn activation_store_creates_parent_dirs() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("sub/dir/activation.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    assert!(store.has_activation());
}

#[test]
fn activation_store_overwrite() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("activation.json"));
    let (sk, pk) = test_keypair();
    let now = chrono::Utc::now().timestamp();

    // Save monthly
    let key1 = make_monthly_key_at(&sk, now);
    let parsed1 = LicenseKey::parse_with_key(&key1, &pk).unwrap();
    let a1 = activate_offline(&parsed1).unwrap();
    store.save(&a1).unwrap();

    // Overwrite with perpetual
    let key2 = make_perpetual_key(&sk);
    let parsed2 = LicenseKey::parse_with_key(&key2, &pk).unwrap();
    let a2 = activate_offline(&parsed2).unwrap();
    store.save(&a2).unwrap();

    let loaded = store.load_with_key(Some(&pk)).unwrap().unwrap();
    assert_eq!(loaded.license_plan(), LicensePlan::Perpetual);
}

// ── status() behavior ────────────────────────────────────────────

#[test]
fn status_returns_active_for_valid_activation() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    assert_eq!(activation.status(), LicenseStatus::Active);
}

#[test]
fn is_valid_returns_true_for_current_device() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    assert!(activation.is_valid());
}

// ── load() with corrupted JSON ───────────────────────────────────

#[test]
fn load_corrupted_json_returns_error() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("corrupted.json");
    std::fs::write(&path, "this is not valid json {{{").unwrap();

    let store = ActivationStore::new(&path);
    assert!(store.has_activation());
    let result = store.load();
    assert!(result.is_err());
}

#[test]
fn load_partial_json_returns_error() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("partial.json");
    std::fs::write(&path, r#"{"license_key": "abc"#).unwrap();

    let store = ActivationStore::new(&path);
    let result = store.load();
    assert!(result.is_err());
}

#[test]
fn load_empty_file_returns_error() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("empty.json");
    std::fs::write(&path, "").unwrap();

    let store = ActivationStore::new(&path);
    let result = store.load();
    assert!(result.is_err());
}

#[test]
fn load_wrong_json_structure_returns_error() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("wrong.json");
    std::fs::write(&path, r#"{"wrong_field": true}"#).unwrap();

    let store = ActivationStore::new(&path);
    let result = store.load();
    assert!(result.is_err());
}

// ── save() directory creation edge cases ─────────────────────────

#[test]
fn save_creates_deeply_nested_directories() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("a/b/c/d/e/activation.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    assert!(store.has_activation());
    let loaded = store.load_with_key(Some(&pk)).unwrap().unwrap();
    assert_eq!(loaded.license_plan(), LicensePlan::Perpetual);
}

#[test]
fn save_to_existing_directory_succeeds() {
    let dir = tempdir().unwrap();
    std::fs::create_dir_all(dir.path().join("existing")).unwrap();
    let store = ActivationStore::new(dir.path().join("existing/activation.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    assert!(store.has_activation());
}

#[test]
fn save_overwrites_existing_file() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("overwrite.json");
    std::fs::write(&path, "old content").unwrap();

    let store = ActivationStore::new(&path);
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    let loaded = store.load_with_key(Some(&pk)).unwrap().unwrap();
    assert_eq!(loaded.license_plan(), LicensePlan::Perpetual);
}

// ── Activation debug/clone ───────────────────────────────────────

#[test]
fn activation_debug_format() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    let debug = format!("{:?}", activation);
    assert!(debug.contains("Activation"));
}

#[test]
fn activation_clone() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    let cloned = activation.clone();
    assert_eq!(activation.license_key(), cloned.license_key());
    assert_eq!(activation.license_plan(), cloned.license_plan());
}

// ── clear then load returns None ─────────────────────────────────

#[test]
fn clear_then_load_returns_none() {
    let dir = tempdir().unwrap();
    let store = ActivationStore::new(dir.path().join("cleartest.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    store.save(&activation).unwrap();
    store.clear().unwrap();
    let loaded = store.load().unwrap();
    assert!(loaded.is_none());
    assert!(!store.has_activation());
}

// ── Expired activation status ────────────────────────────────────

#[test]
fn status_returns_grace_when_recently_expired() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let mut json_val: serde_json::Value = serde_json::to_value(&activation).unwrap();
    // Set expires_at to 1 day ago → within grace period
    let one_day_ago = chrono::Utc::now() - chrono::Duration::days(1);
    json_val["expires_at"] = serde_json::json!(one_day_ago.to_rfc3339());
    let recent_expired: Activation = serde_json::from_value(json_val).unwrap();

    match recent_expired.status() {
        LicenseStatus::Grace { days_remaining } => {
            assert!(days_remaining <= 29);
        }
        other => panic!("expected Grace, got {:?}", other),
    }
}

#[test]
fn status_returns_readonly_when_past_grace() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let mut json_val: serde_json::Value = serde_json::to_value(&activation).unwrap();
    // Set expires_at to 31 days ago → past grace
    let past_grace = chrono::Utc::now() - chrono::Duration::days(31);
    json_val["expires_at"] = serde_json::json!(past_grace.to_rfc3339());
    let expired: Activation = serde_json::from_value(json_val).unwrap();

    assert_eq!(expired.status(), LicenseStatus::ReadOnly);
}

#[test]
fn is_valid_returns_false_when_past_grace() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let mut json_val: serde_json::Value = serde_json::to_value(&activation).unwrap();
    json_val["expires_at"] = serde_json::json!("2020-01-01T00:00:00Z");
    let expired: Activation = serde_json::from_value(json_val).unwrap();

    assert!(!expired.is_valid());
}

#[test]
fn is_valid_returns_false_when_device_fingerprint_differs() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let mut json_val: serde_json::Value = serde_json::to_value(&activation).unwrap();
    json_val["device_fingerprint"]["id"] = serde_json::json!("totally-different-fingerprint");
    let tampered: Activation = serde_json::from_value(json_val).unwrap();

    assert!(!tampered.is_valid());
}

#[test]
fn status_returns_active_even_with_different_device() {
    // status() does not reject on device mismatch — it still returns Active
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let mut json_val: serde_json::Value = serde_json::to_value(&activation).unwrap();
    json_val["device_fingerprint"]["id"] = serde_json::json!("different-device");
    let tampered: Activation = serde_json::from_value(json_val).unwrap();

    assert_eq!(tampered.status(), LicenseStatus::Active);
}

#[test]
fn save_returns_storage_error_on_write_failure() {
    let dir = tempdir().unwrap();
    let blocker = dir.path().join("blocker");
    std::fs::write(&blocker, "I am a file").unwrap();
    let store = ActivationStore::new(blocker.join("activation.json"));

    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let result = store.save(&activation);
    assert!(result.is_err());
    let err_msg = format!("{}", result.unwrap_err());
    assert!(err_msg.contains("storage error"));
}

#[test]
fn load_returns_storage_error_on_read_failure() {
    let dir = tempdir().unwrap();
    let target = dir.path().join("is_a_dir");
    std::fs::create_dir_all(&target).unwrap();
    let store = ActivationStore::new(&target);

    assert!(store.has_activation());
    let result = store.load();
    assert!(result.is_err());
    let err_msg = format!("{}", result.unwrap_err());
    assert!(err_msg.contains("storage error"));
}

#[test]
fn clear_returns_storage_error_on_remove_failure() {
    let dir = tempdir().unwrap();
    let target = dir.path().join("is_a_dir");
    std::fs::create_dir_all(&target).unwrap();
    let store = ActivationStore::new(&target);

    let result = store.clear();
    assert!(result.is_err());
    let err_msg = format!("{}", result.unwrap_err());
    assert!(err_msg.contains("storage error"));
}

#[test]
fn is_valid_with_future_expiry_returns_true() {
    let (sk, pk) = test_keypair();
    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();

    let mut json_val: serde_json::Value = serde_json::to_value(&activation).unwrap();
    json_val["expires_at"] = serde_json::json!("2099-12-31T23:59:59Z");
    let future: Activation = serde_json::from_value(json_val).unwrap();

    assert!(future.is_valid());
    assert_eq!(future.status(), LicenseStatus::Active);
}

// ── Tamper-proofing tests ────────────────────────────────────────

#[test]
fn load_overwrites_tampered_expires_at() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("activation.json");
    let store = ActivationStore::new(&path);
    let (sk, pk) = test_keypair();
    let now = chrono::Utc::now().timestamp();

    // Create a monthly key and activate
    let key_str = make_monthly_key_at(&sk, now);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    store.save(&activation).unwrap();

    // Tamper: set expires_at far into the future
    let json = std::fs::read_to_string(&path).unwrap();
    let mut val: serde_json::Value = serde_json::from_str(&json).unwrap();
    val["expires_at"] = serde_json::json!("2099-12-31T23:59:59Z");
    std::fs::write(&path, serde_json::to_string_pretty(&val).unwrap()).unwrap();

    // Load should overwrite the tampered expiry from the signed payload
    let loaded = store.load_with_key(Some(&pk)).unwrap().unwrap();
    // Monthly key issued at `now` should expire at now + 30 days, NOT 2099
    let expected_exp = chrono::DateTime::from_timestamp(now + 30 * 24 * 60 * 60, 0).unwrap();
    assert_eq!(loaded.status(), activation.status());
    // Verify the loaded expiry is close to the original (not the tampered 2099 value)
    let loaded_json = serde_json::to_value(&loaded).unwrap();
    let loaded_exp_str = loaded_json["expires_at"].as_str().unwrap();
    let loaded_exp: chrono::DateTime<chrono::Utc> = loaded_exp_str.parse().unwrap();
    let diff = (loaded_exp - expected_exp).num_seconds().abs();
    assert!(diff < 2, "expiry should match signed payload, diff was {diff}s");
}

#[test]
fn load_overwrites_tampered_license_plan() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("activation.json");
    let store = ActivationStore::new(&path);
    let (sk, pk) = test_keypair();
    let now = chrono::Utc::now().timestamp();

    // Create a monthly key
    let key_str = make_monthly_key_at(&sk, now);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    store.save(&activation).unwrap();

    // Tamper: change plan to perpetual
    let json = std::fs::read_to_string(&path).unwrap();
    let mut val: serde_json::Value = serde_json::from_str(&json).unwrap();
    val["license_plan"] = serde_json::json!("perpetual");
    val["expires_at"] = serde_json::Value::Null;
    std::fs::write(&path, serde_json::to_string_pretty(&val).unwrap()).unwrap();

    // Load should restore the real plan from the signed key
    let loaded = store.load_with_key(Some(&pk)).unwrap().unwrap();
    assert_eq!(loaded.license_plan(), LicensePlan::Monthly);
}

#[test]
fn load_rejects_tampered_license_key() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("activation.json");
    let store = ActivationStore::new(&path);
    let (sk, pk) = test_keypair();

    let key_str = make_perpetual_key(&sk);
    let parsed = LicenseKey::parse_with_key(&key_str, &pk).unwrap();
    let activation = activate_offline(&parsed).unwrap();
    store.save(&activation).unwrap();

    // Tamper: corrupt the license_key field
    let json = std::fs::read_to_string(&path).unwrap();
    let mut val: serde_json::Value = serde_json::from_str(&json).unwrap();
    val["license_key"] = serde_json::json!("completely-invalid-key");
    std::fs::write(&path, serde_json::to_string_pretty(&val).unwrap()).unwrap();

    // Load should fail — signature verification will reject the corrupted key
    let result = store.load_with_key(Some(&pk));
    assert!(result.is_err());
}
