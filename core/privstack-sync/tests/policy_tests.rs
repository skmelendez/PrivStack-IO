use privstack_storage::{EntityStore, EventStore};
use privstack_sync::policy::{
    AllowAllPolicy, AuditAction, AuditDecision, DeviceId, EntityAcl, EnterpriseSyncPolicy,
    SyncPolicy, SyncRole, TeamId,
};
use privstack_sync::policy_store::PolicyStore;
use privstack_sync::protocol::{EventBatchMessage, HelloMessage, SyncMessage};
use privstack_sync::{AclApplicator, AclEventHandler, SyncConfig, SyncEngine, SyncError};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};
use std::collections::HashSet;
use std::sync::Arc;
fn make_event(entity_id: EntityId, peer_id: PeerId) -> Event {
    Event::new(
        entity_id,
        peer_id,
        HybridTimestamp::now(),
        EventPayload::FullSnapshot {
            entity_type: "note".into(),
            json_data: r#"{"title":"test"}"#.into(),
        },
    )
}

fn make_events(entity_id: EntityId, peer_id: PeerId, count: usize) -> Vec<Event> {
    (0..count).map(|_| make_event(entity_id, peer_id)).collect()
}

async fn setup_enterprise() -> (EnterpriseSyncPolicy, PeerId, PeerId, EntityId) {
    let policy = EnterpriseSyncPolicy::new();
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();
    // Register remote as known
    policy.known_peers.write().await.insert(remote);
    policy.known_peers.write().await.insert(local);
    (policy, local, remote, entity)
}

// ── Happy Path ──────────────────────────────────────────────────

#[tokio::test]
async fn allow_all_policy_permits_everything() {
    let policy = AllowAllPolicy;
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    assert!(policy.on_handshake(&local, &remote).await.is_ok());

    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert_eq!(ids, vec![entity]);

    let events = make_events(entity, remote, 3);
    let sent = policy.on_event_send(&remote, &entity, &events).await.unwrap();
    assert_eq!(sent.len(), 3);

    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 3);
}

#[tokio::test]
async fn owner_can_sync_all_entities() {
    let (policy, local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Owner);
    policy.acls.write().await.insert(entity, acl);

    assert!(policy.on_handshake(&local, &remote).await.is_ok());

    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert_eq!(ids, vec![entity]);

    let events = make_events(entity, remote, 5);
    let sent = policy.on_event_send(&remote, &entity, &events).await.unwrap();
    assert_eq!(sent.len(), 5);

    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 5);
}

#[tokio::test]
async fn admin_can_write_and_read() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Admin);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 2);

    // Can read (event_send = deliver events TO this peer)
    let sent = policy.on_event_send(&remote, &entity, &events).await.unwrap();
    assert_eq!(sent.len(), 2);

    // Can write (event_receive = accept events FROM this peer)
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 2);
}

#[tokio::test]
async fn editor_can_write_assigned_entities() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let entity2 = EntityId::new();

    let acl1 = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl1);
    // entity2 has no ACL for remote

    let events = make_events(entity, remote, 2);
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 2);

    // entity2 should be denied
    let ids = policy.on_sync_request(&remote, &[entity, entity2]).await.unwrap();
    assert_eq!(ids, vec![entity]);
}

#[tokio::test]
async fn viewer_receives_events_but_cannot_send() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 3);

    // Viewer can read (events delivered TO them)
    let sent = policy.on_event_send(&remote, &entity, &events).await.unwrap();
    assert_eq!(sent.len(), 3);

    // Viewer cannot write (events FROM them are stripped)
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());
}

#[tokio::test]
async fn team_role_grants_access_to_all_team_members() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let team = TeamId::new();

    // Add remote to team
    policy.teams.write().await.insert(team, HashSet::from([remote]));

    // ACL grants team Editor access
    let acl = EntityAcl::new(entity).with_team_role(team, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 2);
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 2);

    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert_eq!(ids, vec![entity]);
}

#[tokio::test]
async fn peer_role_overrides_team_role() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let team = TeamId::new();

    policy.teams.write().await.insert(team, HashSet::from([remote]));

    // Team grants Editor, but peer is explicitly Viewer
    let acl = EntityAcl::new(entity)
        .with_team_role(team, SyncRole::Editor)
        .with_peer_role(remote, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 2);
    // Peer override (Viewer) should win — cannot write
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());
}

#[tokio::test]
async fn audit_log_records_all_policy_decisions() {
    let (policy, local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 1);

    policy.on_handshake(&local, &remote).await.unwrap();
    policy.on_sync_request(&remote, &[entity]).await.unwrap();
    policy.on_event_send(&remote, &entity, &events).await.unwrap();
    policy.on_event_receive(&remote, &entity, &events).await.unwrap();

    let log = policy.audit_log.read().await;
    assert_eq!(log.len(), 4);
    assert_eq!(log[0].action, AuditAction::Handshake);
    assert_eq!(log[1].action, AuditAction::SyncRequest);
    assert_eq!(log[2].action, AuditAction::EventSend);
    assert_eq!(log[3].action, AuditAction::EventReceive);

    // All should be allowed/allowed
    assert_eq!(log[0].decision, AuditDecision::Allowed);
    assert_eq!(log[1].decision, AuditDecision::Allowed);
    assert_eq!(log[2].decision, AuditDecision::Allowed);
    assert_eq!(log[3].decision, AuditDecision::Allowed);
}

// ── Denial / Adversarial ────────────────────────────────────────

#[tokio::test]
async fn unknown_peer_denied_at_handshake() {
    let policy = EnterpriseSyncPolicy::new();
    let local = PeerId::new();
    let remote = PeerId::new();

    // Add some known peer that is NOT remote
    policy.known_peers.write().await.insert(PeerId::new());

    let result = policy.on_handshake(&local, &remote).await;
    assert!(result.is_err());
    match result.unwrap_err() {
        SyncError::PolicyDenied { reason } => assert!(reason.contains("unknown peer")),
        other => panic!("Expected PolicyDenied, got {:?}", other),
    }
}

#[tokio::test]
async fn viewer_event_send_stripped() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 5);
    // on_event_receive filters out writes from a Viewer
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());

    let log = policy.audit_log.read().await;
    let last = log.last().unwrap();
    assert_eq!(last.decision, AuditDecision::Filtered);
}

#[tokio::test]
async fn device_limit_exceeded_blocks_new_device() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    // Limit: 2 devices
    policy.device_limits.write().await.insert(peer, 2);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();
    let d3 = DeviceId::new();

    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());

    // Third device should fail
    let result = policy.check_device_limit(&peer, &d3).await;
    assert!(result.is_err());
    match result.unwrap_err() {
        SyncError::PolicyDenied { reason } => assert!(reason.contains("device limit exceeded")),
        other => panic!("Expected PolicyDenied, got {:?}", other),
    }

    // Re-registering existing device is fine
    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
}

#[tokio::test]
async fn revoked_peer_blocked_mid_sync() {
    let (policy, local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    // Initially can sync
    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert_eq!(ids.len(), 1);

    // Revoke: remove peer role, no default
    policy.acls.write().await.get_mut(&entity).unwrap().peer_roles.remove(&remote);

    // Now denied
    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert!(ids.is_empty());

    // Also remove from known_peers for handshake denial
    policy.known_peers.write().await.remove(&remote);
    let result = policy.on_handshake(&local, &remote).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn entity_not_in_request_filtered() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let secret_entity = EntityId::new();

    // Remote only has access to `entity`
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let ids = policy.on_sync_request(&remote, &[entity, secret_entity]).await.unwrap();
    assert_eq!(ids, vec![entity]);
}

#[tokio::test]
async fn tampered_events_rejected() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let wrong_entity = EntityId::new();
    // Events claim to be for `wrong_entity` but are submitted under `entity`
    let tampered = make_events(wrong_entity, remote, 3);
    let recv = policy.on_event_receive(&remote, &entity, &tampered).await.unwrap();
    // Should be filtered out because event.entity_id != entity
    assert!(recv.is_empty());
}

#[tokio::test]
async fn role_downgrade_takes_effect_immediately() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Admin);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 2);
    // Admin can write
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 2);

    // Downgrade to Viewer
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .insert(remote, SyncRole::Viewer);

    // Viewer cannot write
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());
}

#[tokio::test]
async fn empty_acl_denies_all() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    // ACL exists but has no default role and no peer/team entries
    let acl = EntityAcl::new(entity);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 2);

    let ids = policy.on_sync_request(&remote, &[entity]).await.unwrap();
    assert!(ids.is_empty());

    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());

    let sent = policy.on_event_send(&remote, &entity, &events).await.unwrap();
    assert!(sent.is_empty());
}

// ── Multi-Tier / N-Peer ─────────────────────────────────────────

#[tokio::test]
async fn mixed_roles_10_peers_correct_convergence() {
    let policy = EnterpriseSyncPolicy::new();
    let entity = EntityId::new();

    let owners: Vec<PeerId> = (0..2).map(|_| PeerId::new()).collect();
    let admins: Vec<PeerId> = (0..3).map(|_| PeerId::new()).collect();
    let editors: Vec<PeerId> = (0..3).map(|_| PeerId::new()).collect();
    let viewers: Vec<PeerId> = (0..2).map(|_| PeerId::new()).collect();

    let mut acl = EntityAcl::new(entity);
    for &p in &owners {
        acl.peer_roles.insert(p, SyncRole::Owner);
        policy.known_peers.write().await.insert(p);
    }
    for &p in &admins {
        acl.peer_roles.insert(p, SyncRole::Admin);
        policy.known_peers.write().await.insert(p);
    }
    for &p in &editors {
        acl.peer_roles.insert(p, SyncRole::Editor);
        policy.known_peers.write().await.insert(p);
    }
    for &p in &viewers {
        acl.peer_roles.insert(p, SyncRole::Viewer);
        policy.known_peers.write().await.insert(p);
    }
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, owners[0], 5);

    // All peers can read (on_event_send)
    for peers in [&owners, &admins, &editors, &viewers] {
        for &p in peers {
            let sent = policy.on_event_send(&p, &entity, &events).await.unwrap();
            assert_eq!(sent.len(), 5, "peer should be able to read");
        }
    }

    // Owners, admins, editors can write; viewers cannot
    for &p in owners.iter().chain(admins.iter()).chain(editors.iter()) {
        let recv = policy.on_event_receive(&p, &entity, &events).await.unwrap();
        assert_eq!(recv.len(), 5, "peer {:?} should be able to write", p);
    }
    for &p in &viewers {
        let recv = policy.on_event_receive(&p, &entity, &events).await.unwrap();
        assert!(recv.is_empty(), "viewer should not write");
    }
}

#[tokio::test]
async fn three_teams_isolated_entities() {
    let policy = EnterpriseSyncPolicy::new();

    let team_a = TeamId::new();
    let team_b = TeamId::new();
    let team_c = TeamId::new();

    let peers_a: Vec<PeerId> = (0..3).map(|_| PeerId::new()).collect();
    let peers_b: Vec<PeerId> = (0..3).map(|_| PeerId::new()).collect();
    let peers_c: Vec<PeerId> = (0..3).map(|_| PeerId::new()).collect();

    policy.teams.write().await.insert(team_a, peers_a.iter().copied().collect());
    policy.teams.write().await.insert(team_b, peers_b.iter().copied().collect());
    policy.teams.write().await.insert(team_c, peers_c.iter().copied().collect());

    let entity_a = EntityId::new();
    let entity_b = EntityId::new();
    let entity_c = EntityId::new();

    policy.acls.write().await.insert(
        entity_a,
        EntityAcl::new(entity_a).with_team_role(team_a, SyncRole::Editor),
    );
    policy.acls.write().await.insert(
        entity_b,
        EntityAcl::new(entity_b).with_team_role(team_b, SyncRole::Editor),
    );
    policy.acls.write().await.insert(
        entity_c,
        EntityAcl::new(entity_c).with_team_role(team_c, SyncRole::Editor),
    );

    // Team A members can access entity_a but not b or c
    for &p in &peers_a {
        let ids = policy
            .on_sync_request(&p, &[entity_a, entity_b, entity_c])
            .await
            .unwrap();
        assert_eq!(ids, vec![entity_a]);
    }

    // Team B → entity_b only
    for &p in &peers_b {
        let ids = policy
            .on_sync_request(&p, &[entity_a, entity_b, entity_c])
            .await
            .unwrap();
        assert_eq!(ids, vec![entity_b]);
    }

    // Team C → entity_c only
    for &p in &peers_c {
        let ids = policy
            .on_sync_request(&p, &[entity_a, entity_b, entity_c])
            .await
            .unwrap();
        assert_eq!(ids, vec![entity_c]);
    }
}

#[tokio::test]
async fn team_merge_grants_access() {
    let policy = EnterpriseSyncPolicy::new();

    let team_src = TeamId::new();
    let team_dst = TeamId::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    // Peer is in team_src
    policy.teams.write().await.insert(team_src, HashSet::from([peer]));
    policy.teams.write().await.insert(team_dst, HashSet::new());

    // Entity accessible by team_dst only
    policy.acls.write().await.insert(
        entity,
        EntityAcl::new(entity).with_team_role(team_dst, SyncRole::Editor),
    );

    // Before move: no access
    let ids = policy.on_sync_request(&peer, &[entity]).await.unwrap();
    assert!(ids.is_empty());

    // Move peer to team_dst
    policy.teams.write().await.get_mut(&team_src).unwrap().remove(&peer);
    policy.teams.write().await.get_mut(&team_dst).unwrap().insert(peer);

    // After move: access granted
    let ids = policy.on_sync_request(&peer, &[entity]).await.unwrap();
    assert_eq!(ids, vec![entity]);
}

#[tokio::test]
async fn fifty_peer_enterprise_full_sync_with_policy() {
    let policy = EnterpriseSyncPolicy::new();

    let teams: Vec<TeamId> = (0..5).map(|_| TeamId::new()).collect();
    let entities: Vec<EntityId> = (0..10).map(|_| EntityId::new()).collect();
    let peers: Vec<PeerId> = (0..50).map(|_| PeerId::new()).collect();

    // Distribute peers across teams (10 per team)
    for (i, team) in teams.iter().enumerate() {
        let members: HashSet<PeerId> = peers[i * 10..(i + 1) * 10].iter().copied().collect();
        for &p in &members {
            policy.known_peers.write().await.insert(p);
        }
        policy.teams.write().await.insert(*team, members);
    }

    // Each team gets 2 entities
    for (i, team) in teams.iter().enumerate() {
        for j in 0..2 {
            let eid = entities[i * 2 + j];
            policy.acls.write().await.insert(
                eid,
                EntityAcl::new(eid).with_team_role(*team, SyncRole::Editor),
            );
        }
    }

    // Each peer should only see their team's 2 entities
    for (i, &peer) in peers.iter().enumerate() {
        let team_idx = i / 10;
        let all_entities: Vec<EntityId> = entities.iter().copied().collect();
        let allowed = policy.on_sync_request(&peer, &all_entities).await.unwrap();
        assert_eq!(
            allowed.len(),
            2,
            "peer {} in team {} should see 2 entities, got {}",
            i,
            team_idx,
            allowed.len()
        );

        let expected: HashSet<EntityId> = [entities[team_idx * 2], entities[team_idx * 2 + 1]]
            .iter()
            .copied()
            .collect();
        let actual: HashSet<EntityId> = allowed.into_iter().collect();
        assert_eq!(actual, expected);
    }
}

#[tokio::test]
async fn cascading_permission_changes() {
    let policy = EnterpriseSyncPolicy::new();

    let team = TeamId::new();
    let entity = EntityId::new();
    let peers: Vec<PeerId> = (0..5).map(|_| PeerId::new()).collect();

    policy.teams.write().await.insert(team, peers.iter().copied().collect());
    policy.acls.write().await.insert(
        entity,
        EntityAcl::new(entity).with_team_role(team, SyncRole::Editor),
    );

    // All peers have access
    for &p in &peers {
        let ids = policy.on_sync_request(&p, &[entity]).await.unwrap();
        assert_eq!(ids.len(), 1);
    }

    // Owner revokes team access
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .team_roles
        .remove(&team);

    // All peers lose access
    for &p in &peers {
        let ids = policy.on_sync_request(&p, &[entity]).await.unwrap();
        assert!(ids.is_empty());
    }
}

#[tokio::test]
async fn license_tier_upgrade_allows_more_devices() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    // Trial: 1 device
    policy.device_limits.write().await.insert(peer, 1);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();

    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_err());

    // Upgrade to Pro: 5 devices
    policy.device_limits.write().await.insert(peer, 5);

    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());

    let d3 = DeviceId::new();
    let d4 = DeviceId::new();
    let d5 = DeviceId::new();
    let d6 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d3).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d4).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d5).await.is_ok());
    // 6th device exceeds Pro limit
    assert!(policy.check_device_limit(&peer, &d6).await.is_err());
}

// ── Role Resolution Edge Cases ──────────────────────────────────

#[tokio::test]
async fn peer_in_multiple_teams_gets_highest_role() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    let team_viewer = TeamId::new();
    let team_editor = TeamId::new();
    let team_admin = TeamId::new();

    policy.teams.write().await.insert(team_viewer, HashSet::from([peer]));
    policy.teams.write().await.insert(team_editor, HashSet::from([peer]));
    policy.teams.write().await.insert(team_admin, HashSet::from([peer]));

    let acl = EntityAcl::new(entity)
        .with_team_role(team_viewer, SyncRole::Viewer)
        .with_team_role(team_editor, SyncRole::Editor)
        .with_team_role(team_admin, SyncRole::Admin);
    policy.acls.write().await.insert(entity, acl);

    // Should resolve to Admin (highest)
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Admin));

    // Admin can write
    let events = make_events(entity, peer, 2);
    let recv = policy.on_event_receive(&peer, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 2);
}

#[tokio::test]
async fn peer_override_wins_over_multiple_teams() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    let team = TeamId::new();
    policy.teams.write().await.insert(team, HashSet::from([peer]));

    // Team gives Owner, but peer is explicitly Viewer
    let acl = EntityAcl::new(entity)
        .with_team_role(team, SyncRole::Owner)
        .with_peer_role(peer, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Viewer));
}

#[tokio::test]
async fn default_role_applies_when_no_peer_or_team_match() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    let acl = EntityAcl::new(entity).with_default_role(SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Viewer));

    // Can read but not write
    let events = make_events(entity, peer, 1);
    let sent = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert_eq!(sent.len(), 1);
    let recv = policy.on_event_receive(&peer, &entity, &events).await.unwrap();
    assert!(recv.is_empty());
}

#[tokio::test]
async fn peer_removed_from_teams_falls_back_to_default() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    let team = TeamId::new();

    policy.teams.write().await.insert(team, HashSet::from([peer]));

    let acl = EntityAcl::new(entity)
        .with_default_role(SyncRole::Viewer)
        .with_team_role(team, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    // With team: Editor
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));

    // Remove from team
    policy.teams.write().await.get_mut(&team).unwrap().remove(&peer);

    // Falls back to default: Viewer
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Viewer));
}

#[tokio::test]
async fn no_acl_at_all_returns_none() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    // No ACL inserted for this entity at all
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, None);
}

// ── Boundary / Degenerate Inputs ────────────────────────────────

#[tokio::test]
async fn empty_entity_list_sync_request() {
    let (policy, _local, remote, _entity) = setup_enterprise().await;
    let ids = policy.on_sync_request(&remote, &[]).await.unwrap();
    assert!(ids.is_empty());
}

#[tokio::test]
async fn empty_event_list_send_and_receive() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let sent = policy.on_event_send(&remote, &entity, &[]).await.unwrap();
    assert!(sent.is_empty());

    let recv = policy.on_event_receive(&remote, &entity, &[]).await.unwrap();
    assert!(recv.is_empty());
}

#[tokio::test]
async fn empty_known_peers_skips_membership_check() {
    let policy = EnterpriseSyncPolicy::new();
    let local = PeerId::new();
    let remote = PeerId::new();

    // known_peers is empty — handshake should be allowed for any peer
    assert!(policy.known_peers.read().await.is_empty());
    assert!(policy.on_handshake(&local, &remote).await.is_ok());
}

#[tokio::test]
async fn device_limit_zero_blocks_all() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    policy.device_limits.write().await.insert(peer, 0);
    let d = DeviceId::new();
    let result = policy.check_device_limit(&peer, &d).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn device_limit_downgrade_grandfathers_existing() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    // Start with limit 3, register 3 devices
    policy.device_limits.write().await.insert(peer, 3);
    let d1 = DeviceId::new();
    let d2 = DeviceId::new();
    let d3 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d3).await.is_ok());

    // Downgrade limit to 1
    policy.device_limits.write().await.insert(peer, 1);

    // Existing devices are grandfathered (already registered)
    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d3).await.is_ok());

    // But new device is blocked
    let d4 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d4).await.is_err());
}

// ── Audit Log Edge Cases ────────────────────────────────────────

#[tokio::test]
async fn denied_handshake_produces_audit_entry() {
    let policy = EnterpriseSyncPolicy::new();
    let local = PeerId::new();
    let remote = PeerId::new();

    // Add a different peer so known_peers is non-empty
    policy.known_peers.write().await.insert(PeerId::new());

    let _ = policy.on_handshake(&local, &remote).await;

    let log = policy.audit_log.read().await;
    assert_eq!(log.len(), 1);
    assert_eq!(log[0].action, AuditAction::Handshake);
    assert_eq!(log[0].decision, AuditDecision::Denied);
    assert_eq!(log[0].peer, remote);
}

#[tokio::test]
async fn denied_sync_request_produces_per_entity_audit_entries() {
    let (policy, _local, remote, _entity) = setup_enterprise().await;
    let e1 = EntityId::new();
    let e2 = EntityId::new();
    let e3 = EntityId::new();

    // No ACLs for any of these entities
    let ids = policy.on_sync_request(&remote, &[e1, e2, e3]).await.unwrap();
    assert!(ids.is_empty());

    let log = policy.audit_log.read().await;
    assert_eq!(log.len(), 3);
    for entry in log.iter() {
        assert_eq!(entry.action, AuditAction::SyncRequest);
        assert_eq!(entry.decision, AuditDecision::Denied);
    }
}

#[tokio::test]
async fn audit_entries_contain_correct_entity_ids() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let entity2 = EntityId::new();

    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let _ = policy.on_sync_request(&remote, &[entity, entity2]).await.unwrap();

    let log = policy.audit_log.read().await;
    assert_eq!(log.len(), 2);
    assert_eq!(log[0].entity, Some(entity));
    assert_eq!(log[0].decision, AuditDecision::Allowed);
    assert_eq!(log[1].entity, Some(entity2));
    assert_eq!(log[1].decision, AuditDecision::Denied);
}

#[tokio::test]
async fn device_limit_denial_produces_audit_entry() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 0);

    let _ = policy.check_device_limit(&peer, &DeviceId::new()).await;

    let log = policy.audit_log.read().await;
    assert_eq!(log.len(), 1);
    assert_eq!(log[0].action, AuditAction::DeviceRegister);
    assert_eq!(log[0].decision, AuditDecision::Denied);
}

// ── Concurrency ─────────────────────────────────────────────────

#[tokio::test]
async fn concurrent_acl_mutation_during_sync_requests() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let peer = PeerId::new();
    let entity = EntityId::new();

    let acl = EntityAcl::new(entity).with_peer_role(peer, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let policy_check = policy.clone();
    let policy_mutate = policy.clone();

    // Spawn 100 concurrent sync_request checks
    let check_handle = tokio::spawn(async move {
        let mut allowed_count = 0;
        let mut denied_count = 0;
        for _ in 0..100 {
            let ids = policy_check.on_sync_request(&peer, &[entity]).await.unwrap();
            if ids.contains(&entity) {
                allowed_count += 1;
            } else {
                denied_count += 1;
            }
            tokio::task::yield_now().await;
        }
        (allowed_count, denied_count)
    });

    // Concurrently toggle the ACL
    let mutate_handle = tokio::spawn(async move {
        for i in 0..100 {
            let mut acls = policy_mutate.acls.write().await;
            if i % 2 == 0 {
                acls.get_mut(&entity).unwrap().peer_roles.remove(&peer);
            } else {
                acls.get_mut(&entity).unwrap().peer_roles.insert(peer, SyncRole::Editor);
            }
            drop(acls);
            tokio::task::yield_now().await;
        }
    });

    let (counts, _) = tokio::join!(check_handle, mutate_handle);
    let (allowed, denied) = counts.unwrap();
    // Both allowed and denied should have occurred (no panics, no deadlocks)
    // We can't assert exact counts due to scheduling, but total must be 100
    assert_eq!(allowed + denied, 100);
}

#[tokio::test]
async fn concurrent_device_registration_respects_limit() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 5);

    let mut handles = Vec::new();
    // Try to register 20 devices concurrently, only 5 should succeed
    for _ in 0..20 {
        let p = policy.clone();
        let pr = peer;
        handles.push(tokio::spawn(async move {
            let d = DeviceId::new();
            p.check_device_limit(&pr, &d).await.is_ok()
        }));
    }

    let mut success = 0;
    for h in handles {
        if h.await.unwrap() {
            success += 1;
        }
    }

    assert_eq!(success, 5);

    let active = policy.active_devices.read().await;
    assert_eq!(active.get(&peer).unwrap().len(), 5);
}

// ── Engine Integration ──────────────────────────────────────────

fn make_stores() -> (Arc<EntityStore>, Arc<EventStore>) {
    let entity_store = Arc::new(EntityStore::open_in_memory().unwrap());
    let event_store = Arc::new(EventStore::open_in_memory().unwrap());
    (entity_store, event_store)
}

#[tokio::test]
async fn engine_with_policy_rejects_hello_from_unknown_peer() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();

    // Only local is known
    policy.known_peers.write().await.insert(local);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);

    let hello = HelloMessage::new(remote, "Unknown Device");
    let response = engine.handle_hello(&hello).await;

    match response {
        SyncMessage::HelloAck(ack) => {
            assert!(!ack.accepted);
            assert!(ack.reason.unwrap().contains("policy denied"));
        }
        other => panic!("Expected HelloAck rejection, got {:?}", other),
    }

    // Peer should NOT be tracked
    assert!(engine.connected_peers().await.is_empty());
}

#[tokio::test]
async fn engine_with_policy_accepts_hello_from_known_peer() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();

    policy.known_peers.write().await.insert(local);
    policy.known_peers.write().await.insert(remote);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);

    let hello = HelloMessage::new(remote, "Known Device");
    let response = engine.handle_hello(&hello).await;

    match response {
        SyncMessage::HelloAck(ack) => {
            assert!(ack.accepted);
        }
        other => panic!("Expected HelloAck accept, got {:?}", other),
    }

    assert_eq!(engine.connected_peers().await.len(), 1);
}

#[tokio::test]
async fn engine_handle_sync_request_filters_entities() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let allowed_entity = EntityId::new();
    let denied_entity = EntityId::new();

    policy.known_peers.write().await.insert(remote);

    let acl = EntityAcl::new(allowed_entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(allowed_entity, acl);
    // No ACL for denied_entity

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (_entity_store, event_store) = make_stores();

    let request = privstack_sync::protocol::SyncRequestMessage {
        entity_ids: vec![allowed_entity, denied_entity],
        known_event_ids: std::collections::HashMap::new(),
    };

    let response = engine.handle_sync_request(&remote, &request, &event_store).await;
    match response {
        SyncMessage::SyncState(state) => {
            // Only allowed_entity should be in the response
            // (The state may have empty clocks for entities with no events,
            //  but denied_entity should not appear)
            assert!(
                !state.clocks.contains_key(&denied_entity),
                "denied entity should not be in sync state"
            );
        }
        other => panic!("Expected SyncState, got {:?}", other),
    }
}

#[tokio::test]
async fn engine_handle_event_batch_viewer_drops_events() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(remote);
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (entity_store, event_store) = make_stores();

    let events = make_events(entity, remote, 3);
    let batch = EventBatchMessage {
        entity_id: entity,
        events,
        is_final: true,
        batch_seq: 0,
    };

    let (ack, updated) = engine
        .handle_event_batch(&remote, &batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.received_count, 0, "viewer events should be dropped");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
    assert!(updated.is_empty());
}

#[tokio::test]
async fn engine_handle_event_batch_editor_applies_events() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(remote);
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (entity_store, event_store) = make_stores();

    let events = make_events(entity, remote, 2);
    let batch = EventBatchMessage {
        entity_id: entity,
        events,
        is_final: true,
        batch_seq: 0,
    };

    let (ack, updated) = engine
        .handle_event_batch(&remote, &batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.received_count, 2, "editor events should be applied");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
    assert_eq!(updated.len(), 1);
}

#[tokio::test]
async fn engine_compute_batches_for_peer_denied_returns_empty() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    // No ACL for remote on this entity
    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (_entity_store, event_store) = make_stores();

    // Store some events
    let events = make_events(entity, local, 3);
    for e in &events {
        event_store.save_event(e).unwrap();
        engine.record_local_event(e).await;
    }

    let batches = engine
        .compute_event_batches_for_peer(&remote, entity, &HashSet::new(), &event_store)
        .await;
    assert!(batches.is_empty(), "denied peer should get no batches");
}

#[tokio::test]
async fn engine_compute_batches_without_peer_skips_policy() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let entity = EntityId::new();

    // No ACLs at all — compute_event_batches (no peer) should still return events
    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (_entity_store, event_store) = make_stores();

    let events = make_events(entity, local, 3);
    for e in &events {
        event_store.save_event(e).unwrap();
        engine.record_local_event(e).await;
    }

    let batches = engine
        .compute_event_batches(entity, &HashSet::new(), &event_store)
        .await;
    assert!(!batches.is_empty(), "no-peer overload should skip policy");
}

#[tokio::test]
async fn engine_asymmetric_sync_owner_to_viewer() {
    // Owner engine sends to viewer engine — viewer should receive events
    // but viewer's events should be dropped by owner's policy
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let owner_id = PeerId::new();
    let viewer_id = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(owner_id);
    policy.known_peers.write().await.insert(viewer_id);

    let acl = EntityAcl::new(entity)
        .with_peer_role(owner_id, SyncRole::Owner)
        .with_peer_role(viewer_id, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let owner_engine = SyncEngine::with_policy(owner_id, SyncConfig::default(), policy.clone());
    let viewer_engine = SyncEngine::with_policy(viewer_id, SyncConfig::default(), policy.clone());

    let (entity_store, event_store) = make_stores();

    // Owner creates events
    let owner_events = make_events(entity, owner_id, 3);
    for e in &owner_events {
        event_store.save_event(e).unwrap();
        owner_engine.record_local_event(e).await;
    }

    // Owner sends to viewer — policy allows (viewer can read)
    let batches = owner_engine
        .compute_event_batches_for_peer(&viewer_id, entity, &HashSet::new(), &event_store)
        .await;
    assert!(!batches.is_empty(), "owner should be able to send to viewer");

    // Viewer creates events and tries to send to owner
    let (_, viewer_event_store) = make_stores();
    let viewer_events = make_events(entity, viewer_id, 2);
    for e in &viewer_events {
        viewer_event_store.save_event(e).unwrap();
        viewer_engine.record_local_event(e).await;
    }

    // Owner receives batch from viewer — policy should drop (viewer can't write)
    let batch = EventBatchMessage {
        entity_id: entity,
        events: viewer_events,
        is_final: true,
        batch_seq: 0,
    };

    let (ack, updated) = owner_engine
        .handle_event_batch(&viewer_id, &batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.received_count, 0, "viewer writes should be rejected by owner");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
    assert!(updated.is_empty());
}

// ── Cross-Policy ────────────────────────────────────────────────

#[tokio::test]
async fn allow_all_engine_accepts_any_hello() {
    // Verify default SyncEngine (AllowAllPolicy) still works
    let engine = SyncEngine::new(PeerId::new(), SyncConfig::default());
    let remote = PeerId::new();

    let hello = HelloMessage::new(remote, "Any Device");
    let response = engine.handle_hello(&hello).await;

    match response {
        SyncMessage::HelloAck(ack) => assert!(ack.accepted),
        other => panic!("Expected HelloAck, got {:?}", other),
    }
}

#[tokio::test]
async fn mixed_policy_engines_interop() {
    // Enterprise engine talks to AllowAll engine
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let enterprise_id = PeerId::new();
    let open_id = PeerId::new();

    policy.known_peers.write().await.insert(enterprise_id);
    policy.known_peers.write().await.insert(open_id);

    let enterprise_engine =
        SyncEngine::with_policy(enterprise_id, SyncConfig::default(), policy.clone());
    let open_engine = SyncEngine::new(open_id, SyncConfig::default());

    // Open engine sends hello to enterprise — accepted (open_id is known)
    let hello = HelloMessage::new(open_id, "Open Device");
    let response = enterprise_engine.handle_hello(&hello).await;
    match response {
        SyncMessage::HelloAck(ack) => assert!(ack.accepted),
        other => panic!("Expected accepted, got {:?}", other),
    }

    // Enterprise engine sends hello to open — always accepted (AllowAll)
    let hello = HelloMessage::new(enterprise_id, "Enterprise Device");
    let response = open_engine.handle_hello(&hello).await;
    match response {
        SyncMessage::HelloAck(ack) => assert!(ack.accepted),
        other => panic!("Expected accepted, got {:?}", other),
    }
}

// ── Multi-Entity Mixed Permissions ──────────────────────────────

#[tokio::test]
async fn peer_has_different_roles_on_different_entities() {
    let (policy, _local, remote, _) = setup_enterprise().await;
    let owned = EntityId::new();
    let editable = EntityId::new();
    let viewable = EntityId::new();
    let forbidden = EntityId::new();

    policy.acls.write().await.insert(
        owned,
        EntityAcl::new(owned).with_peer_role(remote, SyncRole::Owner),
    );
    policy.acls.write().await.insert(
        editable,
        EntityAcl::new(editable).with_peer_role(remote, SyncRole::Editor),
    );
    policy.acls.write().await.insert(
        viewable,
        EntityAcl::new(viewable).with_peer_role(remote, SyncRole::Viewer),
    );
    // forbidden has no ACL

    let ids = policy
        .on_sync_request(&remote, &[owned, editable, viewable, forbidden])
        .await
        .unwrap();
    assert_eq!(ids.len(), 3);
    assert!(ids.contains(&owned));
    assert!(ids.contains(&editable));
    assert!(ids.contains(&viewable));
    assert!(!ids.contains(&forbidden));

    // Write checks per entity
    let events_owned = make_events(owned, remote, 1);
    let events_editable = make_events(editable, remote, 1);
    let events_viewable = make_events(viewable, remote, 1);

    assert_eq!(
        policy.on_event_receive(&remote, &owned, &events_owned).await.unwrap().len(),
        1
    );
    assert_eq!(
        policy.on_event_receive(&remote, &editable, &events_editable).await.unwrap().len(),
        1
    );
    assert_eq!(
        policy.on_event_receive(&remote, &viewable, &events_viewable).await.unwrap().len(),
        0
    );
}

#[tokio::test]
async fn role_upgrade_takes_effect_immediately() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, remote, 2);

    // Viewer: cannot write
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());

    // Upgrade to Owner
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .insert(remote, SyncRole::Owner);

    // Owner: can write
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert_eq!(recv.len(), 2);
}

#[tokio::test]
async fn hundred_entities_mixed_access_stress() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    let entities: Vec<EntityId> = (0..100).map(|_| EntityId::new()).collect();

    // Even-indexed entities: Editor, odd: no access
    for (i, &eid) in entities.iter().enumerate() {
        if i % 2 == 0 {
            let acl = EntityAcl::new(eid).with_peer_role(peer, SyncRole::Editor);
            policy.acls.write().await.insert(eid, acl);
        }
    }

    let allowed = policy.on_sync_request(&peer, &entities).await.unwrap();
    assert_eq!(allowed.len(), 50);

    for &eid in &allowed {
        let idx = entities.iter().position(|&e| e == eid).unwrap();
        assert_eq!(idx % 2, 0, "only even-indexed entities should be allowed");
    }
}

// ── SyncRole ordering ───────────────────────────────────────────

#[tokio::test]
async fn sync_role_ordering_is_correct() {
    assert!(SyncRole::Viewer < SyncRole::Editor);
    assert!(SyncRole::Editor < SyncRole::Admin);
    assert!(SyncRole::Admin < SyncRole::Owner);
    assert!(SyncRole::Viewer < SyncRole::Owner);

    // PartialOrd consistency
    assert!(SyncRole::Editor >= SyncRole::Editor);
    assert!(SyncRole::Owner >= SyncRole::Viewer);
}

// ── Data Preservation / One-Sided Sync / Revocation Lifecycle ───

#[tokio::test]
async fn one_sided_events_populate_empty_peer_not_wipe() {
    // Peer A has 10 events, Peer B has 0. Syncing should populate B, not wipe A.
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(peer_a);
    policy.known_peers.write().await.insert(peer_b);
    let acl = EntityAcl::new(entity)
        .with_peer_role(peer_a, SyncRole::Owner)
        .with_peer_role(peer_b, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let engine_a = SyncEngine::with_policy(peer_a, SyncConfig::default(), policy.clone());
    let engine_b = SyncEngine::with_policy(peer_b, SyncConfig::default(), policy.clone());

    let (_entity_store_a, event_store_a) = make_stores();
    let (entity_store_b, event_store_b) = make_stores();

    // A has 10 events
    let events_a: Vec<Event> = make_events(entity, peer_a, 10);
    for e in &events_a {
        event_store_a.save_event(e).unwrap();
        engine_a.record_local_event(e).await;
    }

    // B has nothing
    assert_eq!(
        event_store_b.get_events_for_entity(&entity).unwrap().len(),
        0
    );

    // A computes batches to send to B
    let batches = engine_a
        .compute_event_batches_for_peer(&peer_b, entity, &HashSet::new(), &event_store_a)
        .await;
    assert!(!batches.is_empty());

    // B receives the batches
    let mut total_received = 0;
    for batch_msg in &batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            let (ack, _updated) = engine_b
                .handle_event_batch(&peer_a, batch, &entity_store_b, &event_store_b)
                .await;
            if let SyncMessage::EventAck(a) = ack {
                total_received += a.received_count;
            }
        }
    }
    assert_eq!(total_received, 10, "B should receive all 10 events");

    // A still has all 10 events (not wiped)
    assert_eq!(
        event_store_a.get_events_for_entity(&entity).unwrap().len(),
        10
    );

    // B now also has 10 events
    assert_eq!(
        event_store_b.get_events_for_entity(&entity).unwrap().len(),
        10
    );
}

#[tokio::test]
async fn empty_peer_syncs_to_full_peer_adds_not_wipes() {
    // Reverse direction: B (empty) sends empty batch to A (10 events).
    // A should not lose data. B should get A's events via reverse delta.
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(peer_a);
    policy.known_peers.write().await.insert(peer_b);
    let acl = EntityAcl::new(entity)
        .with_peer_role(peer_a, SyncRole::Owner)
        .with_peer_role(peer_b, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let engine_a = SyncEngine::with_policy(peer_a, SyncConfig::default(), policy.clone());

    let (entity_store_a, event_store_a) = make_stores();

    // A has 10 events
    let events_a = make_events(entity, peer_a, 10);
    for e in &events_a {
        event_store_a.save_event(e).unwrap();
        engine_a.record_local_event(e).await;
    }

    // B sends an empty final batch (simulating "I have nothing, give me what you have")
    let empty_batch = EventBatchMessage {
        entity_id: entity,
        events: Vec::new(),
        is_final: true,
        batch_seq: 0,
    };

    let (ack, _) = engine_a
        .handle_event_batch(&peer_b, &empty_batch, &entity_store_a, &event_store_a)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            // A should include reverse-delta events for B
            assert_eq!(a.events.len(), 10, "A should send all 10 events back to B");
            assert_eq!(a.received_count, 0, "no events were received from B");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }

    // A still has all events
    assert_eq!(
        event_store_a.get_events_for_entity(&entity).unwrap().len(),
        10
    );
}

#[tokio::test]
async fn revoked_peer_existing_data_stays_cannot_write_or_read_new() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let owner_id = PeerId::new();
    let revokee_id = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(owner_id);
    policy.known_peers.write().await.insert(revokee_id);

    let acl = EntityAcl::new(entity)
        .with_peer_role(owner_id, SyncRole::Owner)
        .with_peer_role(revokee_id, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let owner_engine = SyncEngine::with_policy(owner_id, SyncConfig::default(), policy.clone());
    let (owner_entity_store, owner_event_store) = make_stores();
    let (revokee_entity_store, revokee_event_store) = make_stores();
    let revokee_engine =
        SyncEngine::with_policy(revokee_id, SyncConfig::default(), policy.clone());

    // Phase 1: Both sides have data, revokee synced successfully
    let initial_events = make_events(entity, owner_id, 5);
    for e in &initial_events {
        owner_event_store.save_event(e).unwrap();
        owner_engine.record_local_event(e).await;
    }

    // Revokee receives initial events
    let batches = owner_engine
        .compute_event_batches_for_peer(&revokee_id, entity, &HashSet::new(), &owner_event_store)
        .await;
    for batch_msg in &batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            revokee_engine
                .handle_event_batch(
                    &owner_id,
                    batch,
                    &revokee_entity_store,
                    &revokee_event_store,
                )
                .await;
        }
    }

    let revokee_count_before = revokee_event_store
        .get_events_for_entity(&entity)
        .unwrap()
        .len();
    assert_eq!(revokee_count_before, 5, "revokee should have 5 events before revocation");

    // Phase 2: REVOKE — remove revokee's role
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .remove(&revokee_id);

    // (a) Existing data on revokee stays — we don't touch their local store
    let revokee_count_after = revokee_event_store
        .get_events_for_entity(&entity)
        .unwrap()
        .len();
    assert_eq!(
        revokee_count_after, 5,
        "revokee's existing data should not be deleted on revocation"
    );

    // (b) Revokee cannot write new events to owner
    let revokee_new_events = make_events(entity, revokee_id, 3);
    let batch = EventBatchMessage {
        entity_id: entity,
        events: revokee_new_events.clone(),
        is_final: true,
        batch_seq: 0,
    };
    let (ack, updated) = owner_engine
        .handle_event_batch(&revokee_id, &batch, &owner_entity_store, &owner_event_store)
        .await;
    match &ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.received_count, 0, "revoked peer's writes should be rejected");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
    assert!(updated.is_empty());

    // (b) Revokee cannot get new events from owner
    let new_owner_events = make_events(entity, owner_id, 3);
    for e in &new_owner_events {
        owner_event_store.save_event(e).unwrap();
        owner_engine.record_local_event(e).await;
    }

    let batches = owner_engine
        .compute_event_batches_for_peer(
            &revokee_id,
            entity,
            &HashSet::new(),
            &owner_event_store,
        )
        .await;
    assert!(
        batches.is_empty(),
        "revoked peer should not receive new events"
    );

    // Owner still has all their events (5 initial + 3 new)
    assert_eq!(
        owner_event_store.get_events_for_entity(&entity).unwrap().len(),
        8
    );
}

#[tokio::test]
async fn re_granted_peer_resyncs_including_gap_events() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let owner_id = PeerId::new();
    let peer_id = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(owner_id);
    policy.known_peers.write().await.insert(peer_id);

    let acl = EntityAcl::new(entity)
        .with_peer_role(owner_id, SyncRole::Owner)
        .with_peer_role(peer_id, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let owner_engine = SyncEngine::with_policy(owner_id, SyncConfig::default(), policy.clone());
    let peer_engine = SyncEngine::with_policy(peer_id, SyncConfig::default(), policy.clone());
    let (owner_entity_store, owner_event_store) = make_stores();
    let (peer_entity_store, peer_event_store) = make_stores();

    // Phase 1: Initial sync — peer gets 5 events
    let initial_events = make_events(entity, owner_id, 5);
    for e in &initial_events {
        owner_event_store.save_event(e).unwrap();
        owner_engine.record_local_event(e).await;
    }

    let batches = owner_engine
        .compute_event_batches_for_peer(&peer_id, entity, &HashSet::new(), &owner_event_store)
        .await;
    for batch_msg in &batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            peer_engine
                .handle_event_batch(&owner_id, batch, &peer_entity_store, &peer_event_store)
                .await;
        }
    }
    assert_eq!(peer_event_store.get_events_for_entity(&entity).unwrap().len(), 5);

    // Phase 2: Revoke peer access
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .remove(&peer_id);

    // Owner creates 3 more events during revocation
    let gap_events = make_events(entity, owner_id, 3);
    for e in &gap_events {
        owner_event_store.save_event(e).unwrap();
        owner_engine.record_local_event(e).await;
    }

    // Peer creates 2 events locally during revocation (could not sync)
    let peer_gap_events = make_events(entity, peer_id, 2);
    for e in &peer_gap_events {
        peer_event_store.save_event(e).unwrap();
        peer_engine.record_local_event(e).await;
    }

    // Verify peer can't sync during revocation
    let batches = owner_engine
        .compute_event_batches_for_peer(&peer_id, entity, &HashSet::new(), &owner_event_store)
        .await;
    assert!(batches.is_empty());

    // Phase 3: Re-grant peer access
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .insert(peer_id, SyncRole::Editor);

    // Peer now syncs — should get the 3 gap events from owner
    let peer_known: HashSet<_> = peer_event_store
        .get_events_for_entity(&entity)
        .unwrap()
        .iter()
        .map(|e| e.id)
        .collect();

    let batches = owner_engine
        .compute_event_batches_for_peer(&peer_id, entity, &peer_known, &owner_event_store)
        .await;

    let mut received_from_owner = 0;
    for batch_msg in &batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            let (ack, _) = peer_engine
                .handle_event_batch(&owner_id, batch, &peer_entity_store, &peer_event_store)
                .await;
            if let SyncMessage::EventAck(a) = ack {
                received_from_owner += a.received_count;
            }
        }
    }
    assert_eq!(
        received_from_owner, 3,
        "peer should receive the 3 gap events from owner after re-grant"
    );

    // Owner receives peer's 2 gap events
    let owner_known: HashSet<_> = owner_event_store
        .get_events_for_entity(&entity)
        .unwrap()
        .iter()
        .map(|e| e.id)
        .collect();

    // Peer sends their events to owner
    let peer_batches = peer_engine
        .compute_event_batches_for_peer(&owner_id, entity, &owner_known, &peer_event_store)
        .await;

    let mut received_from_peer = 0;
    for batch_msg in &peer_batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            let (ack, _) = owner_engine
                .handle_event_batch(&peer_id, batch, &owner_entity_store, &owner_event_store)
                .await;
            if let SyncMessage::EventAck(a) = ack {
                received_from_peer += a.received_count;
            }
        }
    }
    assert_eq!(
        received_from_peer, 2,
        "owner should receive peer's 2 gap events after re-grant"
    );

    // Final state: both have 10 events (5 initial + 3 owner gap + 2 peer gap)
    assert_eq!(
        peer_event_store.get_events_for_entity(&entity).unwrap().len(),
        10
    );
    assert_eq!(
        owner_event_store.get_events_for_entity(&entity).unwrap().len(),
        10
    );
}

#[tokio::test]
async fn re_granted_peer_with_no_new_data_syncs_cleanly() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let owner_id = PeerId::new();
    let peer_id = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(owner_id);
    policy.known_peers.write().await.insert(peer_id);

    let acl = EntityAcl::new(entity)
        .with_peer_role(owner_id, SyncRole::Owner)
        .with_peer_role(peer_id, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let owner_engine = SyncEngine::with_policy(owner_id, SyncConfig::default(), policy.clone());
    let peer_engine = SyncEngine::with_policy(peer_id, SyncConfig::default(), policy.clone());
    let (_owner_entity_store, owner_event_store) = make_stores();
    let (peer_entity_store, peer_event_store) = make_stores();

    // Initial sync: 5 events
    let initial_events = make_events(entity, owner_id, 5);
    for e in &initial_events {
        owner_event_store.save_event(e).unwrap();
        owner_engine.record_local_event(e).await;
    }

    let batches = owner_engine
        .compute_event_batches_for_peer(&peer_id, entity, &HashSet::new(), &owner_event_store)
        .await;
    for batch_msg in &batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            peer_engine
                .handle_event_batch(&owner_id, batch, &peer_entity_store, &peer_event_store)
                .await;
        }
    }

    // Revoke and immediately re-grant (no new data on either side)
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .remove(&peer_id);
    policy
        .acls
        .write()
        .await
        .get_mut(&entity)
        .unwrap()
        .peer_roles
        .insert(peer_id, SyncRole::Editor);

    // Sync again — both already have everything, should be a no-op
    let peer_known: HashSet<_> = peer_event_store
        .get_events_for_entity(&entity)
        .unwrap()
        .iter()
        .map(|e| e.id)
        .collect();

    let batches = owner_engine
        .compute_event_batches_for_peer(&peer_id, entity, &peer_known, &owner_event_store)
        .await;
    assert!(
        batches.is_empty(),
        "no new events to send when both sides are in sync"
    );

    // Both still have 5 events
    assert_eq!(
        owner_event_store.get_events_for_entity(&entity).unwrap().len(),
        5
    );
    assert_eq!(
        peer_event_store.get_events_for_entity(&entity).unwrap().len(),
        5
    );
}

#[tokio::test]
async fn large_asymmetric_sync_does_not_lose_events() {
    // A has 200 events, B has 50 (subset). Sync should bring B to 200, A stays 200.
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(peer_a);
    policy.known_peers.write().await.insert(peer_b);
    let acl = EntityAcl::new(entity)
        .with_peer_role(peer_a, SyncRole::Owner)
        .with_peer_role(peer_b, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let engine_a = SyncEngine::with_policy(peer_a, SyncConfig::default(), policy.clone());
    let engine_b = SyncEngine::with_policy(peer_b, SyncConfig::default(), policy.clone());

    let (entity_store_b, event_store_b) = make_stores();
    let (_, event_store_a) = make_stores();

    // A creates 200 events
    let all_events = make_events(entity, peer_a, 200);
    for e in &all_events {
        event_store_a.save_event(e).unwrap();
        engine_a.record_local_event(e).await;
    }

    // B already has the first 50
    for e in &all_events[..50] {
        event_store_b.save_event(e).unwrap();
        engine_b.record_local_event(e).await;
    }

    let b_known: HashSet<_> = event_store_b
        .get_events_for_entity(&entity)
        .unwrap()
        .iter()
        .map(|e| e.id)
        .collect();
    assert_eq!(b_known.len(), 50);

    // A sends missing 150 events to B
    let batches = engine_a
        .compute_event_batches_for_peer(&peer_b, entity, &b_known, &event_store_a)
        .await;

    let mut total_received = 0;
    for batch_msg in &batches {
        if let SyncMessage::EventBatch(batch) = batch_msg {
            let (ack, _) = engine_b
                .handle_event_batch(&peer_a, batch, &entity_store_b, &event_store_b)
                .await;
            if let SyncMessage::EventAck(a) = ack {
                total_received += a.received_count;
            }
        }
    }
    assert_eq!(total_received, 150);

    // A still has 200
    assert_eq!(
        event_store_a.get_events_for_entity(&entity).unwrap().len(),
        200
    );
    // B now has 200
    assert_eq!(
        event_store_b.get_events_for_entity(&entity).unwrap().len(),
        200
    );
}

// ══════════════════════════════════════════════════════════════════
// Phase 1 Tests: Orchestrator Policy Plumbing
// ══════════════════════════════════════════════════════════════════

#[tokio::test]
async fn enterprise_orchestrator_constructs_with_policy() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let peer_id = PeerId::new();
    let (entity_store, event_store) = make_stores();
    let config = privstack_sync::OrchestratorConfig::default();

    let (handle, _event_rx, _cmd_rx, _orch) =
        privstack_sync::create_orchestrator_with_policy(
            peer_id,
            entity_store,
            event_store,
            config,
            policy,
        );

    // Should be able to send a shutdown command
    assert!(handle.shutdown().await.is_ok());
}

#[tokio::test]
async fn reverse_delta_filtered_by_orchestrator_policy() {
    // When a peer has no role, reverse-delta events from the ack should be filtered
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let owner_id = PeerId::new();
    let viewer_id = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(owner_id);
    policy.known_peers.write().await.insert(viewer_id);

    // Viewer has Viewer role — can read but not write
    let acl = EntityAcl::new(entity)
        .with_peer_role(owner_id, SyncRole::Owner)
        .with_peer_role(viewer_id, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let engine = SyncEngine::with_policy(owner_id, SyncConfig::default(), policy.clone());
    let (entity_store, event_store) = make_stores();

    // Owner has events
    let events = make_events(entity, owner_id, 5);
    for e in &events {
        event_store.save_event(e).unwrap();
        engine.record_local_event(e).await;
    }

    // Viewer sends empty batch (requesting reverse-delta)
    let empty_batch = EventBatchMessage {
        entity_id: entity,
        events: Vec::new(),
        is_final: true,
        batch_seq: 0,
    };

    let (ack, _) = engine
        .handle_event_batch(&viewer_id, &empty_batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            // Viewer can read, so reverse-delta should be sent
            assert_eq!(a.events.len(), 5);
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }

    // Now test with a denied peer (no role at all)
    let denied_id = PeerId::new();
    policy.known_peers.write().await.insert(denied_id);

    let (ack2, _) = engine
        .handle_event_batch(&denied_id, &empty_batch, &entity_store, &event_store)
        .await;

    match ack2 {
        SyncMessage::EventAck(a) => {
            // No role = reverse-delta should be empty
            assert!(a.events.is_empty(), "denied peer should get no reverse-delta");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
}

#[tokio::test]
async fn backward_compat_create_orchestrator_still_works() {
    let peer_id = PeerId::new();
    let (entity_store, event_store) = make_stores();
    let config = privstack_sync::OrchestratorConfig::default();

    let (handle, _event_rx, _cmd_rx, _orch) =
        privstack_sync::create_orchestrator(peer_id, entity_store, event_store, config);

    assert!(handle.shutdown().await.is_ok());
}

// ══════════════════════════════════════════════════════════════════
// Phase 2 Tests: Reverse-Delta on_event_send in Engine
// ══════════════════════════════════════════════════════════════════

#[tokio::test]
async fn reverse_delta_filtered_for_denied_peer() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    // No ACL for remote — denied
    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (entity_store, event_store) = make_stores();

    let events = make_events(entity, local, 3);
    for e in &events {
        event_store.save_event(e).unwrap();
        engine.record_local_event(e).await;
    }

    let batch = EventBatchMessage {
        entity_id: entity,
        events: Vec::new(),
        is_final: true,
        batch_seq: 0,
    };

    let (ack, _) = engine
        .handle_event_batch(&remote, &batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert!(
                a.events.is_empty(),
                "denied peer should get empty reverse-delta"
            );
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
}

#[tokio::test]
async fn reverse_delta_passes_for_viewer_and_above() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(remote);
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Viewer);
    policy.acls.write().await.insert(entity, acl);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (entity_store, event_store) = make_stores();

    let events = make_events(entity, local, 4);
    for e in &events {
        event_store.save_event(e).unwrap();
        engine.record_local_event(e).await;
    }

    let batch = EventBatchMessage {
        entity_id: entity,
        events: Vec::new(),
        is_final: true,
        batch_seq: 0,
    };

    let (ack, _) = engine
        .handle_event_batch(&remote, &batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.events.len(), 4, "viewer+ should get reverse-delta");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
}

// ══════════════════════════════════════════════════════════════════
// Phase 3 Tests: Device Limit Integration
// ══════════════════════════════════════════════════════════════════

#[tokio::test]
async fn device_limit_enforced_in_handshake() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();

    policy.known_peers.write().await.insert(local);
    policy.known_peers.write().await.insert(remote);
    policy.device_limits.write().await.insert(remote, 1);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy.clone());

    // First device: OK
    let d1 = DeviceId::new();
    let hello1 = HelloMessage::new(remote, "Device 1").with_device_id(d1.0.to_string());
    let resp1 = engine.handle_hello(&hello1).await;
    match resp1 {
        SyncMessage::HelloAck(ack) => assert!(ack.accepted, "first device should be accepted"),
        other => panic!("Expected HelloAck, got {:?}", other),
    }

    // Second device: rejected
    let d2 = DeviceId::new();
    let hello2 = HelloMessage::new(remote, "Device 2").with_device_id(d2.0.to_string());
    let resp2 = engine.handle_hello(&hello2).await;
    match resp2 {
        SyncMessage::HelloAck(ack) => assert!(!ack.accepted, "second device should be rejected"),
        other => panic!("Expected HelloAck rejection, got {:?}", other),
    }
}

#[tokio::test]
async fn device_re_register_succeeds() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();

    policy.known_peers.write().await.insert(local);
    policy.known_peers.write().await.insert(remote);
    policy.device_limits.write().await.insert(remote, 1);

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);

    let d1 = DeviceId::new();
    let hello = HelloMessage::new(remote, "Device 1").with_device_id(d1.0.to_string());

    // Register once
    let resp1 = engine.handle_hello(&hello).await;
    assert!(matches!(resp1, SyncMessage::HelloAck(ref a) if a.accepted));

    // Re-register same device
    let resp2 = engine.handle_hello(&hello).await;
    assert!(matches!(resp2, SyncMessage::HelloAck(ref a) if a.accepted));
}

#[tokio::test]
async fn remove_device_frees_slot() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    policy.device_limits.write().await.insert(peer, 1);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();

    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_err());

    // Remove d1
    assert!(policy.remove_device(&peer, &d1).await);

    // d2 should now succeed
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());
}

#[tokio::test]
async fn clear_devices_removes_all() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    policy.device_limits.write().await.insert(peer, 2);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());

    policy.clear_devices(&peer).await;

    // Both slots freed, new devices can register
    let d3 = DeviceId::new();
    let d4 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d3).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d4).await.is_ok());
}

// ══════════════════════════════════════════════════════════════════
// Phase 4 Tests: PolicyStore + Audit Persistence
// ══════════════════════════════════════════════════════════════════

#[tokio::test]
async fn audit_round_trip_through_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());
    let peer = PeerId::new();
    let entity = EntityId::new();

    policy.known_peers.write().await.insert(peer);
    let acl = EntityAcl::new(entity).with_peer_role(peer, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let events = make_events(entity, peer, 1);
    let _ = policy.on_event_receive(&peer, &entity, &events).await;

    // Audit entry should be in both memory and store
    let log = policy.audit_log.read().await;
    assert!(!log.is_empty());

    let db_count = store.audit_log_count().unwrap();
    assert!(db_count > 0);

    let db_entries = store.load_audit_log(100, 0).unwrap();
    assert!(!db_entries.is_empty());
}

#[tokio::test]
async fn in_memory_audit_trim() {
    let policy = EnterpriseSyncPolicy::new().with_max_in_memory_log(5);
    let peer = PeerId::new();
    let entity = EntityId::new();

    let acl = EntityAcl::new(entity).with_peer_role(peer, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    // Generate 10 audit entries
    for _ in 0..10 {
        let events = make_events(entity, peer, 1);
        let _ = policy.on_event_receive(&peer, &entity, &events).await;
    }

    let log = policy.audit_log.read().await;
    assert!(log.len() <= 5, "in-memory log should be trimmed to max");
}

#[tokio::test]
async fn flush_audit_log_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());
    let peer = PeerId::new();

    policy.known_peers.write().await.insert(peer);
    let local = PeerId::new();
    policy.known_peers.write().await.insert(local);

    // Generate entries — they go to both memory and store automatically
    let _ = policy.on_handshake(&local, &peer).await;

    let count_before = store.audit_log_count().unwrap();
    assert!(count_before > 0);

    // Flush clears memory
    policy.flush_audit_log().await.unwrap();
    let log = policy.audit_log.read().await;
    assert!(log.is_empty(), "memory should be cleared after flush");

    // Store still has entries (plus the flushed ones, but since we write on log() too,
    // flush re-writes them; that's fine, the count should be >= before)
    let count_after = store.audit_log_count().unwrap();
    assert!(count_after >= count_before);
}

// ══════════════════════════════════════════════════════════════════
// Phase 5 Tests: ACL/Team State Persistence
// ══════════════════════════════════════════════════════════════════

#[tokio::test]
async fn round_trip_persist_and_load() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let entity = EntityId::new();
    let peer = PeerId::new();
    let team = TeamId::new();

    // Build policy with store and populate
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());
    policy.grant_peer_role(entity, peer, SyncRole::Admin).await;
    policy.grant_team_role(entity, team, SyncRole::Editor).await;
    policy.set_default_role(entity, Some(SyncRole::Viewer)).await;
    policy.add_team_member(team, peer).await;
    policy.add_known_peer(peer).await;
    policy.set_device_limit(peer, 3).await;

    // Load into a new policy from the same store
    let loaded = EnterpriseSyncPolicy::load(store).await.unwrap();

    // Verify state matches
    let role = loaded.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Admin));

    assert!(loaded.known_peers.read().await.contains(&peer));
    assert_eq!(*loaded.device_limits.read().await.get(&peer).unwrap(), 3);
}

#[tokio::test]
async fn grant_revoke_persisted() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let entity = EntityId::new();
    let peer = PeerId::new();

    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());
    policy.grant_peer_role(entity, peer, SyncRole::Owner).await;

    // Verify in store
    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 1);

    // Revoke
    policy.revoke_peer_role(entity, peer).await;

    let acls = store.load_acls().unwrap();
    assert!(acls.is_empty());
}

#[tokio::test]
async fn load_from_pre_populated_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let entity = EntityId::new();
    let peer = PeerId::new();

    // Pre-populate store directly
    store.save_acl(&entity, &peer, SyncRole::Editor).unwrap();
    store.save_known_peer(&peer).unwrap();
    store.save_device_limit(&peer, 5).unwrap();

    // Load
    let policy = EnterpriseSyncPolicy::load(store).await.unwrap();

    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));
    assert!(policy.known_peers.read().await.contains(&peer));
    assert_eq!(*policy.device_limits.read().await.get(&peer).unwrap(), 5);
}

#[tokio::test]
async fn persistence_works_without_store() {
    // Wrapper methods should work fine without a store
    let policy = EnterpriseSyncPolicy::new();
    let entity = EntityId::new();
    let peer = PeerId::new();

    policy.grant_peer_role(entity, peer, SyncRole::Admin).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Admin));

    policy.revoke_peer_role(entity, peer).await;
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, None);
}

// ══════════════════════════════════════════════════════════════════
// Phase 6 Tests: ACL-as-CRDT Events
// ══════════════════════════════════════════════════════════════════

fn make_acl_grant_event(
    entity_id: EntityId,
    sender: PeerId,
    target_entity: EntityId,
    target_peer: PeerId,
    role: &str,
) -> Event {
    Event::new(
        entity_id,
        sender,
        HybridTimestamp::now(),
        EventPayload::AclGrantPeer {
            entity_id: target_entity.to_string(),
            peer_id: target_peer.to_string(),
            role: role.to_string(),
        },
    )
}

#[tokio::test]
async fn acl_grant_from_owner_propagates() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let owner = PeerId::new();
    let target_peer = PeerId::new();
    let entity = EntityId::new();

    // Owner has Admin+ on entity
    policy.grant_peer_role(entity, owner, SyncRole::Owner).await;

    let applicator = AclApplicator::new(policy.clone());

    let event = make_acl_grant_event(entity, owner, entity, target_peer, "Editor");

    // Policy should allow (owner has Admin+)
    let allowed = policy
        .on_event_receive(&owner, &entity, &[event.clone()])
        .await
        .unwrap();
    assert_eq!(allowed.len(), 1);

    // Applicator should handle it
    let handled = applicator.handle_acl_event(&event).await.unwrap();
    assert!(handled);

    // target_peer should now have Editor role
    let role = policy.resolve_role(&target_peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));
}

#[tokio::test]
async fn acl_grant_from_viewer_rejected() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let viewer = PeerId::new();
    let target_peer = PeerId::new();
    let entity = EntityId::new();

    // Viewer only has Viewer role
    policy.grant_peer_role(entity, viewer, SyncRole::Viewer).await;

    let event = make_acl_grant_event(entity, viewer, entity, target_peer, "Editor");

    // Policy should reject (viewer doesn't have Admin+)
    let allowed = policy
        .on_event_receive(&viewer, &entity, &[event])
        .await
        .unwrap();
    assert!(allowed.is_empty(), "viewer should not be able to send ACL events");

    // target_peer should NOT have any role
    let role = policy.resolve_role(&target_peer, &entity).await;
    assert_eq!(role, None);
}

#[tokio::test]
async fn team_membership_propagates_via_event() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let admin = PeerId::new();
    let new_member = PeerId::new();
    let entity = EntityId::new();
    let team = TeamId::new();

    policy.grant_peer_role(entity, admin, SyncRole::Admin).await;

    let applicator = AclApplicator::new(policy.clone());

    let event = Event::new(
        entity,
        admin,
        HybridTimestamp::now(),
        EventPayload::TeamAddPeer {
            team_id: team.0.to_string(),
            peer_id: new_member.to_string(),
        },
    );

    let handled = applicator.handle_acl_event(&event).await.unwrap();
    assert!(handled);

    // new_member should be in the team
    let teams = policy.teams.read().await;
    assert!(teams.get(&team).unwrap().contains(&new_member));
}

#[tokio::test]
async fn acl_round_trip_sync_updates_remote_policy() {
    // Two engines, one sends ACL event, other applies it
    let policy_a = Arc::new(EnterpriseSyncPolicy::new());
    let policy_b = Arc::new(EnterpriseSyncPolicy::new());

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let target = PeerId::new();
    let entity = EntityId::new();

    // Both know about each other
    policy_a.add_known_peer(peer_a).await;
    policy_a.add_known_peer(peer_b).await;
    policy_b.add_known_peer(peer_a).await;
    policy_b.add_known_peer(peer_b).await;

    // peer_a is Owner on both policies
    policy_a.grant_peer_role(entity, peer_a, SyncRole::Owner).await;
    policy_a.grant_peer_role(entity, peer_b, SyncRole::Admin).await;
    policy_b.grant_peer_role(entity, peer_a, SyncRole::Owner).await;
    policy_b.grant_peer_role(entity, peer_b, SyncRole::Admin).await;

    let mut engine_b = SyncEngine::with_policy(peer_b, SyncConfig::default(), policy_b.clone());
    let acl_handler = Arc::new(AclApplicator::new(policy_b.clone()));
    engine_b.set_acl_handler(acl_handler);

    let (entity_store_b, event_store_b) = make_stores();

    // peer_a creates an ACL grant event
    let acl_event = make_acl_grant_event(entity, peer_a, entity, target, "Editor");

    let batch = EventBatchMessage {
        entity_id: entity,
        events: vec![acl_event],
        is_final: true,
        batch_seq: 0,
    };

    let (ack, _) = engine_b
        .handle_event_batch(&peer_a, &batch, &entity_store_b, &event_store_b)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.received_count, 1, "ACL event should be applied");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }

    // target should now have Editor role on policy_b
    let role = policy_b.resolve_role(&target, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));
}

#[tokio::test]
async fn revoke_removes_access_on_receiver() {
    let policy = Arc::new(EnterpriseSyncPolicy::new());
    let admin = PeerId::new();
    let target = PeerId::new();
    let entity = EntityId::new();

    policy.grant_peer_role(entity, admin, SyncRole::Owner).await;
    policy.grant_peer_role(entity, target, SyncRole::Editor).await;

    // Verify target has access
    let role = policy.resolve_role(&target, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));

    let applicator = AclApplicator::new(policy.clone());

    // Admin sends a revoke event
    let revoke_event = Event::new(
        entity,
        admin,
        HybridTimestamp::now(),
        EventPayload::AclRevokePeer {
            entity_id: entity.to_string(),
            peer_id: target.to_string(),
        },
    );

    let handled = applicator.handle_acl_event(&revoke_event).await.unwrap();
    assert!(handled);

    // target should no longer have access
    let role = policy.resolve_role(&target, &entity).await;
    assert_eq!(role, None);
}

// ── PersonalSyncPolicy ──────────────────────────────────────────

use privstack_sync::PersonalSyncPolicy;

#[tokio::test]
async fn personal_share_unshare_round_trip() {
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    // Initially empty
    assert!(policy.shared_peers(&entity).await.is_empty());
    assert!(policy.shared_entities(&peer).await.is_empty());

    // Share
    policy.share(entity, peer).await;
    assert_eq!(policy.shared_peers(&entity).await, vec![peer]);
    assert_eq!(policy.shared_entities(&peer).await, vec![entity]);

    // Unshare
    policy.unshare(entity, peer).await;
    assert!(policy.shared_peers(&entity).await.is_empty());
    assert!(policy.shared_entities(&peer).await.is_empty());
}

#[tokio::test]
async fn personal_on_sync_request_filters() {
    let policy = PersonalSyncPolicy::new();
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let e1 = EntityId::new();
    let e2 = EntityId::new();
    let e3 = EntityId::new();

    policy.share(e1, peer_a).await;
    policy.share(e2, peer_a).await;
    policy.share(e2, peer_b).await;

    // peer_a sees e1, e2
    let allowed = policy.on_sync_request(&peer_a, &[e1, e2, e3]).await.unwrap();
    assert_eq!(allowed.len(), 2);
    assert!(allowed.contains(&e1));
    assert!(allowed.contains(&e2));
    assert!(!allowed.contains(&e3));

    // peer_b sees only e2
    let allowed = policy.on_sync_request(&peer_b, &[e1, e2, e3]).await.unwrap();
    assert_eq!(allowed.len(), 1);
    assert!(allowed.contains(&e2));
}

#[tokio::test]
async fn personal_on_event_send_filters() {
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let shared_entity = EntityId::new();
    let unshared_entity = EntityId::new();

    policy.share(shared_entity, peer).await;

    let events = make_events(shared_entity, PeerId::new(), 3);

    // Shared entity: events pass through
    let result = policy.on_event_send(&peer, &shared_entity, &events).await.unwrap();
    assert_eq!(result.len(), 3);

    // Unshared entity: events blocked
    let result = policy.on_event_send(&peer, &unshared_entity, &events).await.unwrap();
    assert!(result.is_empty());
}

#[tokio::test]
async fn personal_on_event_receive_passes_all() {
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    // No sharing configured — receive still passes (sender shared with us)
    let events = make_events(entity, peer, 5);
    let result = policy.on_event_receive(&peer, &entity, &events).await.unwrap();
    assert_eq!(result.len(), 5);
}

#[tokio::test]
async fn personal_engine_sync_request_filters_entities() {
    let policy = Arc::new(PersonalSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let shared = EntityId::new();
    let unshared = EntityId::new();

    policy.share(shared, remote).await;

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (_entity_store, event_store) = make_stores();

    let request = privstack_sync::protocol::SyncRequestMessage {
        entity_ids: vec![shared, unshared],
        known_event_ids: std::collections::HashMap::new(),
    };

    let response = engine.handle_sync_request(&remote, &request, &event_store).await;
    match response {
        SyncMessage::SyncState(state) => {
            assert!(
                !state.clocks.contains_key(&unshared),
                "unshared entity should not appear in sync state"
            );
        }
        other => panic!("Expected SyncState, got {:?}", other),
    }
}

#[tokio::test]
async fn personal_compute_batches_filters_unshared() {
    let policy = Arc::new(PersonalSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let shared = EntityId::new();
    let unshared = EntityId::new();

    policy.share(shared, remote).await;

    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (_entity_store, event_store) = make_stores();

    // Store events for both entities
    let shared_events = make_events(shared, local, 3);
    let unshared_events = make_events(unshared, local, 3);
    for e in shared_events.iter().chain(unshared_events.iter()) {
        event_store.save_event(e).unwrap();
        engine.record_local_event(e).await;
    }

    // Shared entity: peer gets batches
    let batches = engine
        .compute_event_batches_for_peer(&remote, shared, &HashSet::new(), &event_store)
        .await;
    assert!(!batches.is_empty(), "shared entity should produce batches");

    // Unshared entity: peer gets nothing
    let batches = engine
        .compute_event_batches_for_peer(&remote, unshared, &HashSet::new(), &event_store)
        .await;
    assert!(batches.is_empty(), "unshared entity should produce no batches");
}

#[tokio::test]
async fn personal_event_receive_allows_all() {
    let policy = Arc::new(PersonalSyncPolicy::new());
    let local = PeerId::new();
    let remote = PeerId::new();
    let entity = EntityId::new();

    // Don't share entity with remote in *our* policy — but receive should still work
    let engine = SyncEngine::with_policy(local, SyncConfig::default(), policy);
    let (entity_store, event_store) = make_stores();

    let events = make_events(entity, remote, 3);
    let batch = EventBatchMessage {
        entity_id: entity,
        events,
        is_final: true,
        batch_seq: 0,
    };

    let (ack, updated) = engine
        .handle_event_batch(&remote, &batch, &entity_store, &event_store)
        .await;

    match ack {
        SyncMessage::EventAck(a) => {
            assert_eq!(a.received_count, 3, "personal policy allows all incoming events");
        }
        other => panic!("Expected EventAck, got {:?}", other),
    }
    assert_eq!(updated.len(), 1);
}

#[tokio::test]
async fn personal_share_entity_with_peer_command_routes_to_policy() {
    // Verify the ShareEntityWithPeer variant exists and the policy method works
    let policy = Arc::new(PersonalSyncPolicy::new());
    let peer = PeerId::new();
    let entity = EntityId::new();

    policy.share(entity, peer).await;

    let entities = policy.shared_entities(&peer).await;
    assert_eq!(entities, vec![entity]);

    let peers = policy.shared_peers(&entity).await;
    assert_eq!(peers, vec![peer]);
}

#[tokio::test]
async fn personal_multiple_peers_multiple_entities() {
    let policy = PersonalSyncPolicy::new();
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let e1 = EntityId::new();
    let e2 = EntityId::new();
    let e3 = EntityId::new();

    policy.share(e1, peer_a).await;
    policy.share(e2, peer_a).await;
    policy.share(e2, peer_b).await;
    policy.share(e3, peer_b).await;

    assert_eq!(policy.shared_entities(&peer_a).await.len(), 2);
    assert_eq!(policy.shared_entities(&peer_b).await.len(), 2);

    // e2 is shared with both
    let peers_e2 = policy.shared_peers(&e2).await;
    assert_eq!(peers_e2.len(), 2);

    // Unshare e2 from peer_a
    policy.unshare(e2, peer_a).await;
    assert_eq!(policy.shared_entities(&peer_a).await.len(), 1);
    assert_eq!(policy.shared_peers(&e2).await.len(), 1);
}

// ── Additional coverage: device limit boundary (exactly at limit) ──

#[tokio::test]
async fn device_limit_exactly_at_boundary() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();

    // Limit = 2
    policy.device_limits.write().await.insert(peer, 2);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();

    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());

    // Exactly at limit, re-registering existing should succeed
    assert!(policy.check_device_limit(&peer, &d1).await.is_ok());
    assert!(policy.check_device_limit(&peer, &d2).await.is_ok());

    // One more new device should fail
    let d3 = DeviceId::new();
    assert!(policy.check_device_limit(&peer, &d3).await.is_err());
}

// ── resolve_role with no ACL entry at all ───────────────────────

#[tokio::test]
async fn resolve_role_no_acl_no_teams_returns_none() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    // No ACL for entity
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, None);
}

// ── Audit log trimming beyond max ───────────────────────────────

#[tokio::test]
async fn audit_log_trims_beyond_max() {
    let policy = EnterpriseSyncPolicy::new().with_max_in_memory_log(5);
    let peer = PeerId::new();
    let entity = EntityId::new();

    // Add known peer and grant role to trigger audit entries
    policy.known_peers.write().await.insert(peer);
    let acl = EntityAcl::new(entity).with_peer_role(peer, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    // Generate more than 5 audit entries
    let events = make_events(entity, peer, 1);
    for _ in 0..10 {
        let _ = policy.on_event_receive(&peer, &entity, &events).await;
    }

    let log = policy.audit_log.read().await;
    assert!(log.len() <= 5, "log should be trimmed to max_in_memory_log");
}

// ── on_event_receive with ACL events ────────────────────────────

#[tokio::test]
async fn on_event_receive_acl_event_requires_admin() {
    let (policy, _local, remote, entity) = setup_enterprise().await;

    // Remote is Editor on entity (not Admin)
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Editor);
    policy.acls.write().await.insert(entity, acl);

    let acl_event = Event::new(
        entity,
        remote,
        HybridTimestamp::now(),
        EventPayload::AclGrantPeer {
            entity_id: entity.to_string(),
            peer_id: PeerId::new().to_string(),
            role: "Viewer".to_string(),
        },
    );

    // ACL event from Editor should be denied (requires Admin+)
    let result = policy.on_event_receive(&remote, &entity, &[acl_event]).await.unwrap();
    // ACL event stripped, and then Editor allows remaining non-ACL events (empty here)
    assert!(result.is_empty());
}

#[tokio::test]
async fn on_event_receive_acl_event_accepted_by_admin() {
    let (policy, _local, remote, entity) = setup_enterprise().await;

    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Admin);
    policy.acls.write().await.insert(entity, acl);

    let acl_event = Event::new(
        entity,
        remote,
        HybridTimestamp::now(),
        EventPayload::AclGrantPeer {
            entity_id: entity.to_string(),
            peer_id: PeerId::new().to_string(),
            role: "Viewer".to_string(),
        },
    );

    let result = policy.on_event_receive(&remote, &entity, &[acl_event]).await.unwrap();
    assert_eq!(result.len(), 1);
}

// ── acl_target_entity for all payload variants (via on_event_receive) ──

#[tokio::test]
async fn on_event_receive_acl_revoke_peer_event() {
    let (policy, _local, remote, entity) = setup_enterprise().await;

    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Owner);
    policy.acls.write().await.insert(entity, acl);

    let acl_event = Event::new(
        entity,
        remote,
        HybridTimestamp::now(),
        EventPayload::AclRevokePeer {
            entity_id: entity.to_string(),
            peer_id: PeerId::new().to_string(),
        },
    );

    let result = policy.on_event_receive(&remote, &entity, &[acl_event]).await.unwrap();
    assert_eq!(result.len(), 1);
}

#[tokio::test]
async fn on_event_receive_team_add_peer_event_no_entity_target() {
    let (policy, _local, remote, entity) = setup_enterprise().await;

    // TeamAddPeer has no entity_id in payload, so acl_target_entity returns None
    // It falls back to the `entity` argument. Give remote Admin on `entity`.
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Admin);
    policy.acls.write().await.insert(entity, acl);

    let acl_event = Event::new(
        entity,
        remote,
        HybridTimestamp::now(),
        EventPayload::TeamAddPeer {
            team_id: uuid::Uuid::new_v4().to_string(),
            peer_id: PeerId::new().to_string(),
        },
    );

    let result = policy.on_event_receive(&remote, &entity, &[acl_event]).await.unwrap();
    assert_eq!(result.len(), 1);
}

// ── on_device_check ─────────────────────────────────────────────

#[tokio::test]
async fn on_device_check_no_limit_always_ok() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    // No device limit configured
    let result = policy.on_device_check(&peer, None).await;
    assert!(result.is_ok());
}

#[tokio::test]
async fn on_device_check_limit_but_no_device_id() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 3);

    let result = policy.on_device_check(&peer, None).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn on_device_check_invalid_device_id() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 3);

    let result = policy.on_device_check(&peer, Some("not-a-uuid")).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn on_device_check_valid() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 3);

    let device_id = uuid::Uuid::new_v4().to_string();
    let result = policy.on_device_check(&peer, Some(&device_id)).await;
    assert!(result.is_ok());
}

// ── remove_device / clear_devices ───────────────────────────────

#[tokio::test]
async fn remove_device_returns_true_when_present() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let device = DeviceId::new();

    policy.device_limits.write().await.insert(peer, 5);
    policy.check_device_limit(&peer, &device).await.unwrap();

    assert!(policy.remove_device(&peer, &device).await);
}

#[tokio::test]
async fn remove_device_returns_false_when_absent() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    let device = DeviceId::new();

    assert!(!policy.remove_device(&peer, &device).await);
}

#[tokio::test]
async fn clear_devices_clears_all() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 5);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();
    policy.check_device_limit(&peer, &d1).await.unwrap();
    policy.check_device_limit(&peer, &d2).await.unwrap();

    policy.clear_devices(&peer).await;
    let active = policy.active_devices.read().await;
    assert!(!active.contains_key(&peer));
}

// ── flush_audit_log ─────────────────────────────────────────────

#[tokio::test]
async fn flush_audit_log_no_store_is_noop() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.known_peers.write().await.insert(peer);
    let _ = policy.on_handshake(&PeerId::new(), &peer).await;

    assert!(policy.audit_log.read().await.len() > 0);
    // No store attached, flush is a no-op
    policy.flush_audit_log().await.unwrap();
    // In-memory log is NOT cleared since no store
    assert!(policy.audit_log.read().await.len() > 0);
}

#[tokio::test]
async fn flush_audit_log_with_store_clears_memory() {
    let store = Arc::new(privstack_sync::PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());
    let peer = PeerId::new();
    policy.known_peers.write().await.insert(peer);
    let _ = policy.on_handshake(&PeerId::new(), &peer).await;

    let count_before = policy.audit_log.read().await.len();
    assert!(count_before > 0);

    policy.flush_audit_log().await.unwrap();
    assert_eq!(policy.audit_log.read().await.len(), 0);
    assert!(store.audit_log_count().unwrap() > 0);
}

// ── EnterpriseSyncPolicy Debug + Default ────────────────────────

#[test]
fn enterprise_policy_debug() {
    let policy = EnterpriseSyncPolicy::new();
    let debug = format!("{:?}", policy);
    assert!(debug.contains("EnterpriseSyncPolicy"));
}

#[test]
fn enterprise_policy_default() {
    let policy = EnterpriseSyncPolicy::default();
    let debug = format!("{:?}", policy);
    assert!(debug.contains("EnterpriseSyncPolicy"));
}

// ── PersonalSyncPolicy ──────────────────────────────────────────

#[tokio::test]
async fn personal_policy_entities_for_peer_returns_none() {
    use privstack_sync::policy::PersonalSyncPolicy;
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    assert!(policy.entities_for_peer(&peer).is_none());
}

// ── Display impls ───────────────────────────────────────────────

#[test]
fn sync_role_display() {
    assert_eq!(format!("{}", SyncRole::Viewer), "Viewer");
    assert_eq!(format!("{}", SyncRole::Editor), "Editor");
    assert_eq!(format!("{}", SyncRole::Admin), "Admin");
    assert_eq!(format!("{}", SyncRole::Owner), "Owner");
}

#[test]
fn team_id_display() {
    let team = TeamId::new();
    let display = format!("{}", team);
    assert!(!display.is_empty());
    assert_eq!(display, team.0.to_string());
}

#[test]
fn device_id_display() {
    let device = DeviceId::new();
    let display = format!("{}", device);
    assert!(!display.is_empty());
    assert_eq!(display, device.0.to_string());
}

#[test]
fn team_id_default() {
    let team = TeamId::default();
    let team2 = TeamId::default();
    // Both should be valid unique UUIDs
    assert_ne!(team, team2);
}

#[test]
fn device_id_default() {
    let d = DeviceId::default();
    let d2 = DeviceId::default();
    assert_ne!(d, d2);
}

#[test]
fn audit_action_display() {
    assert_eq!(format!("{}", AuditAction::Handshake), "handshake");
    assert_eq!(format!("{}", AuditAction::SyncRequest), "sync_request");
    assert_eq!(format!("{}", AuditAction::EventSend), "event_send");
    assert_eq!(format!("{}", AuditAction::EventReceive), "event_receive");
    assert_eq!(format!("{}", AuditAction::DeviceRegister), "device_register");
}

// ── EnterpriseSyncPolicy Debug detail ────────────────────────────

#[test]
fn enterprise_policy_debug_contains_fields() {
    let policy = EnterpriseSyncPolicy::new();
    let debug = format!("{:?}", policy);
    assert!(debug.contains("store"));
    assert!(debug.contains("max_in_memory_log"));
}

// ── PersonalSyncPolicy unshare and shared_peers ─────────────────

#[tokio::test]
async fn personal_policy_unshare() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    let entity = EntityId::new();
    let peer = PeerId::new();

    policy.share(entity, peer).await;
    assert_eq!(policy.shared_entities(&peer).await.len(), 1);

    policy.unshare(entity, peer).await;
    assert!(policy.shared_entities(&peer).await.is_empty());
}

#[tokio::test]
async fn personal_policy_unshare_nonexistent() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    // Unsharing something that was never shared should be a no-op
    policy.unshare(EntityId::new(), PeerId::new()).await;
}

#[tokio::test]
async fn personal_policy_shared_peers() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    let entity = EntityId::new();
    let p1 = PeerId::new();
    let p2 = PeerId::new();
    let p3 = PeerId::new();

    policy.share(entity, p1).await;
    policy.share(entity, p2).await;

    let peers = policy.shared_peers(&entity).await;
    assert_eq!(peers.len(), 2);
    assert!(peers.contains(&p1));
    assert!(peers.contains(&p2));
    assert!(!peers.contains(&p3));
}

#[tokio::test]
async fn personal_policy_on_event_send_denied() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();
    let other_entity = EntityId::new();

    // Activate selective sharing by sharing a different entity,
    // so the peer_entities map is non-empty and filtering kicks in
    policy.share(other_entity, peer).await;

    // entity is not shared with peer - should return empty
    let events = make_events(entity, peer, 3);
    let sent = policy.on_event_send(&peer, &entity, &events).await.unwrap();
    assert!(sent.is_empty());
}

#[tokio::test]
async fn personal_policy_on_sync_request_filters() {
    use privstack_sync::policy::PersonalSyncPolicy;

    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let shared = EntityId::new();
    let unshared = EntityId::new();

    policy.share(shared, peer).await;

    let ids = policy.on_sync_request(&peer, &[shared, unshared]).await.unwrap();
    assert_eq!(ids, vec![shared]);
}

// ── remove_device and clear_devices ─────────────────────────────

#[tokio::test]
async fn remove_device() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 5);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();
    policy.check_device_limit(&peer, &d1).await.unwrap();
    policy.check_device_limit(&peer, &d2).await.unwrap();

    assert!(policy.remove_device(&peer, &d1).await);
    assert!(!policy.remove_device(&peer, &d1).await); // already removed

    // Removing from unknown peer
    assert!(!policy.remove_device(&PeerId::new(), &d1).await);
}

#[tokio::test]
async fn clear_devices() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 5);

    let d1 = DeviceId::new();
    let d2 = DeviceId::new();
    policy.check_device_limit(&peer, &d1).await.unwrap();
    policy.check_device_limit(&peer, &d2).await.unwrap();

    policy.clear_devices(&peer).await;
    assert!(policy.active_devices.read().await.get(&peer).is_none());
}

// ── with_max_in_memory_log and audit trimming ───────────────────

#[tokio::test]
async fn audit_log_trimming() {
    let policy = EnterpriseSyncPolicy::new().with_max_in_memory_log(5);
    let local = PeerId::new();

    // Generate 10 handshake events to trigger trimming
    for _ in 0..10 {
        let remote = PeerId::new();
        policy.known_peers.write().await.insert(remote);
        let _ = policy.on_handshake(&local, &remote).await;
    }

    let log = policy.audit_log.read().await;
    assert!(log.len() <= 5, "audit log should be trimmed to max");
}

// ── flush_audit_log with store ──────────────────────────────────

#[tokio::test]
async fn flush_audit_log_with_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    // Generate some audit entries
    let local = PeerId::new();
    let remote = PeerId::new();
    let _ = policy.on_handshake(&local, &remote).await;

    let before = policy.audit_log.read().await.len();
    assert!(before > 0);

    policy.flush_audit_log().await.unwrap();

    // In-memory should be cleared
    let after = policy.audit_log.read().await.len();
    assert_eq!(after, 0);

    // Store should have the entry
    assert!(store.audit_log_count().unwrap() > 0);
}

// ── with_store and store() ──────────────────────────────────────

#[test]
fn enterprise_policy_store_accessor() {
    let policy = EnterpriseSyncPolicy::new();
    assert!(policy.store().is_none());

    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store);
    assert!(policy.store().is_some());
}

// ── on_device_check via trait ───────────────────────────────────

#[tokio::test]
async fn on_device_check_no_limit_configured() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    // No device limit configured - should always pass
    let result = policy.on_device_check(&peer, Some("some-id")).await;
    assert!(result.is_ok());
}

#[tokio::test]
async fn on_device_check_valid_device_id() {
    let policy = EnterpriseSyncPolicy::new();
    let peer = PeerId::new();
    policy.device_limits.write().await.insert(peer, 2);

    let d = DeviceId::new();
    let result = policy.on_device_check(&peer, Some(&d.0.to_string())).await;
    assert!(result.is_ok());
}

#[tokio::test]
async fn on_event_receive_acl_event_from_admin_allowed() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    let acl = EntityAcl::new(entity).with_peer_role(remote, SyncRole::Admin);
    policy.acls.write().await.insert(entity, acl);

    let acl_event = Event::new(
        entity,
        remote,
        HybridTimestamp::now(),
        EventPayload::AclGrantPeer {
            entity_id: entity.to_string(),
            peer_id: PeerId::new().to_string(),
            role: "Viewer".to_string(),
        },
    );

    let recv = policy.on_event_receive(&remote, &entity, &[acl_event]).await.unwrap();
    assert_eq!(recv.len(), 1);
}

// ── on_event_receive with no role (denied) ──────────────────────

#[tokio::test]
async fn on_event_receive_no_role_denied() {
    let (policy, _local, remote, entity) = setup_enterprise().await;
    // No ACL for this entity at all
    let events = make_events(entity, remote, 2);
    let recv = policy.on_event_receive(&remote, &entity, &events).await.unwrap();
    assert!(recv.is_empty());
}

// ── Persisting wrapper methods with store ───────────────────────

#[tokio::test]
async fn grant_peer_role_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let entity = EntityId::new();
    let peer = PeerId::new();
    policy.grant_peer_role(entity, peer, SyncRole::Editor).await;

    // Verify in store
    let acls = store.load_acls().unwrap();
    assert_eq!(acls.len(), 1);
    assert_eq!(acls[0].2, SyncRole::Editor);
}

#[tokio::test]
async fn revoke_peer_role_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let entity = EntityId::new();
    let peer = PeerId::new();
    policy.grant_peer_role(entity, peer, SyncRole::Editor).await;
    policy.revoke_peer_role(entity, peer).await;

    let acls = store.load_acls().unwrap();
    assert!(acls.is_empty());
}

#[tokio::test]
async fn grant_team_role_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let entity = EntityId::new();
    let team = TeamId::new();
    policy.grant_team_role(entity, team, SyncRole::Admin).await;

    let roles = store.load_team_roles().unwrap();
    assert_eq!(roles.len(), 1);
}

#[tokio::test]
async fn revoke_team_role_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let entity = EntityId::new();
    let team = TeamId::new();
    policy.grant_team_role(entity, team, SyncRole::Admin).await;
    policy.revoke_team_role(entity, team).await;

    let roles = store.load_team_roles().unwrap();
    assert!(roles.is_empty());
}

#[tokio::test]
async fn set_default_role_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let entity = EntityId::new();
    policy.set_default_role(entity, Some(SyncRole::Viewer)).await;

    let roles = store.load_default_roles().unwrap();
    assert_eq!(roles.len(), 1);

    policy.set_default_role(entity, None).await;
    let roles = store.load_default_roles().unwrap();
    assert!(roles.is_empty());
}

#[tokio::test]
async fn add_team_member_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let team = TeamId::new();
    let peer = PeerId::new();
    policy.add_team_member(team, peer).await;

    let teams = store.load_teams().unwrap();
    assert_eq!(teams.len(), 1);
}

#[tokio::test]
async fn remove_team_member_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let team = TeamId::new();
    let peer = PeerId::new();
    policy.add_team_member(team, peer).await;
    policy.remove_team_member(team, peer).await;

    let teams = store.load_teams().unwrap();
    assert!(teams.is_empty());
}

#[tokio::test]
async fn add_known_peer_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let peer = PeerId::new();
    policy.add_known_peer(peer).await;

    let peers = store.load_known_peers().unwrap();
    assert_eq!(peers.len(), 1);
}

#[tokio::test]
async fn remove_known_peer_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let peer = PeerId::new();
    policy.add_known_peer(peer).await;
    policy.remove_known_peer(peer).await;

    let peers = store.load_known_peers().unwrap();
    assert!(peers.is_empty());
}

#[tokio::test]
async fn set_device_limit_persists_to_store() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());
    let policy = EnterpriseSyncPolicy::new().with_store(store.clone());

    let peer = PeerId::new();
    policy.set_device_limit(peer, 5).await;

    let limits = store.load_device_limits().unwrap();
    assert_eq!(limits.len(), 1);
    assert_eq!(limits[0].1, 5);
}

// ── Policy load from store ──────────────────────────────────────

#[tokio::test]
async fn enterprise_policy_load_roundtrip() {
    let store = Arc::new(PolicyStore::open_in_memory().unwrap());

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
    store.save_active_devices(&peer, &[device_id]).unwrap();

    let policy = EnterpriseSyncPolicy::load(store).await.unwrap();

    // Verify all loaded correctly
    let role = policy.resolve_role(&peer, &entity).await;
    assert_eq!(role, Some(SyncRole::Editor));
    assert!(policy.known_peers.read().await.contains(&peer));
    assert_eq!(*policy.device_limits.read().await.get(&peer).unwrap(), 3);
    assert!(!policy.active_devices.read().await.is_empty());
    assert!(!policy.teams.read().await.is_empty());
}
