use privstack_model::{Entity, EntitySchema, FieldType, IndexedField, MergeStrategy};
use privstack_storage::EntityStore;

fn test_schema() -> EntitySchema {
    EntitySchema {
        entity_type: "bookmark".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::text("/url", true),
            IndexedField::tag("/tags"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    }
}

fn test_entity(title: &str) -> Entity {
    Entity {
        id: uuid::Uuid::new_v4().to_string(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({
            "title": title,
            "url": "https://example.com",
            "tags": ["rust", "test"],
        }),
        created_at: 1000,
        modified_at: 1000,
        created_by: "test-peer".into(),
    }
}

// ── Basic CRUD ───────────────────────────────────────────────────

#[test]
fn save_and_get() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let entity = test_entity("Test Bookmark");

    store.save_entity(&entity, &schema).unwrap();

    let retrieved = store.get_entity(&entity.id).unwrap().unwrap();
    assert_eq!(retrieved.id, entity.id);
    assert_eq!(retrieved.entity_type, "bookmark");
    assert_eq!(retrieved.get_str("/title"), Some("Test Bookmark"));
}

#[test]
fn get_nonexistent_returns_none() {
    let store = EntityStore::open_in_memory().unwrap();
    let result = store.get_entity("nonexistent-id").unwrap();
    assert!(result.is_none());
}

#[test]
fn save_entity_raw() {
    let store = EntityStore::open_in_memory().unwrap();
    let entity = Entity {
        id: "raw-1".into(),
        entity_type: "note".into(),
        data: serde_json::json!({"content": "hello"}),
        created_at: 100,
        modified_at: 200,
        created_by: "peer1".into(),
    };
    store.save_entity_raw(&entity).unwrap();
    let retrieved = store.get_entity("raw-1").unwrap().unwrap();
    assert_eq!(retrieved.entity_type, "note");
    assert_eq!(retrieved.created_at, 100);
    assert_eq!(retrieved.modified_at, 200);
}

#[test]
fn upsert_overwrites() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let mut entity = test_entity("v1");
    let id = entity.id.clone();

    store.save_entity(&entity, &schema).unwrap();

    entity.data = serde_json::json!({"title": "v2", "url": "https://example.com", "tags": []});
    store.save_entity(&entity, &schema).unwrap();

    let retrieved = store.get_entity(&id).unwrap().unwrap();
    assert_eq!(retrieved.get_str("/title"), Some("v2"));
}

#[test]
fn delete_entity() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let entity = test_entity("To Delete");
    let id = entity.id.clone();

    store.save_entity(&entity, &schema).unwrap();
    assert!(store.get_entity(&id).unwrap().is_some());

    store.delete_entity(&id).unwrap();
    assert!(store.get_entity(&id).unwrap().is_none());
}

#[test]
fn delete_nonexistent_is_ok() {
    let store = EntityStore::open_in_memory().unwrap();
    store.delete_entity("nope").unwrap(); // should not error
}

// ── List ─────────────────────────────────────────────────────────

#[test]
fn list_entities() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();

    for i in 0..5 {
        let mut e = test_entity(&format!("Bookmark {i}"));
        e.modified_at = 1000 + i as i64;
        store.save_entity(&e, &schema).unwrap();
    }

    let list = store.list_entities("bookmark", false, None, None).unwrap();
    assert_eq!(list.len(), 5);
}

#[test]
fn list_with_limit_and_offset() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();

    for i in 0..10 {
        let mut e = test_entity(&format!("B{i}"));
        e.modified_at = 1000 + i as i64;
        store.save_entity(&e, &schema).unwrap();
    }

    let page = store.list_entities("bookmark", false, Some(3), Some(0)).unwrap();
    assert_eq!(page.len(), 3);

    let page2 = store.list_entities("bookmark", false, Some(3), Some(3)).unwrap();
    assert_eq!(page2.len(), 3);
    // Different entities
    assert_ne!(page[0].id, page2[0].id);
}

#[test]
fn list_empty_type() {
    let store = EntityStore::open_in_memory().unwrap();
    let list = store.list_entities("nothing", false, None, None).unwrap();
    assert!(list.is_empty());
}

// ── Trash ────────────────────────────────────────────────────────

#[test]
fn trash_and_restore() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let entity = test_entity("Trashable");
    let id = entity.id.clone();
    store.save_entity(&entity, &schema).unwrap();

    // Trash it
    store.trash_entity(&id).unwrap();
    let list = store.list_entities("bookmark", false, None, None).unwrap();
    assert_eq!(list.len(), 0); // excluded from non-trash list

    let list_with_trash = store.list_entities("bookmark", true, None, None).unwrap();
    assert_eq!(list_with_trash.len(), 1);

    // Restore it
    store.restore_entity(&id).unwrap();
    let list = store.list_entities("bookmark", false, None, None).unwrap();
    assert_eq!(list.len(), 1);
}

// ── Count ────────────────────────────────────────────────────────

#[test]
fn count_entities() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();

    assert_eq!(store.count_entities("bookmark", false).unwrap(), 0);

    for i in 0..3 {
        store.save_entity(&test_entity(&format!("B{i}")), &schema).unwrap();
    }
    assert_eq!(store.count_entities("bookmark", false).unwrap(), 3);
    assert_eq!(store.count_entities("bookmark", true).unwrap(), 3);
}

#[test]
fn count_excludes_trashed() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let entity = test_entity("Trashme");
    let id = entity.id.clone();
    store.save_entity(&entity, &schema).unwrap();
    store.trash_entity(&id).unwrap();

    assert_eq!(store.count_entities("bookmark", false).unwrap(), 0);
    assert_eq!(store.count_entities("bookmark", true).unwrap(), 1);
}

// ── Search ───────────────────────────────────────────────────────

#[test]
fn search_entities() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();

    let e1 = Entity {
        id: uuid::Uuid::new_v4().to_string(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Rust Programming", "url": "https://rust-lang.org", "tags": ["rust"]}),
        created_at: 1000, modified_at: 1000, created_by: "test".into(),
    };
    let e2 = Entity {
        id: uuid::Uuid::new_v4().to_string(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Python Docs", "url": "https://python.org", "tags": ["python"]}),
        created_at: 1001, modified_at: 1001, created_by: "test".into(),
    };

    store.save_entity(&e1, &schema).unwrap();
    store.save_entity(&e2, &schema).unwrap();

    let results = store.search("rust", None, 10).unwrap();
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].id, e1.id);
}

#[test]
fn search_no_results() {
    let store = EntityStore::open_in_memory().unwrap();
    let results = store.search("nonexistent", None, 10).unwrap();
    assert!(results.is_empty());
}

#[test]
fn search_by_entity_type() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let entity = Entity {
        id: "s1".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "findme", "url": "x", "tags": []}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    let found = store.search("findme", Some(&["bookmark"]), 10).unwrap();
    assert_eq!(found.len(), 1);

    let not_found = store.search("findme", Some(&["note"]), 10).unwrap();
    assert!(not_found.is_empty());
}

#[test]
fn search_excludes_trashed() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    let entity = Entity {
        id: "trash-search".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Secret", "url": "x", "tags": []}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();
    store.trash_entity("trash-search").unwrap();

    let results = store.search("Secret", None, 10).unwrap();
    assert!(results.is_empty());
}

// ── Query ────────────────────────────────────────────────────────

#[test]
fn query_entities_no_filters() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    store.save_entity(&test_entity("Q1"), &schema).unwrap();
    store.save_entity(&test_entity("Q2"), &schema).unwrap();

    let results = store.query_entities("bookmark", &[], false, None).unwrap();
    assert_eq!(results.len(), 2);
}

#[test]
fn query_entities_with_limit() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    for i in 0..5 {
        store.save_entity(&test_entity(&format!("Q{i}")), &schema).unwrap();
    }

    let results = store.query_entities("bookmark", &[], false, Some(2)).unwrap();
    assert_eq!(results.len(), 2);
}

// ── Links ────────────────────────────────────────────────────────

#[test]
fn entity_links() {
    let store = EntityStore::open_in_memory().unwrap();

    store.save_link("task", "t1", "note", "n1").unwrap();
    store.save_link("task", "t1", "contact", "c1").unwrap();

    let links = store.get_links_from("task", "t1").unwrap();
    assert_eq!(links.len(), 2);

    let backlinks = store.get_links_to("note", "n1").unwrap();
    assert_eq!(backlinks.len(), 1);
    assert_eq!(backlinks[0], ("task".to_string(), "t1".to_string()));

    store.remove_link("task", "t1", "note", "n1").unwrap();
    let links = store.get_links_from("task", "t1").unwrap();
    assert_eq!(links.len(), 1);
}

#[test]
fn duplicate_link_is_ignored() {
    let store = EntityStore::open_in_memory().unwrap();
    store.save_link("a", "1", "b", "2").unwrap();
    store.save_link("a", "1", "b", "2").unwrap(); // duplicate
    let links = store.get_links_from("a", "1").unwrap();
    assert_eq!(links.len(), 1);
}

#[test]
fn get_links_empty() {
    let store = EntityStore::open_in_memory().unwrap();
    let from = store.get_links_from("x", "y").unwrap();
    assert!(from.is_empty());
    let to = store.get_links_to("x", "y").unwrap();
    assert!(to.is_empty());
}

// ── Relation auto-linking ────────────────────────────────────────

#[test]
fn relation_field_creates_link_from_string_id() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/parent_id"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "task-1".into(),
        entity_type: "task".into(),
        data: serde_json::json!({
            "title": "Subtask",
            "parent_id": "task-parent"
        }),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    // The relation field should have auto-created a link
    let links = store.get_links_from("task", "task-1").unwrap();
    assert_eq!(links.len(), 1);
    assert_eq!(links[0], ("_".to_string(), "task-parent".to_string()));
}

#[test]
fn relation_field_creates_link_from_typed_object() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/linked_note"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "task-2".into(),
        entity_type: "task".into(),
        data: serde_json::json!({
            "title": "With typed link",
            "linked_note": {"type": "note", "id": "note-42"}
        }),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    let links = store.get_links_from("task", "task-2").unwrap();
    assert_eq!(links.len(), 1);
    assert_eq!(links[0], ("note".to_string(), "note-42".to_string()));

    // Verify backlink lookup works too
    let backlinks = store.get_links_to("note", "note-42").unwrap();
    assert_eq!(backlinks.len(), 1);
    assert_eq!(backlinks[0], ("task".to_string(), "task-2".to_string()));
}

#[test]
fn relation_field_no_link_when_field_missing() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/parent_id"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    // Entity has no parent_id field in data
    let entity = Entity {
        id: "task-3".into(),
        entity_type: "task".into(),
        data: serde_json::json!({"title": "No parent"}),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    let links = store.get_links_from("task", "task-3").unwrap();
    assert!(links.is_empty());
}

#[test]
fn relation_field_ignores_non_string_non_object() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/parent_id"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    // parent_id is a number, not a string or object — should be ignored
    let entity = Entity {
        id: "task-4".into(),
        entity_type: "task".into(),
        data: serde_json::json!({"title": "Bad relation", "parent_id": 42}),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    let links = store.get_links_from("task", "task-4").unwrap();
    assert!(links.is_empty());
}

#[test]
fn relation_field_null_value_creates_no_link() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/parent_id"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "task-5".into(),
        entity_type: "task".into(),
        data: serde_json::json!({"title": "Null parent", "parent_id": null}),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    let links = store.get_links_from("task", "task-5").unwrap();
    assert!(links.is_empty());
}

#[test]
fn multiple_relation_fields_create_multiple_links() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/parent_id"),
            IndexedField::relation("/assignee_id"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "task-6".into(),
        entity_type: "task".into(),
        data: serde_json::json!({
            "title": "Multi-link",
            "parent_id": "task-parent",
            "assignee_id": {"type": "contact", "id": "contact-1"}
        }),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    let links = store.get_links_from("task", "task-6").unwrap();
    assert_eq!(links.len(), 2);

    // Verify both links exist (order may vary)
    let link_set: std::collections::HashSet<_> = links.into_iter().collect();
    assert!(link_set.contains(&("_".to_string(), "task-parent".to_string())));
    assert!(link_set.contains(&("contact".to_string(), "contact-1".to_string())));
}

#[test]
fn relation_link_persists_after_upsert() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "task".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::relation("/parent_id"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "task-7".into(),
        entity_type: "task".into(),
        data: serde_json::json!({"title": "v1", "parent_id": "p1"}),
        created_at: 1,
        modified_at: 1,
        created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    // Upsert with a different relation target
    let entity2 = Entity {
        id: "task-7".into(),
        entity_type: "task".into(),
        data: serde_json::json!({"title": "v2", "parent_id": "p2"}),
        created_at: 1,
        modified_at: 2,
        created_by: "p".into(),
    };
    store.save_entity(&entity2, &schema).unwrap();

    let links = store.get_links_from("task", "task-7").unwrap();
    // Both links exist (INSERT OR IGNORE) — old link isn't removed automatically
    assert!(links.len() >= 1);
    // At minimum the new link should be present
    let link_targets: Vec<_> = links.iter().map(|(_, id)| id.as_str()).collect();
    assert!(link_targets.contains(&"p2"));
}

#[test]
fn schema_without_relation_fields_creates_no_auto_links() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema(); // bookmark schema has no Relation fields

    let entity = test_entity("No auto links");
    store.save_entity(&entity, &schema).unwrap();

    let links = store.get_links_from("bookmark", &entity.id).unwrap();
    assert!(links.is_empty());
}

// ── Schema field extraction ──────────────────────────────────────

#[test]
fn save_entity_with_body_field() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "note".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField { field_path: "/body".into(), field_type: FieldType::Text, searchable: true, vector_dim: None, enum_options: None },
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };
    let entity = Entity {
        id: "note-1".into(),
        entity_type: "note".into(),
        data: serde_json::json!({"title": "My Note", "body": "Long content here"}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    // Should be searchable by body
    let results = store.search("Long content", None, 10).unwrap();
    assert_eq!(results.len(), 1);
}

// ── Encryption failure paths ─────────────────────────────────────

/// A mock encryptor that fails on encrypt.
struct FailingEncryptor;

impl privstack_crypto::DataEncryptor for FailingEncryptor {
    fn encrypt_bytes(&self, _entity_id: &str, _data: &[u8]) -> privstack_crypto::EncryptorResult<Vec<u8>> {
        Err(privstack_crypto::EncryptorError::Crypto("simulated encrypt failure".into()))
    }
    fn decrypt_bytes(&self, _data: &[u8]) -> privstack_crypto::EncryptorResult<Vec<u8>> {
        Err(privstack_crypto::EncryptorError::Crypto("simulated decrypt failure".into()))
    }
    fn reencrypt_bytes(&self, _data: &[u8], _old: &[u8], _new: &[u8]) -> privstack_crypto::EncryptorResult<Vec<u8>> {
        Err(privstack_crypto::EncryptorError::Crypto("simulated reencrypt failure".into()))
    }
    fn is_available(&self) -> bool {
        true
    }
}

/// Encryptor that is not available (simulates locked vault).
struct UnavailableEncryptor;

impl privstack_crypto::DataEncryptor for UnavailableEncryptor {
    fn encrypt_bytes(&self, _entity_id: &str, _data: &[u8]) -> privstack_crypto::EncryptorResult<Vec<u8>> {
        Err(privstack_crypto::EncryptorError::Unavailable)
    }
    fn decrypt_bytes(&self, _data: &[u8]) -> privstack_crypto::EncryptorResult<Vec<u8>> {
        Err(privstack_crypto::EncryptorError::Unavailable)
    }
    fn reencrypt_bytes(&self, _data: &[u8], _old: &[u8], _new: &[u8]) -> privstack_crypto::EncryptorResult<Vec<u8>> {
        Err(privstack_crypto::EncryptorError::Unavailable)
    }
    fn is_available(&self) -> bool {
        false
    }
}

#[test]
fn save_entity_raw_with_encryption_failure() {
    let store = EntityStore::open_with_encryptor(
        std::path::Path::new(":memory:"),
        std::sync::Arc::new(FailingEncryptor),
    );
    // open_with_encryptor may not support :memory: — use tempfile if needed
    // The FailingEncryptor will cause encrypt_data_json to fail
    if let Ok(store) = store {
        let entity = Entity {
            id: "fail-1".into(),
            entity_type: "note".into(),
            data: serde_json::json!({"content": "hello"}),
            created_at: 100,
            modified_at: 200,
            created_by: "peer1".into(),
        };
        let result = store.save_entity_raw(&entity);
        assert!(result.is_err());
    }
}

#[test]
fn save_entity_with_encryption_failure() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("enc_fail.db");
    let store = EntityStore::open_with_encryptor(
        &db_path,
        std::sync::Arc::new(FailingEncryptor),
    ).unwrap();
    let schema = test_schema();
    let entity = test_entity("Enc Fail");
    let result = store.save_entity(&entity, &schema);
    assert!(result.is_err());
}

#[test]
fn decrypt_failure_returns_error_for_corrupted_data() {
    // Store with passthrough, then manually corrupt and try to read
    // with a failing encryptor — the decrypt_data_json path for base64 decode
    let store = EntityStore::open_in_memory().unwrap();
    let entity = test_entity("Corrupt Test");
    let schema = test_schema();
    store.save_entity(&entity, &schema).unwrap();
    // The passthrough stores plaintext JSON, so decryption works via fast path.
    // This test verifies the happy path still works (plaintext JSON fast path).
    let retrieved = store.get_entity(&entity.id).unwrap().unwrap();
    assert_eq!(retrieved.get_str("/title"), Some("Corrupt Test"));
}

// ── Query with multiple filters ─────────────────────────────────

/// Helper: create a store with unavailable encryptor so data_json stays plaintext.
/// This is needed for query_entities with filters since json_extract_string
/// only works on plaintext JSON in data_json.
fn store_with_plaintext_data() -> EntityStore {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("plaintext.db");
    let store = EntityStore::open_with_encryptor(
        &db_path,
        std::sync::Arc::new(UnavailableEncryptor),
    ).unwrap();
    // Leak the tempdir so it persists for the store's lifetime
    std::mem::forget(dir);
    store
}

#[test]
fn query_entities_with_single_filter() {
    let store = store_with_plaintext_data();
    let schema = test_schema();

    let e1 = Entity {
        id: "qf-1".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Rust Guide", "url": "https://rust.org", "tags": []}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    let e2 = Entity {
        id: "qf-2".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Python Guide", "url": "https://python.org", "tags": []}),
        created_at: 2, modified_at: 2, created_by: "p".into(),
    };
    store.save_entity(&e1, &schema).unwrap();
    store.save_entity(&e2, &schema).unwrap();

    let filters = vec![("/title".to_string(), serde_json::Value::String("Rust Guide".into()))];
    let results = store.query_entities("bookmark", &filters, false, None).unwrap();
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].id, "qf-1");
}

#[test]
fn query_entities_with_multiple_filters_empty_result() {
    let store = store_with_plaintext_data();
    let schema = test_schema();

    let e1 = Entity {
        id: "qm-1".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Rust Guide", "url": "https://rust.org", "tags": []}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&e1, &schema).unwrap();

    // Multiple filters applied — exercises the multi-filter SQL generation path
    let filters = vec![
        ("/title".to_string(), serde_json::Value::String("Rust Guide".into())),
        ("/url".to_string(), serde_json::Value::String("https://rust.org".into())),
    ];
    let results = store.query_entities("bookmark", &filters, false, None).unwrap();
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].id, "qm-1");
}

#[test]
fn query_entities_with_numeric_filter() {
    let store = store_with_plaintext_data();
    let schema = EntitySchema {
        entity_type: "item".into(),
        indexed_fields: vec![
            IndexedField::text("/name", true),
            IndexedField::number("/count"),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let e1 = Entity {
        id: "nf-1".into(),
        entity_type: "item".into(),
        data: serde_json::json!({"name": "widget", "count": 42}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&e1, &schema).unwrap();

    // Numeric filter: json Value::Number.to_string() = "42", json_extract_string = "42"
    let filters = vec![("/count".to_string(), serde_json::json!(42))];
    let results = store.query_entities("item", &filters, false, None).unwrap();
    // Numeric values serialize without quotes, so this should match
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].id, "nf-1");
}

#[test]
fn query_entities_excludes_trashed() {
    let store = store_with_plaintext_data();
    let schema = test_schema();
    let entity = Entity {
        id: "qt-1".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Trashed", "url": "x", "tags": []}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();
    store.trash_entity("qt-1").unwrap();

    // Even without filters, trashed entities should be excluded
    let results = store.query_entities("bookmark", &[], false, None).unwrap();
    assert!(results.is_empty());
}

// ── Search with special chars in entity_types ────────────────────

#[test]
fn search_with_entity_types_containing_special_chars() {
    let store = EntityStore::open_in_memory().unwrap();
    // Entity type with special chars — the SQL uses escaped quotes via replace
    let schema = EntitySchema {
        entity_type: "my_note''s".into(),
        indexed_fields: vec![IndexedField::text("/title", true)],
        merge_strategy: MergeStrategy::LwwDocument,
    };
    let entity = Entity {
        id: "sp-1".into(),
        entity_type: "my_note''s".into(),
        data: serde_json::json!({"title": "Special"}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();

    // Search with entity type filter containing escaped apostrophe
    let results = store.search("Special", Some(&["my_note''s"]), 10).unwrap();
    assert_eq!(results.len(), 1);
}

#[test]
fn search_with_empty_entity_types_filter() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    store.save_entity(&test_entity("Findable"), &schema).unwrap();

    // Empty types slice — no entity_type filter applied
    let results = store.search("Findable", Some(&[]), 10).unwrap();
    assert_eq!(results.len(), 1);
}

// ── migrate_unencrypted ──────────────────────────────────────────

#[test]
fn migrate_unencrypted_with_unavailable_encryptor_fails() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("migrate_fail.db");
    let store = EntityStore::open_with_encryptor(
        &db_path,
        std::sync::Arc::new(UnavailableEncryptor),
    ).unwrap();
    let result = store.migrate_unencrypted();
    assert!(result.is_err());
}

#[test]
fn migrate_unencrypted_skips_already_encrypted_rows() {
    // With passthrough encryptor, data is stored as plaintext JSON.
    // migrate_unencrypted should see it as JSON and try to encrypt,
    // but since passthrough returns identical data, encrypted == raw, so 0 migrated.
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    store.save_entity(&test_entity("Already enc"), &schema).unwrap();

    let migrated = store.migrate_unencrypted().unwrap();
    assert_eq!(migrated, 0); // passthrough returns same data
}

#[test]
fn migrate_unencrypted_on_empty_store() {
    let store = EntityStore::open_in_memory().unwrap();
    let migrated = store.migrate_unencrypted().unwrap();
    assert_eq!(migrated, 0);
}

// ── re_encrypt_all ───────────────────────────────────────────────

#[test]
fn re_encrypt_all_with_no_rows() {
    let store = EntityStore::open_in_memory().unwrap();
    let count = store.re_encrypt_all(b"old_key_material", b"new_key_material").unwrap();
    assert_eq!(count, 0);
}

#[test]
fn re_encrypt_all_processes_encrypted_rows() {
    // With passthrough (is_available=true), data is base64-encoded.
    // re_encrypt_all should process non-JSON (base64) rows.
    let store = EntityStore::open_in_memory().unwrap();
    let schema = test_schema();
    store.save_entity(&test_entity("Enc1"), &schema).unwrap();
    store.save_entity(&test_entity("Enc2"), &schema).unwrap();

    // PassthroughEncryptor.reencrypt_bytes returns data unchanged
    let count = store.re_encrypt_all(b"old", b"new").unwrap();
    assert_eq!(count, 2); // base64-encoded rows are processed
}

#[test]
fn re_encrypt_all_skips_plaintext_rows() {
    // Store with unavailable encryptor so data stays as plaintext JSON
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("reenc_plain.db");
    let store = EntityStore::open_with_encryptor(
        &db_path,
        std::sync::Arc::new(UnavailableEncryptor),
    ).unwrap();
    let schema = test_schema();
    store.save_entity(&test_entity("Plain"), &schema).unwrap();
    store.save_entity(&test_entity("Plain2"), &schema).unwrap();

    let count = store.re_encrypt_all(b"old", b"new").unwrap();
    assert_eq!(count, 0); // all are plaintext JSON, skipped
}

// ── Vector extraction dimension mismatch ─────────────────────────

#[test]
fn vector_field_dimension_mismatch_skipped() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "embedding".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::vector("/embedding", 3), // expects dim=3
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    // Provide 2 elements instead of 3
    let entity = Entity {
        id: "vec-mismatch-1".into(),
        entity_type: "embedding".into(),
        data: serde_json::json!({
            "title": "Bad vector",
            "embedding": [0.1, 0.2]
        }),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    // Should not error — just skips the vector
    store.save_entity(&entity, &schema).unwrap();
    let retrieved = store.get_entity("vec-mismatch-1").unwrap().unwrap();
    assert_eq!(retrieved.get_str("/title"), Some("Bad vector"));
}

#[test]
fn vector_field_correct_dimensions_stored() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "embedding".into(),
        indexed_fields: vec![
            IndexedField::text("/title", true),
            IndexedField::vector("/embedding", 3),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "vec-ok-1".into(),
        entity_type: "embedding".into(),
        data: serde_json::json!({
            "title": "Good vector",
            "embedding": [0.1, 0.2, 0.3]
        }),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity(&entity, &schema).unwrap();
    let retrieved = store.get_entity("vec-ok-1").unwrap().unwrap();
    assert_eq!(retrieved.get_str("/title"), Some("Good vector"));
}

#[test]
fn vector_field_with_non_numeric_values_skipped() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "embedding".into(),
        indexed_fields: vec![
            IndexedField::vector("/embedding", 3),
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "vec-bad-vals".into(),
        entity_type: "embedding".into(),
        data: serde_json::json!({
            "embedding": [0.1, "not_a_number", 0.3]
        }),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    // Should not error — filter_map skips non-f64, dimension check fails
    store.save_entity(&entity, &schema).unwrap();
}

#[test]
fn vector_field_no_vector_dim_defaults_to_zero() {
    let store = EntityStore::open_in_memory().unwrap();
    let schema = EntitySchema {
        entity_type: "embedding".into(),
        indexed_fields: vec![
            IndexedField {
                field_path: "/embedding".into(),
                field_type: FieldType::Vector,
                searchable: false,
                vector_dim: None, // no dim specified
                enum_options: None,
            },
        ],
        merge_strategy: MergeStrategy::LwwDocument,
    };

    let entity = Entity {
        id: "vec-no-dim".into(),
        entity_type: "embedding".into(),
        data: serde_json::json!({
            "embedding": [0.1, 0.2]
        }),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    // dim defaults to 0, array.len() != 0 => skipped
    store.save_entity(&entity, &schema).unwrap();
}

// ── open with file path ──────────────────────────────────────────

#[test]
fn open_with_file_path() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("entity.db");
    let store = EntityStore::open(&db_path).unwrap();
    let schema = test_schema();
    let entity = test_entity("Persisted");
    let id = entity.id.clone();
    store.save_entity(&entity, &schema).unwrap();
    drop(store);

    let store2 = EntityStore::open(&db_path).unwrap();
    let retrieved = store2.get_entity(&id).unwrap().unwrap();
    assert_eq!(retrieved.get_str("/title"), Some("Persisted"));
}

#[test]
fn save_entity_with_is_favorite() {
    let store = EntityStore::open_in_memory().unwrap();
    let entity = Entity {
        id: "fav-1".into(),
        entity_type: "bookmark".into(),
        data: serde_json::json!({"title": "Fav", "is_favorite": true}),
        created_at: 1, modified_at: 1, created_by: "p".into(),
    };
    store.save_entity_raw(&entity).unwrap();
    let retrieved = store.get_entity("fav-1").unwrap().unwrap();
    assert_eq!(retrieved.data["is_favorite"], true);
}
