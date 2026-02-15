use privstack_cloud::compaction::*;

#[test]
fn needs_compaction_above_threshold() {
    assert!(needs_compaction(51));
}

#[test]
fn needs_compaction_at_threshold() {
    assert!(!needs_compaction(50));
}

#[test]
fn needs_compaction_below_threshold() {
    assert!(!needs_compaction(10));
}

#[test]
fn needs_compaction_zero() {
    assert!(!needs_compaction(0));
}

#[test]
fn snapshot_s3_key_format() {
    let key = snapshot_s3_key(42, "ws-abc", "ent-xyz", 100);
    assert_eq!(key, "users/42/workspaces/ws-abc/entities/ent-xyz/snapshot_100.enc");
}

#[test]
fn batch_s3_key_format() {
    let key = batch_s3_key(42, "ws-abc", "ent-xyz", 0, 10);
    assert_eq!(key, "users/42/workspaces/ws-abc/entities/ent-xyz/batch_0_10.enc");
}

#[test]
fn blob_s3_key_format() {
    let key = blob_s3_key(42, "ws-abc", "blob-id");
    assert_eq!(key, "users/42/workspaces/ws-abc/blobs/blob-id.enc");
}

#[test]
fn private_key_s3_key_format() {
    let key = private_key_s3_key(42, "ws-abc");
    assert_eq!(key, "users/42/workspaces/ws-abc/keys/private_key.enc");
}

#[test]
fn s3_key_with_special_chars_in_ids() {
    let key = snapshot_s3_key(1, "ws-with-dashes", "ent-with-dashes", 999);
    assert_eq!(key, "users/1/workspaces/ws-with-dashes/entities/ent-with-dashes/snapshot_999.enc");
}
