//! Adversarial tests for DekRegistry.
//!
//! Validates thread-safety, missing entity behavior, key overwrite semantics,
//! and concurrent read/write correctness under contention.

use privstack_cloud::dek_registry::DekRegistry;
use privstack_cloud::error::CloudError;
use privstack_crypto::generate_random_key;
use std::sync::Arc;

#[tokio::test]
async fn get_missing_entity_returns_envelope_error() {
    let registry = DekRegistry::new();
    let err = registry.get("nonexistent-entity").await.unwrap_err();

    match &err {
        CloudError::Envelope(msg) => {
            assert!(
                msg.contains("nonexistent-entity"),
                "error should include entity_id, got: {msg}"
            );
            assert!(
                msg.contains("no DEK registered"),
                "error should say 'no DEK registered', got: {msg}"
            );
        }
        other => panic!("expected CloudError::Envelope, got: {other:?}"),
    }
}

#[tokio::test]
async fn get_after_remove_returns_error() {
    let registry = DekRegistry::new();
    let key = generate_random_key();

    registry.insert("entity-1".to_string(), key).await;
    assert!(registry.get("entity-1").await.is_ok());

    let removed = registry.remove("entity-1").await;
    assert!(removed.is_some());

    let err = registry.get("entity-1").await.unwrap_err();
    assert!(matches!(err, CloudError::Envelope(_)));
}

#[tokio::test]
async fn remove_nonexistent_returns_none() {
    let registry = DekRegistry::new();
    assert!(registry.remove("ghost").await.is_none());
}

#[tokio::test]
async fn insert_overwrite_uses_latest_key() {
    let registry = DekRegistry::new();
    let key_a = generate_random_key();
    let key_b = generate_random_key();

    let key_a_bytes = *key_a.as_bytes();
    let key_b_bytes = *key_b.as_bytes();

    // Ensure keys are different
    assert_ne!(key_a_bytes, key_b_bytes);

    registry.insert("entity-1".to_string(), key_a).await;
    registry.insert("entity-1".to_string(), key_b).await;

    let retrieved = registry.get("entity-1").await.unwrap();
    assert_eq!(*retrieved.as_bytes(), key_b_bytes);
    assert_eq!(registry.len().await, 1, "overwrite should not create duplicates");
}

#[tokio::test]
async fn empty_registry_state() {
    let registry = DekRegistry::new();
    assert!(registry.is_empty().await);
    assert_eq!(registry.len().await, 0);
}

#[tokio::test]
async fn clone_shares_same_underlying_data() {
    let registry = DekRegistry::new();
    let clone = registry.clone();

    let key = generate_random_key();
    let key_bytes = *key.as_bytes();

    registry.insert("entity-1".to_string(), key).await;

    // Clone should see the same data (shared Arc<RwLock>)
    let retrieved = clone.get("entity-1").await.unwrap();
    assert_eq!(*retrieved.as_bytes(), key_bytes);
    assert_eq!(clone.len().await, 1);
}

#[tokio::test]
async fn many_entities_independent() {
    let registry = DekRegistry::new();

    for i in 0..100 {
        registry
            .insert(format!("entity-{i}"), generate_random_key())
            .await;
    }

    assert_eq!(registry.len().await, 100);

    // Remove one entity, verify others remain
    registry.remove("entity-50").await;
    assert_eq!(registry.len().await, 99);
    assert!(registry.get("entity-50").await.is_err());
    assert!(registry.get("entity-49").await.is_ok());
    assert!(registry.get("entity-51").await.is_ok());
}

#[tokio::test]
async fn concurrent_inserts_no_data_loss() {
    let registry = Arc::new(DekRegistry::new());
    let mut handles = Vec::new();

    // 100 concurrent inserts with unique entity IDs
    for i in 0..100 {
        let reg = Arc::clone(&registry);
        handles.push(tokio::spawn(async move {
            reg.insert(format!("entity-{i}"), generate_random_key())
                .await;
        }));
    }

    for handle in handles {
        handle.await.unwrap();
    }

    assert_eq!(
        registry.len().await,
        100,
        "all 100 concurrent inserts should succeed"
    );
}

#[tokio::test]
async fn concurrent_reads_while_writing() {
    let registry = Arc::new(DekRegistry::new());

    // Pre-populate 50 entities
    for i in 0..50 {
        registry
            .insert(format!("entity-{i}"), generate_random_key())
            .await;
    }

    let mut handles = Vec::new();

    // 50 readers + 50 writers running simultaneously
    for i in 0..50 {
        // Reader: reads existing entity
        let reg = Arc::clone(&registry);
        handles.push(tokio::spawn(async move {
            let result = reg.get(&format!("entity-{i}")).await;
            assert!(result.is_ok(), "concurrent read should succeed");
        }));

        // Writer: inserts new entity
        let reg = Arc::clone(&registry);
        handles.push(tokio::spawn(async move {
            reg.insert(format!("new-entity-{i}"), generate_random_key())
                .await;
        }));
    }

    for handle in handles {
        handle.await.unwrap();
    }

    assert_eq!(
        registry.len().await,
        100,
        "50 original + 50 new entities expected"
    );
}

#[tokio::test]
async fn concurrent_insert_remove_same_entity() {
    let registry = Arc::new(DekRegistry::new());

    // Rapid insert/remove on same entity_id — no panics or deadlocks
    let mut handles = Vec::new();
    for _ in 0..100 {
        let reg = Arc::clone(&registry);
        handles.push(tokio::spawn(async move {
            reg.insert("contested".to_string(), generate_random_key())
                .await;
        }));

        let reg = Arc::clone(&registry);
        handles.push(tokio::spawn(async move {
            let _ = reg.remove("contested").await;
        }));
    }

    for handle in handles {
        handle.await.unwrap();
    }

    // Final state: entity either exists or not, but no panic/deadlock
    let len = registry.len().await;
    assert!(len <= 1, "at most 1 entry for 'contested', got {len}");
}

#[tokio::test]
async fn default_equals_new() {
    let a = DekRegistry::new();
    let b = DekRegistry::default();

    assert!(a.is_empty().await);
    assert!(b.is_empty().await);
}
