//! Pipeline integration tests: encrypt → upload → download → decrypt.
//!
//! Exercises the full sync engine serialization pattern against real MinIO.
//! Requires: `docker compose -f docker-compose.test.yml up -d`

mod support;

use pretty_assertions::assert_eq;
use privstack_cloud::compaction::{batch_s3_key, blob_s3_key, snapshot_s3_key};
use privstack_crypto::{decrypt, encrypt, generate_random_key, EncryptedData};
use serial_test::serial;
use sha2::{Digest, Sha256};

/// Simulates the event batch structure used by sync_engine.
#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize)]
struct TestEvent {
    id: String,
    entity_id: String,
    payload: String,
}

fn make_events(count: usize, entity_id: &str) -> Vec<TestEvent> {
    (0..count)
        .map(|i| TestEvent {
            id: format!("evt-{i}"),
            entity_id: entity_id.into(),
            payload: format!("payload-{i}"),
        })
        .collect()
}

#[tokio::test]
#[serial]
async fn batch_roundtrip_encrypt_upload_download_decrypt() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let events = make_events(10, "entity-abc");
    let serialized = serde_json::to_vec(&events).unwrap();

    let dek = generate_random_key();
    let encrypted = encrypt(&dek, &serialized).unwrap();
    let encrypted_bytes = serde_json::to_vec(&encrypted).unwrap();

    let key = format!("{prefix}/batch_0_10.enc");
    transport.upload(&creds, &key, encrypted_bytes).await.unwrap();

    let downloaded = transport.download(&creds, &key).await.unwrap();
    let decrypted_envelope: EncryptedData = serde_json::from_slice(&downloaded).unwrap();
    let plaintext = decrypt(&dek, &decrypted_envelope).unwrap();
    let recovered: Vec<TestEvent> = serde_json::from_slice(&plaintext).unwrap();

    assert_eq!(recovered, events);
}

#[tokio::test]
#[serial]
async fn wrong_dek_fails_decryption() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let dek_a = generate_random_key();
    let dek_b = generate_random_key();

    let plaintext = b"secret data for dek_a";
    let encrypted = encrypt(&dek_a, plaintext).unwrap();
    let encrypted_bytes = serde_json::to_vec(&encrypted).unwrap();

    let key = format!("{prefix}/wrong-dek.enc");
    transport.upload(&creds, &key, encrypted_bytes).await.unwrap();

    let downloaded = transport.download(&creds, &key).await.unwrap();
    let envelope: EncryptedData = serde_json::from_slice(&downloaded).unwrap();

    let result = decrypt(&dek_b, &envelope);
    assert!(result.is_err(), "decryption with wrong DEK must fail");
}

#[tokio::test]
#[serial]
async fn multi_entity_isolation() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let dek_a = generate_random_key();
    let dek_b = generate_random_key();

    let events_a = make_events(3, "entity-A");
    let events_b = make_events(3, "entity-B");

    // Upload entity A
    let enc_a = encrypt(&dek_a, &serde_json::to_vec(&events_a).unwrap()).unwrap();
    let key_a = format!("{prefix}/entity-A/batch_0_3.enc");
    transport
        .upload(&creds, &key_a, serde_json::to_vec(&enc_a).unwrap())
        .await
        .unwrap();

    // Upload entity B
    let enc_b = encrypt(&dek_b, &serde_json::to_vec(&events_b).unwrap()).unwrap();
    let key_b = format!("{prefix}/entity-B/batch_0_3.enc");
    transport
        .upload(&creds, &key_b, serde_json::to_vec(&enc_b).unwrap())
        .await
        .unwrap();

    // Same-entity decrypt succeeds
    let dl_a = transport.download(&creds, &key_a).await.unwrap();
    let env_a: EncryptedData = serde_json::from_slice(&dl_a).unwrap();
    let plain_a = decrypt(&dek_a, &env_a).unwrap();
    let recovered_a: Vec<TestEvent> = serde_json::from_slice(&plain_a).unwrap();
    assert_eq!(recovered_a, events_a);

    // Cross-entity decrypt fails
    let cross_result = decrypt(&dek_b, &env_a);
    assert!(cross_result.is_err(), "cross-entity decrypt must fail");
}

#[tokio::test]
#[serial]
async fn snapshot_roundtrip() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();

    let user_id = 42;
    let workspace_id = "ws-snap";
    let entity_id = "ent-snap";
    let cursor = 100;

    let state_blob = b"full entity state at cursor 100";
    let dek = generate_random_key();
    let encrypted = encrypt(&dek, state_blob).unwrap();
    let encrypted_bytes = serde_json::to_vec(&encrypted).unwrap();

    let s3_key = snapshot_s3_key(user_id, workspace_id, entity_id, cursor);
    transport.upload(&creds, &s3_key, encrypted_bytes).await.unwrap();

    let downloaded = transport.download(&creds, &s3_key).await.unwrap();
    let envelope: EncryptedData = serde_json::from_slice(&downloaded).unwrap();
    let decrypted = decrypt(&dek, &envelope).unwrap();
    assert_eq!(decrypted, state_blob);
}

#[tokio::test]
#[serial]
async fn blob_roundtrip_with_hash_verification() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();

    let user_id = 42;
    let workspace_id = "ws-blob";
    let blob_id = "blob-001";

    let file_data = b"binary file content for blob test";
    let original_hash = Sha256::digest(file_data);

    let dek = generate_random_key();
    let encrypted = encrypt(&dek, file_data).unwrap();
    let encrypted_bytes = serde_json::to_vec(&encrypted).unwrap();

    let s3_key = blob_s3_key(user_id, workspace_id, blob_id);
    transport.upload(&creds, &s3_key, encrypted_bytes).await.unwrap();

    let downloaded = transport.download(&creds, &s3_key).await.unwrap();
    let envelope: EncryptedData = serde_json::from_slice(&downloaded).unwrap();
    let decrypted = decrypt(&dek, &envelope).unwrap();

    let recovered_hash = Sha256::digest(&decrypted);
    assert_eq!(recovered_hash, original_hash);
    assert_eq!(decrypted, file_data);
}

#[tokio::test]
#[serial]
async fn key_format_listing() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();

    let user_id = 99;
    let workspace_id = "ws-list";
    let entity_id = "ent-list";

    let dek = generate_random_key();

    for (start, end) in [(0, 10), (10, 20), (20, 30)] {
        let s3_key = batch_s3_key(user_id, workspace_id, entity_id, start, end);
        let data = encrypt(&dek, format!("batch {start}-{end}").as_bytes()).unwrap();
        transport
            .upload(&creds, &s3_key, serde_json::to_vec(&data).unwrap())
            .await
            .unwrap();
    }

    let entity_prefix = format!("{user_id}/{workspace_id}/entities/{entity_id}/");
    let keys = transport.list_keys(&creds, &entity_prefix).await.unwrap();
    assert_eq!(keys.len(), 3);

    for key in &keys {
        assert!(key.starts_with(&entity_prefix));
        assert!(key.ends_with(".enc"));
    }
}

#[tokio::test]
#[serial]
async fn concurrent_uploads_all_downloadable() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let mut handles = Vec::new();

    for i in 0..10u32 {
        let t = support::test_transport();
        let c = creds.clone();
        let p = prefix.clone();

        handles.push(tokio::spawn(async move {
            let key = format!("{p}/concurrent-{i}.bin");
            let data = format!("payload-{i}").into_bytes();
            t.upload(&c, &key, data.clone()).await.unwrap();
            (key, data)
        }));
    }

    let results: Vec<_> = futures::future::join_all(handles)
        .await
        .into_iter()
        .map(|r| r.unwrap())
        .collect();

    for (key, expected_data) in results {
        let downloaded = transport.download(&creds, &key).await.unwrap();
        assert_eq!(downloaded, expected_data);
    }
}
