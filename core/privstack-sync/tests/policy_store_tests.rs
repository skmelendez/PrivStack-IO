//! Tests for policy_store.rs — persistent storage for policy state.

use privstack_sync::policy::{AuditAction, AuditDecision, AuditEntry, SyncRole};
use privstack_sync::policy_store::PolicyStore;
use privstack_types::{EntityId, PeerId};

// ── Empty database loads ────────────────────────────────────────

#[test]
fn load_acls_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let acls = store.load_acls().unwrap();
    assert!(acls.is_empty());
}

#[test]
fn load_default_roles_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let roles = store.load_default_roles().unwrap();
    assert!(roles.is_empty());
}

#[test]
fn load_team_roles_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let roles = store.load_team_roles().unwrap();
    assert!(roles.is_empty());
}

#[test]
fn load_teams_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let teams = store.load_teams().unwrap();
    assert!(teams.is_empty());
}

#[test]
fn load_device_limits_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let limits = store.load_device_limits().unwrap();
    assert!(limits.is_empty());
}

#[test]
fn load_known_peers_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peers = store.load_known_peers().unwrap();
    assert!(peers.is_empty());
}

#[test]
fn load_active_devices_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let devices = store.load_active_devices().unwrap();
    assert!(devices.is_empty());
}

#[test]
fn audit_log_count_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let count = store.audit_log_count().unwrap();
    assert_eq!(count, 0);
}

#[test]
fn load_audit_log_empty_db() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entries = store.load_audit_log(100, 0).unwrap();
    assert!(entries.is_empty());
}

// ── ACL save/load roundtrips ────────────────────────────────────

#[test]
fn save_and_load_acl() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();
    let peer = PeerId::new();

    store.save_acl(&entity, &peer, SyncRole::Editor).unwrap();

    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 1);
    assert_eq!(acls[0].0, entity);
    assert_eq!(acls[0].1, peer);
    assert_eq!(acls[0].2, SyncRole::Editor);
}

#[test]
fn save_acl_upsert() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();
    let peer = PeerId::new();

    store.save_acl(&entity, &peer, SyncRole::Viewer).unwrap();
    store.save_acl(&entity, &peer, SyncRole::Admin).unwrap();

    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 1);
    assert_eq!(acls[0].2, SyncRole::Admin);
}

#[test]
fn remove_acl() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();
    let peer = PeerId::new();

    store.save_acl(&entity, &peer, SyncRole::Editor).unwrap();
    store.remove_acl(&entity, &peer).unwrap();

    let acls = store.load_acls().unwrap();
    assert!(acls.is_empty());
}

// ── Default role save/load ──────────────────────────────────────

#[test]
fn save_and_load_default_role() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();

    store.save_default_role(&entity, SyncRole::Viewer).unwrap();

    let roles = store.load_default_roles().unwrap();
    assert_eq!(roles.len(), 1);
    assert_eq!(roles[0].0, entity);
    assert_eq!(roles[0].1, SyncRole::Viewer);
}

#[test]
fn remove_default_role() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();

    store.save_default_role(&entity, SyncRole::Editor).unwrap();
    store.remove_default_role(&entity).unwrap();

    let roles = store.load_default_roles().unwrap();
    assert!(roles.is_empty());
}

// ── Team role save/load ─────────────────────────────────────────

#[test]
fn save_and_load_team_role() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();
    let team_id = uuid::Uuid::new_v4().to_string();

    store.save_team_role(&entity, &team_id, SyncRole::Admin).unwrap();

    let roles = store.load_team_roles().unwrap();
    assert_eq!(roles.len(), 1);
    assert_eq!(roles[0].0, entity);
    assert_eq!(roles[0].1, team_id);
    assert_eq!(roles[0].2, SyncRole::Admin);
}

#[test]
fn remove_team_role() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();
    let team_id = uuid::Uuid::new_v4().to_string();

    store.save_team_role(&entity, &team_id, SyncRole::Editor).unwrap();
    store.remove_team_role(&entity, &team_id).unwrap();

    let roles = store.load_team_roles().unwrap();
    assert!(roles.is_empty());
}

// ── Team membership save/load ───────────────────────────────────

#[test]
fn save_and_load_team_member() {
    let store = PolicyStore::open_in_memory().unwrap();
    let team_id = uuid::Uuid::new_v4().to_string();
    let peer = PeerId::new();

    store.save_team_member(&team_id, &peer).unwrap();

    let teams = store.load_teams().unwrap();
    assert_eq!(teams.len(), 1);
    assert_eq!(teams[0].0, team_id);
    assert_eq!(teams[0].1, peer);
}

#[test]
fn remove_team_member() {
    let store = PolicyStore::open_in_memory().unwrap();
    let team_id = uuid::Uuid::new_v4().to_string();
    let peer = PeerId::new();

    store.save_team_member(&team_id, &peer).unwrap();
    store.remove_team_member(&team_id, &peer).unwrap();

    let teams = store.load_teams().unwrap();
    assert!(teams.is_empty());
}

#[test]
fn save_team_member_duplicate_ignored() {
    let store = PolicyStore::open_in_memory().unwrap();
    let team_id = uuid::Uuid::new_v4().to_string();
    let peer = PeerId::new();

    store.save_team_member(&team_id, &peer).unwrap();
    store.save_team_member(&team_id, &peer).unwrap();

    let teams = store.load_teams().unwrap();
    assert_eq!(teams.len(), 1);
}

// ── Device limits save/load ─────────────────────────────────────

#[test]
fn save_and_load_device_limit() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    store.save_device_limit(&peer, 5).unwrap();

    let limits = store.load_device_limits().unwrap();
    assert_eq!(limits.len(), 1);
    assert_eq!(limits[0].0, peer);
    assert_eq!(limits[0].1, 5);
}

#[test]
fn save_device_limit_upsert() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    store.save_device_limit(&peer, 3).unwrap();
    store.save_device_limit(&peer, 10).unwrap();

    let limits = store.load_device_limits().unwrap();
    assert_eq!(limits.len(), 1);
    assert_eq!(limits[0].1, 10);
}

// ── Known peers save/load ───────────────────────────────────────

#[test]
fn save_and_load_known_peer() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    store.save_known_peer(&peer).unwrap();

    let peers = store.load_known_peers().unwrap();
    assert_eq!(peers.len(), 1);
    assert_eq!(peers[0], peer);
}

#[test]
fn remove_known_peer() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    store.save_known_peer(&peer).unwrap();
    store.remove_known_peer(&peer).unwrap();

    let peers = store.load_known_peers().unwrap();
    assert!(peers.is_empty());
}

#[test]
fn save_known_peer_duplicate_ignored() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    store.save_known_peer(&peer).unwrap();
    store.save_known_peer(&peer).unwrap();

    let peers = store.load_known_peers().unwrap();
    assert_eq!(peers.len(), 1);
}

// ── Active devices save/load ────────────────────────────────────

#[test]
fn save_and_load_active_devices() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();
    let devices = vec!["device-1".to_string(), "device-2".to_string()];

    store.save_active_devices(&peer, &devices).unwrap();

    let loaded = store.load_active_devices().unwrap();
    assert_eq!(loaded.len(), 2);
    for (p, _) in &loaded {
        assert_eq!(*p, peer);
    }
}

#[test]
fn save_active_devices_empty_list() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    // First save some devices
    store.save_active_devices(&peer, &["d1".to_string()]).unwrap();
    // Then save empty list (clears them)
    store.save_active_devices(&peer, &[]).unwrap();

    let loaded = store.load_active_devices().unwrap();
    assert!(loaded.is_empty());
}

#[test]
fn save_active_devices_replaces_previous() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    store.save_active_devices(&peer, &["old-1".to_string(), "old-2".to_string()]).unwrap();
    store.save_active_devices(&peer, &["new-1".to_string()]).unwrap();

    let loaded = store.load_active_devices().unwrap();
    assert_eq!(loaded.len(), 1);
    assert_eq!(loaded[0].1, "new-1");
}

// ── Audit log save/load ─────────────────────────────────────────

#[test]
fn save_and_load_audit_entry() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();
    let entity = EntityId::new();

    let entry = AuditEntry {
        peer,
        entity: Some(entity),
        action: AuditAction::Handshake,
        decision: AuditDecision::Allowed,
        detail: "test handshake".to_string(),
        timestamp: std::time::SystemTime::now(),
    };

    store.save_audit_entry(&entry).unwrap();

    let entries = store.load_audit_log(100, 0).unwrap();
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].peer, peer);
    assert_eq!(entries[0].entity, Some(entity));
    assert_eq!(entries[0].action, AuditAction::Handshake);
    assert_eq!(entries[0].decision, AuditDecision::Allowed);
    assert_eq!(entries[0].detail, "test handshake");
}

#[test]
fn audit_log_count_increments() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    for i in 0..5 {
        let entry = AuditEntry {
            peer,
            entity: None,
            action: AuditAction::SyncRequest,
            decision: AuditDecision::Denied,
            detail: format!("entry {i}"),
            timestamp: std::time::SystemTime::now(),
        };
        store.save_audit_entry(&entry).unwrap();
    }

    assert_eq!(store.audit_log_count().unwrap(), 5);
}

#[test]
fn audit_log_pagination_limit() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    for i in 0..10 {
        let entry = AuditEntry {
            peer,
            entity: None,
            action: AuditAction::EventSend,
            decision: AuditDecision::Allowed,
            detail: format!("entry {i}"),
            timestamp: std::time::SystemTime::now(),
        };
        store.save_audit_entry(&entry).unwrap();
    }

    let page = store.load_audit_log(3, 0).unwrap();
    assert_eq!(page.len(), 3);
}

#[test]
fn audit_log_pagination_offset() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    for i in 0..10 {
        let entry = AuditEntry {
            peer,
            entity: None,
            action: AuditAction::EventReceive,
            decision: AuditDecision::Filtered,
            detail: format!("entry {i}"),
            timestamp: std::time::SystemTime::now(),
        };
        store.save_audit_entry(&entry).unwrap();
    }

    // Offset past all entries
    let page = store.load_audit_log(10, 10).unwrap();
    assert!(page.is_empty());
}

#[test]
fn audit_log_pagination_offset_beyond_count() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    for i in 0..3 {
        let entry = AuditEntry {
            peer,
            entity: None,
            action: AuditAction::DeviceRegister,
            decision: AuditDecision::Denied,
            detail: format!("entry {i}"),
            timestamp: std::time::SystemTime::now(),
        };
        store.save_audit_entry(&entry).unwrap();
    }

    let page = store.load_audit_log(100, 999).unwrap();
    assert!(page.is_empty());
}

#[test]
fn audit_entry_with_none_entity() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    let entry = AuditEntry {
        peer,
        entity: None,
        action: AuditAction::Handshake,
        decision: AuditDecision::Allowed,
        detail: "no entity".to_string(),
        timestamp: std::time::SystemTime::now(),
    };

    store.save_audit_entry(&entry).unwrap();

    let entries = store.load_audit_log(10, 0).unwrap();
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].entity, None);
}

// ── Full policy load from store ─────────────────────────────────

#[tokio::test]
async fn enterprise_policy_load_from_store() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());

    let entity = EntityId::new();
    let peer = PeerId::new();
    let team_id = uuid::Uuid::new_v4().to_string();

    store.save_acl(&entity, &peer, SyncRole::Editor).unwrap();
    store.save_default_role(&entity, SyncRole::Viewer).unwrap();
    store.save_team_role(&entity, &team_id, SyncRole::Admin).unwrap();
    store.save_team_member(&team_id, &peer).unwrap();
    store.save_device_limit(&peer, 3).unwrap();
    store.save_known_peer(&peer).unwrap();
    let device_id = uuid::Uuid::new_v4().to_string();
    store.save_active_devices(&peer, &[device_id.clone()]).unwrap();

    let policy = EnterpriseSyncPolicy::load(store).await.unwrap();

    // Verify peer ACL
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));

    // Verify known peers
    assert!(policy.known_peers.read().await.contains(&peer));

    // Verify device limits
    assert_eq!(*policy.device_limits.read().await.get(&peer).unwrap(), 3);
}

// ── Multiple entries ────────────────────────────────────────────

#[test]
fn multiple_acls_different_entities() {
    let store = PolicyStore::open_in_memory().unwrap();
    let e1 = EntityId::new();
    let e2 = EntityId::new();
    let p1 = PeerId::new();
    let p2 = PeerId::new();

    store.save_acl(&e1, &p1, SyncRole::Owner).unwrap();
    store.save_acl(&e2, &p2, SyncRole::Viewer).unwrap();

    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 2);
}

// ── All SyncRole variants roundtrip through store ───────────────

#[test]
fn save_and_load_all_role_variants() {
    let store = PolicyStore::open_in_memory().unwrap();

    let roles = vec![SyncRole::Viewer, SyncRole::Editor, SyncRole::Admin, SyncRole::Owner];
    for role in &roles {
        let entity = EntityId::new();
        let peer = PeerId::new();
        store.save_acl(&entity, &peer, *role).unwrap();
    }

    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 4);
    let loaded_roles: Vec<SyncRole> = acls.iter().map(|a| a.2).collect();
    assert!(loaded_roles.contains(&SyncRole::Viewer));
    assert!(loaded_roles.contains(&SyncRole::Editor));
    assert!(loaded_roles.contains(&SyncRole::Admin));
    assert!(loaded_roles.contains(&SyncRole::Owner));
}

// ── All AuditAction and AuditDecision variants roundtrip ────────

#[test]
fn save_and_load_all_audit_actions() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    let actions = vec![
        AuditAction::Handshake,
        AuditAction::SyncRequest,
        AuditAction::EventSend,
        AuditAction::EventReceive,
        AuditAction::DeviceRegister,
    ];

    for action in &actions {
        let entry = AuditEntry {
            peer,
            entity: None,
            action: action.clone(),
            decision: AuditDecision::Allowed,
            detail: format!("testing {:?}", action),
            timestamp: std::time::SystemTime::now(),
        };
        store.save_audit_entry(&entry).unwrap();
    }

    let entries = store.load_audit_log(100, 0).unwrap();
    assert_eq!(entries.len(), 5);
}

#[test]
fn save_and_load_all_audit_decisions() {
    let store = PolicyStore::open_in_memory().unwrap();
    let peer = PeerId::new();

    let decisions = vec![
        AuditDecision::Allowed,
        AuditDecision::Denied,
        AuditDecision::Filtered,
    ];

    for decision in &decisions {
        let entry = AuditEntry {
            peer,
            entity: Some(EntityId::new()),
            action: AuditAction::EventReceive,
            decision: decision.clone(),
            detail: format!("testing {:?}", decision),
            timestamp: std::time::SystemTime::now(),
        };
        store.save_audit_entry(&entry).unwrap();
    }

    let entries = store.load_audit_log(100, 0).unwrap();
    assert_eq!(entries.len(), 3);
    // Verify decisions roundtrip correctly
    let loaded_decisions: std::collections::HashSet<String> = entries
        .iter()
        .map(|e| format!("{:?}", e.decision))
        .collect();
    assert!(loaded_decisions.contains("Allowed"));
    assert!(loaded_decisions.contains("Denied"));
    assert!(loaded_decisions.contains("Filtered"));
}

// ── Default role roundtrip with all role variants ───────────────

#[test]
fn save_default_role_all_variants() {
    let store = PolicyStore::open_in_memory().unwrap();

    for role in &[SyncRole::Viewer, SyncRole::Editor, SyncRole::Admin, SyncRole::Owner] {
        let entity = EntityId::new();
        store.save_default_role(&entity, *role).unwrap();
    }

    let roles = store.load_default_roles().unwrap();
    assert_eq!(roles.len(), 4);
}

// ── Team role roundtrip with all role variants ──────────────────

#[test]
fn save_team_role_all_variants() {
    let store = PolicyStore::open_in_memory().unwrap();
    let entity = EntityId::new();

    for role in &[SyncRole::Viewer, SyncRole::Editor, SyncRole::Admin, SyncRole::Owner] {
        let team_id = uuid::Uuid::new_v4().to_string();
        store.save_team_role(&entity, &team_id, *role).unwrap();
    }

    let roles = store.load_team_roles().unwrap();
    assert_eq!(roles.len(), 4);
}

// ── File-based store ────────────────────────────────────────────

#[test]
fn file_based_store_creates_and_operates() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("test_policy.db");
    let store = PolicyStore::new(path.to_str().unwrap()).unwrap();

    let entity = EntityId::new();
    let peer = PeerId::new();
    store.save_acl(&entity, &peer, SyncRole::Editor).unwrap();

    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 1);
}

// ── Multiple teams per peer ─────────────────────────────────────

// ── Trait impl coverage for policy types ─────────────────────────

#[test]
fn team_id_debug_display_clone_eq_hash() {
    use privstack_sync::policy::TeamId;
    use std::collections::HashSet;

    let t1 = TeamId::new();
    let t2 = TeamId::default();

    // Debug
    let _ = format!("{:?}", t1);
    // Display
    let _ = format!("{}", t1);
    // Clone + Copy
    let t1_clone = t1;
    let _ = t1_clone;
    // PartialEq + Eq
    assert_eq!(t1, t1);
    assert_ne!(t1, t2);
    // Hash
    let mut set = HashSet::new();
    set.insert(t1);
    set.insert(t2);
    assert_eq!(set.len(), 2);
}

#[test]
fn device_id_debug_display_clone_eq_hash() {
    use privstack_sync::policy::DeviceId;
    use std::collections::HashSet;

    let d1 = DeviceId::new();
    let d2 = DeviceId::default();

    let _ = format!("{:?}", d1);
    let _ = format!("{}", d1);
    let d1_clone = d1;
    let _ = d1_clone;
    assert_eq!(d1, d1);
    assert_ne!(d1, d2);
    let mut set = HashSet::new();
    set.insert(d1);
    set.insert(d2);
    assert_eq!(set.len(), 2);
}

#[test]
fn sync_role_debug_display_clone_ord_hash() {
    use std::collections::HashSet;

    let roles = [SyncRole::Viewer, SyncRole::Editor, SyncRole::Admin, SyncRole::Owner];
    for r in &roles {
        let _ = format!("{:?}", r);
        let _ = format!("{}", r);
        let clone = *r;
        assert_eq!(*r, clone);
    }
    // Ord
    assert!(SyncRole::Viewer < SyncRole::Editor);
    assert!(SyncRole::Editor < SyncRole::Admin);
    assert!(SyncRole::Admin < SyncRole::Owner);
    // Hash
    let set: HashSet<SyncRole> = roles.iter().copied().collect();
    assert_eq!(set.len(), 4);
}

#[test]
fn audit_action_debug_display_clone_eq() {
    let actions = [
        AuditAction::Handshake,
        AuditAction::SyncRequest,
        AuditAction::EventSend,
        AuditAction::EventReceive,
        AuditAction::DeviceRegister,
    ];
    for a in &actions {
        let _ = format!("{:?}", a);
        let _ = format!("{}", a);
        let clone = a.clone();
        assert_eq!(*a, clone);
    }
    assert_ne!(AuditAction::Handshake, AuditAction::SyncRequest);
}

#[test]
fn audit_decision_debug_clone_eq() {
    let decisions = [AuditDecision::Allowed, AuditDecision::Denied, AuditDecision::Filtered];
    for d in &decisions {
        let _ = format!("{:?}", d);
        let clone = d.clone();
        assert_eq!(*d, clone);
    }
    assert_ne!(AuditDecision::Allowed, AuditDecision::Denied);
}

#[test]
fn audit_entry_debug_clone() {
    let peer = PeerId::new();
    let entry = AuditEntry {
        peer,
        entity: Some(EntityId::new()),
        action: AuditAction::Handshake,
        decision: AuditDecision::Allowed,
        detail: "test".to_string(),
        timestamp: std::time::SystemTime::now(),
    };
    let _ = format!("{:?}", entry);
    let clone = entry.clone();
    assert_eq!(clone.peer, peer);
    assert_eq!(clone.detail, "test");
}

#[test]
fn entity_acl_new_and_builders() {
    use privstack_sync::policy::{EntityAcl, TeamId};

    let entity = EntityId::new();
    let peer = PeerId::new();
    let team = TeamId::new();

    let acl = EntityAcl::new(entity)
        .with_default_role(SyncRole::Viewer)
        .with_peer_role(peer, SyncRole::Editor)
        .with_team_role(team, SyncRole::Admin);

    assert_eq!(acl.entity_id, entity);
    assert_eq!(acl.default_role, Some(SyncRole::Viewer));
    assert_eq!(acl.peer_roles.get(&peer), Some(&SyncRole::Editor));
    assert_eq!(acl.team_roles.get(&team), Some(&SyncRole::Admin));
}

#[test]
fn enterprise_sync_policy_debug_default() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let p = EnterpriseSyncPolicy::default();
    let _ = format!("{:?}", p);
}

#[test]
fn enterprise_sync_policy_builder_methods() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new()
        .with_store(store.clone())
        .with_max_in_memory_log(500);

    assert!(policy.store().is_some());
    let _ = format!("{:?}", policy);
}

#[test]
fn personal_sync_policy_debug_default() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let p = PersonalSyncPolicy::default();
    let _ = format!("{:?}", p);
}

// ── EnterpriseSyncPolicy async methods ──────────────────────────

#[tokio::test]
async fn enterprise_policy_grant_revoke_peer_role() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store);

    let entity = EntityId::new();
    let peer = PeerId::new();

    policy.grant_peer_role(entity, peer, SyncRole::Editor).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));

    policy.revoke_peer_role(entity, peer).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, None);
}

#[tokio::test]
async fn enterprise_policy_grant_revoke_team_role() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, TeamId};

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store);

    let entity = EntityId::new();
    let team = TeamId::new();
    let peer = PeerId::new();

    policy.grant_team_role(entity, team, SyncRole::Admin).await;
    policy.add_team_member(team, peer).await;

    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Admin));

    policy.revoke_team_role(entity, team).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, None);
}

#[tokio::test]
async fn enterprise_policy_set_default_role() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store);

    let entity = EntityId::new();
    let peer = PeerId::new();

    policy.set_default_role(entity, Some(SyncRole::Viewer)).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Viewer));

    policy.set_default_role(entity, None).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, None);
}

#[tokio::test]
async fn enterprise_policy_add_remove_team_member() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, TeamId};

    let policy = EnterpriseSyncPolicy::new();
    let team = TeamId::new();
    let peer = PeerId::new();

    policy.add_team_member(team, peer).await;
    assert!(policy.teams.read().await.get(&team).unwrap().contains(&peer));

    policy.remove_team_member(team, peer).await;
    assert!(!policy.teams.read().await.get(&team).map_or(false, |s| s.contains(&peer)));
}

#[tokio::test]
async fn enterprise_policy_add_remove_known_peer() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store);

    let peer = PeerId::new();
    policy.add_known_peer(peer).await;
    assert!(policy.known_peers.read().await.contains(&peer));

    policy.remove_known_peer(peer).await;
    assert!(!policy.known_peers.read().await.contains(&peer));
}

#[tokio::test]
async fn enterprise_policy_set_device_limit() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store);

    let peer = PeerId::new();
    policy.set_device_limit(peer, 5).await;
    assert_eq!(*policy.device_limits.read().await.get(&peer).unwrap(), 5);
}

#[tokio::test]
async fn enterprise_policy_check_device_limit() {
    use privstack_sync::policy::{DeviceId, EnterpriseSyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    // No limit configured -> always OK
    let d1 = DeviceId::new();
    policy.check_device_limit(&peer, &d1).await.unwrap();

    // Set limit of 1
    policy.set_device_limit(peer, 1).await;
    policy.check_device_limit(&peer, &d1).await.unwrap();

    // Second device should fail
    let d2 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d2).await.is_err());

    // Same device again should succeed (already registered)
    policy.check_device_limit(&peer, &d1).await.unwrap();
}

#[tokio::test]
async fn enterprise_policy_remove_device() {
    use privstack_sync::policy::{DeviceId, EnterpriseSyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let device = DeviceId::new();

    // No devices yet
    assert!(!policy.remove_device(&peer, &device).await);

    policy.set_device_limit(peer, 5).await;
    policy.check_device_limit(&peer, &device).await.unwrap();
    assert!(policy.remove_device(&peer, &device).await);
    assert!(!policy.remove_device(&peer, &device).await);
}

#[tokio::test]
async fn enterprise_policy_clear_devices() {
    use privstack_sync::policy::{DeviceId, EnterpriseSyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    policy.set_device_limit(peer, 5).await;
    policy.check_device_limit(&peer, &DeviceId::new()).await.unwrap();
    policy.check_device_limit(&peer, &DeviceId::new()).await.unwrap();

    policy.clear_devices(&peer).await;
    assert!(!policy.active_devices.read().await.contains_key(&peer));
}

#[tokio::test]
async fn enterprise_policy_flush_audit_log() {
    use privstack_sync::policy::EnterpriseSyncPolicy;

    let store = std::sync::Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    // No store -> flush is no-op
    let no_store_policy = EnterpriseSyncPolicy::new();
    no_store_policy.flush_audit_log().await.unwrap();

    // With store -> adds to audit, then flushes
    let peer = PeerId::new();
    policy.add_known_peer(peer).await;

    // Trigger audit by handshake
    use privstack_sync::policy::SyncPolicy;
    let local = PeerId::new();
    policy.on_handshake(&local, &peer).await.unwrap();

    let count_before = store.audit_log_count().unwrap();
    assert!(count_before > 0); // persisted directly via store in log()

    policy.flush_audit_log().await.unwrap();
}

#[tokio::test]
async fn enterprise_policy_on_handshake_unknown_peer_denied() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, SyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let known = PeerId::new();
    let unknown = PeerId::new();
    let local = PeerId::new();

    policy.add_known_peer(known).await;

    // Known peer -> allowed
    policy.on_handshake(&local, &known).await.unwrap();
    // Unknown peer -> denied
    assert!(policy.on_handshake(&local, &unknown).await.is_err());
}

#[tokio::test]
async fn enterprise_policy_on_sync_request() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, SyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let e1 = EntityId::new();
    let e2 = EntityId::new();

    policy.grant_peer_role(e1, peer, SyncRole::Viewer).await;
    // e2 has no role

    let allowed = policy.on_sync_request(&peer, &[e1, e2]).await.unwrap();
    assert_eq!(allowed, vec![e1]);
}

#[tokio::test]
async fn enterprise_policy_on_event_send() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, SyncPolicy};
    use privstack_types::Event;

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    // No role -> empty
    let events = vec![Event::new(entity, PeerId::new(), privstack_types::HybridTimestamp::now(), privstack_types::EventPayload::EntityDeleted { entity_type: "test".into() })];
    let result = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert!(result.is_empty());

    // With role -> returns events
    policy.grant_peer_role(entity, peer, SyncRole::Viewer).await;
    let result = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert_eq!(result.len(), 1);
}

#[tokio::test]
async fn enterprise_policy_on_event_receive_editor_vs_viewer() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, SyncPolicy};
    use privstack_types::Event;

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    let events = vec![Event::new(entity, peer, privstack_types::HybridTimestamp::now(), privstack_types::EventPayload::EntityDeleted { entity_type: "test".into() })];

    // No role -> empty
    let result = policy.on_event_receive(&peer, &entity, &events).await.unwrap();
    assert!(result.is_empty());

    // Viewer -> stripped (cannot write)
    policy.grant_peer_role(entity, peer, SyncRole::Viewer).await;
    let result = policy.on_event_receive(&peer, &entity, &events).await.unwrap();
    assert!(result.is_empty());

    // Editor -> allowed
    policy.grant_peer_role(entity, peer, SyncRole::Editor).await;
    let result = policy.on_event_receive(&peer, &entity, &events).await.unwrap();
    assert_eq!(result.len(), 1);
}

#[tokio::test]
async fn enterprise_policy_on_device_check() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, SyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    // No device limit -> OK
    policy.on_device_check(&peer, None).await.unwrap();

    // Set limit -> requires device_id
    policy.set_device_limit(peer, 2).await;
    assert!(policy.on_device_check(&peer, None).await.is_err());

    // Invalid device_id
    assert!(policy.on_device_check(&peer, Some("not-a-uuid")).await.is_err());

    // Valid device_id
    let device_id = uuid::Uuid::new_v4().to_string();
    policy.on_device_check(&peer, Some(&device_id)).await.unwrap();
}

#[tokio::test]
async fn enterprise_policy_resolve_role_team_highest_wins() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, TeamId};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    let team1 = TeamId::new();
    let team2 = TeamId::new();

    policy.add_team_member(team1, peer).await;
    policy.add_team_member(team2, peer).await;
    policy.grant_team_role(entity, team1, SyncRole::Viewer).await;
    policy.grant_team_role(entity, team2, SyncRole::Admin).await;

    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Admin));
}

#[tokio::test]
async fn enterprise_policy_entities_for_peer_returns_none() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, SyncPolicy};

    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    assert!(policy.entities_for_peer(&peer).is_none());
}

// ── PersonalSyncPolicy async methods ────────────────────────────

#[tokio::test]
async fn personal_policy_share_unshare() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    let entity = EntityId::new();
    let peer = PeerId::new();

    policy.share(entity, peer).await;
    let peers = policy.shared_peers(&entity).await;
    assert_eq!(peers, vec![peer]);

    let entities = policy.shared_entities(&peer).await;
    assert_eq!(entities, vec![entity]);

    policy.unshare(entity, peer).await;
    let peers = policy.shared_peers(&entity).await;
    assert!(peers.is_empty());
    let entities = policy.shared_entities(&peer).await;
    assert!(entities.is_empty());
}

#[tokio::test]
async fn personal_policy_unshare_nonexistent() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    // Unshare when nothing is shared -> no-op
    policy.unshare(EntityId::new(), PeerId::new()).await;
}

#[tokio::test]
async fn personal_policy_shared_entities_empty() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    let entities = policy.shared_entities(&PeerId::new()).await;
    assert!(entities.is_empty());
}

#[tokio::test]
async fn personal_policy_on_handshake() {
    use privstack_sync::policy::{PersonalSyncPolicy, SyncPolicy};

    let policy = PersonalSyncPolicy::new();
    policy.on_handshake(&PeerId::new(), &PeerId::new()).await.unwrap();
}

#[tokio::test]
async fn personal_policy_on_sync_request_filters() {
    use privstack_sync::policy::{PersonalSyncPolicy, SyncPolicy};

    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let e1 = EntityId::new();
    let e2 = EntityId::new();

    policy.share(e1, peer).await;

    let allowed = policy.on_sync_request(&peer, &[e1, e2]).await.unwrap();
    assert_eq!(allowed, vec![e1]);
}

#[tokio::test]
async fn personal_policy_on_event_send() {
    use privstack_sync::policy::{PersonalSyncPolicy, SyncPolicy};
    use privstack_types::Event;

    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    let other_entity = EntityId::new();
    let events = vec![Event::new(entity, peer, privstack_types::HybridTimestamp::now(), privstack_types::EventPayload::EntityDeleted { entity_type: "test".into() })];

    // No selective sharing configured -> allows all (map empty = no filtering)
    let result = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert_eq!(result.len(), 1);

    // Share a different entity to activate selective sharing,
    // then verify the unshared entity is blocked
    policy.share(other_entity, peer).await;
    let result = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert!(result.is_empty());

    // Now share the target entity -> returns events
    policy.share(entity, peer).await;
    let result = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert_eq!(result.len(), 1);
}

#[tokio::test]
async fn personal_policy_on_event_receive() {
    use privstack_sync::policy::{PersonalSyncPolicy, SyncPolicy};
    use privstack_types::Event;

    let policy = PersonalSyncPolicy::new();
    let entity = EntityId::new();
    let events = vec![Event::new(entity, PeerId::new(), privstack_types::HybridTimestamp::now(), privstack_types::EventPayload::EntityDeleted { entity_type: "test".into() })];

    let result = policy.on_event_receive(&PeerId::new(), &entity, &events).await.unwrap();
    assert_eq!(result.len(), 1);
}

#[tokio::test]
async fn personal_policy_entities_for_peer_returns_none() {
    use privstack_sync::policy::{PersonalSyncPolicy, SyncPolicy};

    let policy = PersonalSyncPolicy::new();
    assert!(policy.entities_for_peer(&PeerId::new()).is_none());
}

// ── AllowAllPolicy coverage ─────────────────────────────────────

#[tokio::test]
async fn allow_all_policy_all_methods() {
    use privstack_sync::policy::{AllowAllPolicy, SyncPolicy};
    use privstack_types::Event;

    let policy = AllowAllPolicy;
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    policy.on_handshake(&local, &remote).await.unwrap();

    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert_eq!(ids, vec![entity]);

    let events = vec![Event::new(entity, local, privstack_types::HybridTimestamp::now(), privstack_types::EventPayload::EntityDeleted { entity_type: "test".into() })];
    let sent = policy.on_event_send(&remote, &entity, &events).await.unwrap();
    assert_eq!(sent.len(), 1);

    let received = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(received.len(), 1);

    // Default impls
    assert!(policy.entities_for_peer(&remote).is_none());
    policy.on_device_check(&remote, None).await.unwrap();
}

// ── SyncError coverage ──────────────────────────────────────────

#[test]
fn sync_error_display_and_debug() {
    use privstack_sync::SyncError;

    let errors: Vec<SyncError> = vec![
        SyncError::Network("net".into()),
        SyncError::Protocol("proto".into()),
        SyncError::Storage("store".into()),
        SyncError::Auth("auth".into()),
        SyncError::PeerNotFound("peer".into()),
        SyncError::ConnectionRefused("refused".into()),
        SyncError::Timeout,
        SyncError::ChannelClosed,
        SyncError::PolicyDenied { reason: "denied".into() },
    ];
    for e in &errors {
        let _ = format!("{}", e);
        let _ = format!("{:?}", e);
    }
}

#[test]
fn sync_error_from_serde_json() {
    use privstack_sync::SyncError;

    let json_err: serde_json::Error = serde_json::from_str::<String>("not json").unwrap_err();
    let sync_err: SyncError = json_err.into();
    let _ = format!("{}", sync_err);
}

#[test]
fn multiple_teams_multiple_peers() {
    let store = PolicyStore::open_in_memory().unwrap();
    let team1 = uuid::Uuid::new_v4().to_string();
    let team2 = uuid::Uuid::new_v4().to_string();
    let p1 = PeerId::new();
    let p2 = PeerId::new();

    store.save_team_member(&team1, &p1).unwrap();
    store.save_team_member(&team1, &p2).unwrap();
    store.save_team_member(&team2, &p1).unwrap();

    let teams = store.load_teams().unwrap();
    assert_eq!(teams.len(), 3);
}
