//! Cross-system verification tests against real MinIO.
//!
//! Validates multi-device access, entity prefix isolation,
//! and batch ordering semantics.
//! Requires: `docker compose -f docker-compose.test.yml up -d`

mod support;

use pretty_assertions::assert_eq;
use privstack_cloud::compaction::batch_s3_key;
use serial_test::serial;

#[tokio::test]
#[serial]
async fn upload_visible_via_exists_and_list() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();
    let key = format!("{prefix}/visible.bin");

    transport.upload(&creds, &key, b"data".to_vec()).await.unwrap();

    assert!(transport.exists(&creds, &key).await.unwrap());

    let listed = transport.list_keys(&creds, &prefix).await.unwrap();
    assert!(listed.contains(&key));
}

#[tokio::test]
#[serial]
async fn multiple_devices_can_download_same_batch() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();
    let key = format!("{prefix}/shared-batch.enc");

    let payload = b"batch data both devices need";
    transport.upload(&creds, &key, payload.to_vec()).await.unwrap();

    // Simulate two devices downloading with same creds (same user, different devices)
    let device_a = transport.download(&creds, &key).await.unwrap();
    let device_b = transport.download(&creds, &key).await.unwrap();

    assert_eq!(device_a, payload.to_vec());
    assert_eq!(device_b, payload.to_vec());
    assert_eq!(device_a, device_b);
}

#[tokio::test]
#[serial]
async fn entity_prefix_isolation() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();

    let user_id = 77;
    let workspace_id = "ws-iso";

    let entities = ["ent-alpha", "ent-beta", "ent-gamma"];

    for (i, entity_id) in entities.iter().enumerate() {
        let s3_key = batch_s3_key(user_id, workspace_id, entity_id, 0, 10);
        transport
            .upload(&creds, &s3_key, format!("data-{i}").into_bytes())
            .await
            .unwrap();
    }

    // List by each entity prefix — should only see that entity's batches
    for entity_id in &entities {
        let prefix = format!("{user_id}/{workspace_id}/entities/{entity_id}/");
        let keys = transport.list_keys(&creds, &prefix).await.unwrap();
        assert_eq!(keys.len(), 1, "entity {entity_id} should have exactly 1 batch");
        assert!(keys[0].contains(entity_id));
    }

    // Full workspace prefix sees all 3
    let ws_prefix = format!("{user_id}/{workspace_id}/entities/");
    let all_keys = transport.list_keys(&creds, &ws_prefix).await.unwrap();
    assert_eq!(all_keys.len(), 3);
}

#[tokio::test]
#[serial]
async fn batch_ordering_by_list() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();

    let user_id = 88;
    let workspace_id = "ws-order";
    let entity_id = "ent-order";

    let ranges = [(0, 10), (10, 20), (20, 30)];

    for (start, end) in &ranges {
        let s3_key = batch_s3_key(user_id, workspace_id, entity_id, *start, *end);
        transport
            .upload(&creds, &s3_key, format!("batch_{start}_{end}").into_bytes())
            .await
            .unwrap();
    }

    let prefix = format!("{user_id}/{workspace_id}/entities/{entity_id}/");
    let mut keys = transport.list_keys(&creds, &prefix).await.unwrap();
    keys.sort();

    assert_eq!(keys.len(), 3);
    assert!(keys[0].contains("batch_0_10"));
    assert!(keys[1].contains("batch_10_20"));
    assert!(keys[2].contains("batch_20_30"));

    // Verify each batch contains the expected data
    for (i, (start, end)) in ranges.iter().enumerate() {
        let data = transport.download(&creds, &keys[i]).await.unwrap();
        assert_eq!(data, format!("batch_{start}_{end}").into_bytes());
    }
}
