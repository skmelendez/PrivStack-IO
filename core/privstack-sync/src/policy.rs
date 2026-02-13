//! Sync policy — role-based access control, team grouping, device limits, and audit logging.
//!
//! The `SyncPolicy` trait provides hooks at each stage of the sync protocol.
//! `AllowAllPolicy` is the default (backward-compatible, no restrictions).
//! `EnterpriseSyncPolicy` enforces ACLs, team membership, device limits, and audit trails.

use crate::error::SyncError;
use crate::policy_store::PolicyStore;
use async_trait::async_trait;
use privstack_types::{EntityId, Event, EventPayload, PeerId};
use std::collections::{HashMap, HashSet};
use std::fmt;
use std::sync::Arc;
use tokio::sync::RwLock;

/// Unique identifier for a team.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct TeamId(pub uuid::Uuid);

impl TeamId {
    pub fn new() -> Self {
        Self(uuid::Uuid::new_v4())
    }
}

impl Default for TeamId {
    fn default() -> Self {
        Self::new()
    }
}

impl fmt::Display for TeamId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// Unique identifier for a device (distinct from PeerId which identifies a user/peer).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct DeviceId(pub uuid::Uuid);

impl DeviceId {
    pub fn new() -> Self {
        Self(uuid::Uuid::new_v4())
    }
}

impl Default for DeviceId {
    fn default() -> Self {
        Self::new()
    }
}

impl fmt::Display for DeviceId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// Role in the sync access control hierarchy.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum SyncRole {
    Viewer,
    Editor,
    Admin,
    Owner,
}

impl fmt::Display for SyncRole {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SyncRole::Viewer => write!(f, "Viewer"),
            SyncRole::Editor => write!(f, "Editor"),
            SyncRole::Admin => write!(f, "Admin"),
            SyncRole::Owner => write!(f, "Owner"),
        }
    }
}

/// Access control list for a single entity.
pub struct EntityAcl {
    pub entity_id: EntityId,
    /// Default role for peers not explicitly listed. `None` means deny by default.
    pub default_role: Option<SyncRole>,
    /// Per-peer role overrides.
    pub peer_roles: HashMap<PeerId, SyncRole>,
    /// Per-team role grants.
    pub team_roles: HashMap<TeamId, SyncRole>,
}

impl EntityAcl {
    pub fn new(entity_id: EntityId) -> Self {
        Self {
            entity_id,
            default_role: None,
            peer_roles: HashMap::new(),
            team_roles: HashMap::new(),
        }
    }

    pub fn with_default_role(mut self, role: SyncRole) -> Self {
        self.default_role = Some(role);
        self
    }

    pub fn with_peer_role(mut self, peer: PeerId, role: SyncRole) -> Self {
        self.peer_roles.insert(peer, role);
        self
    }

    pub fn with_team_role(mut self, team: TeamId, role: SyncRole) -> Self {
        self.team_roles.insert(team, role);
        self
    }
}

/// Action recorded in the audit log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuditAction {
    Handshake,
    SyncRequest,
    EventSend,
    EventReceive,
    DeviceRegister,
}

impl fmt::Display for AuditAction {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            AuditAction::Handshake => write!(f, "handshake"),
            AuditAction::SyncRequest => write!(f, "sync_request"),
            AuditAction::EventSend => write!(f, "event_send"),
            AuditAction::EventReceive => write!(f, "event_receive"),
            AuditAction::DeviceRegister => write!(f, "device_register"),
        }
    }
}

/// Decision recorded in the audit log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuditDecision {
    Allowed,
    Denied,
    Filtered,
}

/// A single audit log entry.
#[derive(Debug, Clone)]
pub struct AuditEntry {
    pub peer: PeerId,
    pub entity: Option<EntityId>,
    pub action: AuditAction,
    pub decision: AuditDecision,
    pub detail: String,
    pub timestamp: std::time::SystemTime,
}

/// Policy hooks called at each stage of the sync protocol.
#[async_trait]
pub trait SyncPolicy: Send + Sync {
    /// Called during handshake. Return `Err` to reject the peer.
    async fn on_handshake(&self, local: &PeerId, remote: &PeerId) -> Result<(), SyncError>;

    /// Called when a peer requests entities. Returns the subset of entity IDs the peer may access.
    async fn on_sync_request(
        &self,
        peer: &PeerId,
        entity_ids: &[EntityId],
    ) -> Result<Vec<EntityId>, SyncError>;

    /// Called before sending events to a peer. Returns the subset of events allowed.
    async fn on_event_send(
        &self,
        peer: &PeerId,
        entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError>;

    /// Called when receiving events from a peer. Returns the subset of events to apply.
    async fn on_event_receive(
        &self,
        peer: &PeerId,
        entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError>;

    /// Returns the entity IDs a specific peer may access, if the policy tracks
    /// per-peer sharing. Default: `None` (no filtering — use shared_entities as-is).
    fn entities_for_peer(&self, _peer: &PeerId) -> Option<Vec<EntityId>> {
        None
    }

    /// Called during handshake to check device limits. Default: always OK.
    async fn on_device_check(
        &self,
        _peer: &PeerId,
        _device_id: Option<&str>,
    ) -> Result<(), SyncError> {
        Ok(())
    }
}

// ── AllowAllPolicy ──────────────────────────────────────────────

/// Default policy that permits everything. Backward-compatible with pre-policy behavior.
pub struct AllowAllPolicy;

#[async_trait]
impl SyncPolicy for AllowAllPolicy {
    async fn on_handshake(&self, _local: &PeerId, _remote: &PeerId) -> Result<(), SyncError> {
        Ok(())
    }

    async fn on_sync_request(
        &self,
        _peer: &PeerId,
        entity_ids: &[EntityId],
    ) -> Result<Vec<EntityId>, SyncError> {
        Ok(entity_ids.to_vec())
    }

    async fn on_event_send(
        &self,
        _peer: &PeerId,
        _entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError> {
        Ok(events.to_vec())
    }

    async fn on_event_receive(
        &self,
        _peer: &PeerId,
        _entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError> {
        Ok(events.to_vec())
    }
}

// ── EnterpriseSyncPolicy ────────────────────────────────────────

/// Enterprise policy with ACLs, teams, device limits, and audit logging.
pub struct EnterpriseSyncPolicy {
    /// ACLs keyed by entity ID.
    pub acls: Arc<RwLock<HashMap<EntityId, EntityAcl>>>,
    /// Team membership: team → set of peers.
    pub teams: Arc<RwLock<HashMap<TeamId, HashSet<PeerId>>>>,
    /// Maximum devices per peer (from license tier).
    pub device_limits: Arc<RwLock<HashMap<PeerId, usize>>>,
    /// Currently active devices per peer.
    pub active_devices: Arc<RwLock<HashMap<PeerId, HashSet<DeviceId>>>>,
    /// Peers allowed to connect (if empty, membership check is skipped for handshake).
    pub known_peers: Arc<RwLock<HashSet<PeerId>>>,
    /// Audit log (in-memory).
    pub audit_log: Arc<RwLock<Vec<AuditEntry>>>,
    /// Optional persistent store for audit + state.
    store: Option<Arc<PolicyStore>>,
    /// Maximum in-memory audit log entries before trimming.
    max_in_memory_log: usize,
}

impl EnterpriseSyncPolicy {
    pub fn new() -> Self {
        Self {
            acls: Arc::new(RwLock::new(HashMap::new())),
            teams: Arc::new(RwLock::new(HashMap::new())),
            device_limits: Arc::new(RwLock::new(HashMap::new())),
            active_devices: Arc::new(RwLock::new(HashMap::new())),
            known_peers: Arc::new(RwLock::new(HashSet::new())),
            audit_log: Arc::new(RwLock::new(Vec::new())),
            store: None,
            max_in_memory_log: 10_000,
        }
    }

    /// Attaches a persistent store for audit logging and state persistence.
    pub fn with_store(mut self, store: Arc<PolicyStore>) -> Self {
        self.store = Some(store);
        self
    }

    /// Sets the maximum number of in-memory audit log entries.
    pub fn with_max_in_memory_log(mut self, max: usize) -> Self {
        self.max_in_memory_log = max;
        self
    }

    /// Returns a reference to the attached store, if any.
    pub fn store(&self) -> Option<&Arc<PolicyStore>> {
        self.store.as_ref()
    }

    /// Flushes all in-memory audit log entries to the persistent store.
    /// Clears the in-memory log after flushing. No-op if no store is attached.
    pub async fn flush_audit_log(&self) -> Result<(), SyncError> {
        let store = match &self.store {
            Some(s) => s.clone(),
            None => return Ok(()),
        };
        let mut log = self.audit_log.write().await;
        for entry in log.iter() {
            store.save_audit_entry(entry)?;
        }
        log.clear();
        Ok(())
    }

    /// Loads an `EnterpriseSyncPolicy` from a `PolicyStore`, populating all
    /// in-memory state from the database. The store is attached for ongoing persistence.
    pub async fn load(store: Arc<PolicyStore>) -> Result<Self, SyncError> {
        let policy = Self::new().with_store(store.clone());

        // Load ACLs (peer roles)
        for (entity_id, peer_id, role) in store.load_acls()? {
            let mut acls = policy.acls.write().await;
            let acl = acls.entry(entity_id).or_insert_with(|| EntityAcl::new(entity_id));
            acl.peer_roles.insert(peer_id, role);
        }

        // Load default roles
        for (entity_id, role) in store.load_default_roles()? {
            let mut acls = policy.acls.write().await;
            let acl = acls.entry(entity_id).or_insert_with(|| EntityAcl::new(entity_id));
            acl.default_role = Some(role);
        }

        // Load team roles
        for (entity_id, team_id_str, role) in store.load_team_roles()? {
            let team_id = TeamId(uuid::Uuid::parse_str(&team_id_str).map_err(|e| {
                SyncError::Storage(format!("invalid team_id: {e}"))
            })?);
            let mut acls = policy.acls.write().await;
            let acl = acls.entry(entity_id).or_insert_with(|| EntityAcl::new(entity_id));
            acl.team_roles.insert(team_id, role);
        }

        // Load team memberships
        for (team_id_str, peer_id) in store.load_teams()? {
            let team_id = TeamId(uuid::Uuid::parse_str(&team_id_str).map_err(|e| {
                SyncError::Storage(format!("invalid team_id: {e}"))
            })?);
            let mut teams = policy.teams.write().await;
            teams.entry(team_id).or_default().insert(peer_id);
        }

        // Load device limits
        for (peer_id, max) in store.load_device_limits()? {
            policy.device_limits.write().await.insert(peer_id, max);
        }

        // Load known peers
        for peer_id in store.load_known_peers()? {
            policy.known_peers.write().await.insert(peer_id);
        }

        // Load active devices
        for (peer_id, device_id_str) in store.load_active_devices()? {
            let device_id = DeviceId(uuid::Uuid::parse_str(&device_id_str).map_err(|e| {
                SyncError::Storage(format!("invalid device_id: {e}"))
            })?);
            policy
                .active_devices
                .write()
                .await
                .entry(peer_id)
                .or_default()
                .insert(device_id);
        }

        Ok(policy)
    }

    // ── Persisting wrapper methods ───────────────────────────────

    /// Grants a peer a role on an entity. Persists to store if attached.
    pub async fn grant_peer_role(
        &self,
        entity_id: EntityId,
        peer_id: PeerId,
        role: SyncRole,
    ) {
        {
            let mut acls = self.acls.write().await;
            let acl = acls.entry(entity_id).or_insert_with(|| EntityAcl::new(entity_id));
            acl.peer_roles.insert(peer_id, role);
        }
        if let Some(store) = &self.store {
            let _ = store.save_acl(&entity_id, &peer_id, role);
        }
    }

    /// Revokes a peer's role on an entity. Persists to store if attached.
    pub async fn revoke_peer_role(&self, entity_id: EntityId, peer_id: PeerId) {
        {
            let mut acls = self.acls.write().await;
            if let Some(acl) = acls.get_mut(&entity_id) {
                acl.peer_roles.remove(&peer_id);
            }
        }
        if let Some(store) = &self.store {
            let _ = store.remove_acl(&entity_id, &peer_id);
        }
    }

    /// Grants a team a role on an entity. Persists to store if attached.
    pub async fn grant_team_role(
        &self,
        entity_id: EntityId,
        team_id: TeamId,
        role: SyncRole,
    ) {
        {
            let mut acls = self.acls.write().await;
            let acl = acls.entry(entity_id).or_insert_with(|| EntityAcl::new(entity_id));
            acl.team_roles.insert(team_id, role);
        }
        if let Some(store) = &self.store {
            let _ = store.save_team_role(&entity_id, &team_id.0.to_string(), role);
        }
    }

    /// Revokes a team's role on an entity. Persists to store if attached.
    pub async fn revoke_team_role(&self, entity_id: EntityId, team_id: TeamId) {
        {
            let mut acls = self.acls.write().await;
            if let Some(acl) = acls.get_mut(&entity_id) {
                acl.team_roles.remove(&team_id);
            }
        }
        if let Some(store) = &self.store {
            let _ = store.remove_team_role(&entity_id, &team_id.0.to_string());
        }
    }

    /// Sets the default role for an entity. Persists to store if attached.
    pub async fn set_default_role(&self, entity_id: EntityId, role: Option<SyncRole>) {
        {
            let mut acls = self.acls.write().await;
            let acl = acls.entry(entity_id).or_insert_with(|| EntityAcl::new(entity_id));
            acl.default_role = role;
        }
        if let Some(store) = &self.store {
            match role {
                Some(r) => { let _ = store.save_default_role(&entity_id, r); }
                None => { let _ = store.remove_default_role(&entity_id); }
            }
        }
    }

    /// Adds a peer to a team. Persists to store if attached.
    pub async fn add_team_member(&self, team_id: TeamId, peer_id: PeerId) {
        {
            let mut teams = self.teams.write().await;
            teams.entry(team_id).or_default().insert(peer_id);
        }
        if let Some(store) = &self.store {
            let _ = store.save_team_member(&team_id.0.to_string(), &peer_id);
        }
    }

    /// Removes a peer from a team. Persists to store if attached.
    pub async fn remove_team_member(&self, team_id: TeamId, peer_id: PeerId) {
        {
            let mut teams = self.teams.write().await;
            if let Some(members) = teams.get_mut(&team_id) {
                members.remove(&peer_id);
            }
        }
        if let Some(store) = &self.store {
            let _ = store.remove_team_member(&team_id.0.to_string(), &peer_id);
        }
    }

    /// Adds a peer to the known peers set. Persists to store if attached.
    pub async fn add_known_peer(&self, peer_id: PeerId) {
        self.known_peers.write().await.insert(peer_id);
        if let Some(store) = &self.store {
            let _ = store.save_known_peer(&peer_id);
        }
    }

    /// Removes a peer from the known peers set. Persists to store if attached.
    pub async fn remove_known_peer(&self, peer_id: PeerId) {
        self.known_peers.write().await.remove(&peer_id);
        if let Some(store) = &self.store {
            let _ = store.remove_known_peer(&peer_id);
        }
    }

    /// Sets a device limit for a peer. Persists to store if attached.
    pub async fn set_device_limit(&self, peer_id: PeerId, max_devices: usize) {
        self.device_limits.write().await.insert(peer_id, max_devices);
        if let Some(store) = &self.store {
            let _ = store.save_device_limit(&peer_id, max_devices);
        }
    }

    /// Resolve the effective role for a peer on a given entity.
    /// Peer-specific role takes precedence over team role, which takes precedence over default.
    pub async fn resolve_role(&self, peer: &PeerId, entity: &EntityId) -> Option<SyncRole> {
        let acls = self.acls.read().await;
        let acl = match acls.get(entity) {
            Some(a) => a,
            None => return None,
        };

        // Peer-specific override takes precedence
        if let Some(&role) = acl.peer_roles.get(peer) {
            return Some(role);
        }

        // Team role (take highest)
        let teams = self.teams.read().await;
        let mut best_team_role: Option<SyncRole> = None;
        for (team_id, role) in &acl.team_roles {
            if let Some(members) = teams.get(team_id) {
                if members.contains(peer) {
                    best_team_role = Some(match best_team_role {
                        Some(existing) if existing > *role => existing,
                        _ => *role,
                    });
                }
            }
        }
        if let Some(role) = best_team_role {
            return Some(role);
        }

        acl.default_role
    }

    /// Check if a peer can register a new device. Returns error if limit exceeded.
    pub async fn check_device_limit(
        &self,
        peer: &PeerId,
        device: &DeviceId,
    ) -> Result<(), SyncError> {
        let limits = self.device_limits.read().await;
        let limit = match limits.get(peer) {
            Some(&l) => l,
            None => return Ok(()), // no limit configured
        };
        drop(limits);

        let mut active = self.active_devices.write().await;
        let devices = active.entry(*peer).or_default();

        // Already registered
        if devices.contains(device) {
            return Ok(());
        }

        if devices.len() >= limit {
            self.log(
                *peer,
                None,
                AuditAction::DeviceRegister,
                AuditDecision::Denied,
                format!("device limit {} exceeded", limit),
            )
            .await;
            return Err(SyncError::PolicyDenied {
                reason: format!(
                    "device limit exceeded: {} active, limit {}",
                    devices.len(),
                    limit
                ),
            });
        }

        devices.insert(*device);
        Ok(())
    }

    /// Removes a specific device from a peer's active device set.
    /// Returns `true` if the device was present and removed.
    pub async fn remove_device(&self, peer: &PeerId, device: &DeviceId) -> bool {
        let mut active = self.active_devices.write().await;
        if let Some(devices) = active.get_mut(peer) {
            devices.remove(device)
        } else {
            false
        }
    }

    /// Clears all active devices for a peer.
    pub async fn clear_devices(&self, peer: &PeerId) {
        let mut active = self.active_devices.write().await;
        active.remove(peer);
    }

    async fn log(
        &self,
        peer: PeerId,
        entity: Option<EntityId>,
        action: AuditAction,
        decision: AuditDecision,
        detail: String,
    ) {
        let entry = AuditEntry {
            peer,
            entity,
            action,
            decision,
            detail,
            timestamp: std::time::SystemTime::now(),
        };

        // Persist to store if available
        if let Some(store) = &self.store {
            if let Err(e) = store.save_audit_entry(&entry) {
                tracing::warn!("Failed to persist audit entry: {}", e);
            }
        }

        let mut log = self.audit_log.write().await;
        log.push(entry);

        // Trim in-memory log if over max
        if log.len() > self.max_in_memory_log {
            let drain_count = log.len() - self.max_in_memory_log;
            log.drain(..drain_count);
        }
    }
}

impl Default for EnterpriseSyncPolicy {
    fn default() -> Self {
        Self::new()
    }
}

impl std::fmt::Debug for EnterpriseSyncPolicy {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EnterpriseSyncPolicy")
            .field("store", &self.store.is_some())
            .field("max_in_memory_log", &self.max_in_memory_log)
            .finish()
    }
}

#[async_trait]
impl SyncPolicy for EnterpriseSyncPolicy {
    async fn on_handshake(&self, _local: &PeerId, remote: &PeerId) -> Result<(), SyncError> {
        let known = self.known_peers.read().await;
        // If known_peers is non-empty, enforce membership
        if !known.is_empty() && !known.contains(remote) {
            self.log(
                *remote,
                None,
                AuditAction::Handshake,
                AuditDecision::Denied,
                "unknown peer".into(),
            )
            .await;
            return Err(SyncError::PolicyDenied {
                reason: "unknown peer".into(),
            });
        }
        self.log(
            *remote,
            None,
            AuditAction::Handshake,
            AuditDecision::Allowed,
            "handshake accepted".into(),
        )
        .await;
        Ok(())
    }

    async fn on_sync_request(
        &self,
        peer: &PeerId,
        entity_ids: &[EntityId],
    ) -> Result<Vec<EntityId>, SyncError> {
        let mut allowed = Vec::new();
        for eid in entity_ids {
            let role = self.resolve_role(peer, eid).await;
            let decision = if role.is_some() {
                allowed.push(*eid);
                AuditDecision::Allowed
            } else {
                AuditDecision::Denied
            };
            self.log(
                *peer,
                Some(*eid),
                AuditAction::SyncRequest,
                decision,
                format!("role={:?}", role),
            )
            .await;
        }
        Ok(allowed)
    }

    async fn on_event_send(
        &self,
        peer: &PeerId,
        entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError> {
        let role = self.resolve_role(peer, entity).await;
        // Send events to the peer only if they have at least Viewer access (they can read)
        let decision = if role.is_some() {
            AuditDecision::Allowed
        } else {
            AuditDecision::Denied
        };
        self.log(
            *peer,
            Some(*entity),
            AuditAction::EventSend,
            decision.clone(),
            format!("role={:?}, count={}", role, events.len()),
        )
        .await;
        if role.is_some() {
            Ok(events.to_vec())
        } else {
            Ok(Vec::new())
        }
    }

    async fn on_device_check(
        &self,
        peer: &PeerId,
        device_id: Option<&str>,
    ) -> Result<(), SyncError> {
        // Only enforce if this peer has a device limit configured
        let has_limit = self.device_limits.read().await.contains_key(peer);
        if !has_limit {
            return Ok(());
        }

        let device_id_str = match device_id {
            Some(id) => id,
            None => {
                self.log(
                    *peer,
                    None,
                    AuditAction::DeviceRegister,
                    AuditDecision::Denied,
                    "device limit configured but no device_id provided".into(),
                )
                .await;
                return Err(SyncError::PolicyDenied {
                    reason: "device limit configured but no device_id provided".into(),
                });
            }
        };

        let device = DeviceId(uuid::Uuid::parse_str(device_id_str).map_err(|_| {
            SyncError::PolicyDenied {
                reason: format!("invalid device_id: {device_id_str}"),
            }
        })?);

        self.check_device_limit(peer, &device).await
    }

    async fn on_event_receive(
        &self,
        peer: &PeerId,
        entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError> {
        // For ACL events, check Admin+ authority on the target entity
        let mut filtered_events = Vec::new();
        for event in events {
            if crate::acl_applicator::is_acl_event(&event.payload) {
                // Extract the target entity from the ACL payload
                let target_entity = acl_target_entity(&event.payload);
                let check_entity = target_entity.as_ref().unwrap_or(entity);
                let acl_role = self.resolve_role(peer, check_entity).await;
                match acl_role {
                    Some(r) if r >= SyncRole::Admin => {
                        filtered_events.push(event.clone());
                    }
                    _ => {
                        self.log(
                            *peer,
                            Some(*check_entity),
                            AuditAction::EventReceive,
                            AuditDecision::Denied,
                            format!("ACL event requires Admin+, peer has {:?}", acl_role),
                        )
                        .await;
                    }
                }
                continue;
            }
            filtered_events.push(event.clone());
        }

        // Now apply normal role-based filtering on non-ACL events
        let events = &filtered_events;
        let role = self.resolve_role(peer, entity).await;
        match role {
            Some(r) if r >= SyncRole::Editor => {
                // Editor and above can write
                self.log(
                    *peer,
                    Some(*entity),
                    AuditAction::EventReceive,
                    AuditDecision::Allowed,
                    format!("role={}, count={}", r, events.len()),
                )
                .await;
                // Filter events: only accept events for the correct entity
                let filtered: Vec<Event> = events
                    .iter()
                    .filter(|e| e.entity_id == *entity)
                    .cloned()
                    .collect();
                Ok(filtered)
            }
            Some(r) => {
                // Viewer — strip all write events
                self.log(
                    *peer,
                    Some(*entity),
                    AuditAction::EventReceive,
                    AuditDecision::Filtered,
                    format!("role={}, viewer cannot write, stripped {} events", r, events.len()),
                )
                .await;
                Ok(Vec::new())
            }
            None => {
                self.log(
                    *peer,
                    Some(*entity),
                    AuditAction::EventReceive,
                    AuditDecision::Denied,
                    format!("no role, stripped {} events", events.len()),
                )
                .await;
                Ok(Vec::new())
            }
        }
    }
}

// ── PersonalSyncPolicy ──────────────────────────────────────────

/// Lightweight sharing policy for non-enterprise personal users.
/// Tracks which entities are shared with which peers — no teams, no device limits, no audit log.
pub struct PersonalSyncPolicy {
    /// peer → set of entities shared with that peer.
    peer_entities: RwLock<HashMap<PeerId, HashSet<EntityId>>>,
}

impl PersonalSyncPolicy {
    pub fn new() -> Self {
        Self {
            peer_entities: RwLock::new(HashMap::new()),
        }
    }

    /// Share an entity with a specific peer.
    pub async fn share(&self, entity_id: EntityId, peer_id: PeerId) {
        self.peer_entities
            .write()
            .await
            .entry(peer_id)
            .or_default()
            .insert(entity_id);
    }

    /// Unshare an entity from a specific peer.
    pub async fn unshare(&self, entity_id: EntityId, peer_id: PeerId) {
        let mut map = self.peer_entities.write().await;
        if let Some(set) = map.get_mut(&peer_id) {
            set.remove(&entity_id);
            // Keep the empty set — an empty set means "no access for this peer",
            // whereas a missing key means "no policy configured" (allow all).
        }
    }

    /// Returns all peers that have access to a given entity.
    pub async fn shared_peers(&self, entity_id: &EntityId) -> Vec<PeerId> {
        let map = self.peer_entities.read().await;
        map.iter()
            .filter(|(_, entities)| entities.contains(entity_id))
            .map(|(peer, _)| *peer)
            .collect()
    }

    /// Returns all entities shared with a given peer.
    pub async fn shared_entities(&self, peer_id: &PeerId) -> Vec<EntityId> {
        let map = self.peer_entities.read().await;
        map.get(peer_id)
            .map(|set| set.iter().copied().collect())
            .unwrap_or_default()
    }

    /// Returns true if selective per-peer sharing is configured.
    /// When false, all entities sync with all trusted peers.
    pub async fn has_selective_sharing(&self) -> bool {
        !self.peer_entities.read().await.is_empty()
    }
}

impl Default for PersonalSyncPolicy {
    fn default() -> Self {
        Self::new()
    }
}

impl std::fmt::Debug for PersonalSyncPolicy {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PersonalSyncPolicy").finish()
    }
}

#[async_trait]
impl SyncPolicy for PersonalSyncPolicy {
    async fn on_handshake(&self, _local: &PeerId, _remote: &PeerId) -> Result<(), SyncError> {
        // Pairing gate lives in orchestrator, not policy
        Ok(())
    }

    async fn on_sync_request(
        &self,
        peer: &PeerId,
        entity_ids: &[EntityId],
    ) -> Result<Vec<EntityId>, SyncError> {
        let map = self.peer_entities.read().await;
        // No selective sharing configured → allow all entities
        if map.is_empty() {
            return Ok(entity_ids.to_vec());
        }
        let peer_set = map.get(peer);
        let allowed = entity_ids
            .iter()
            .filter(|eid| peer_set.is_some_and(|s| s.contains(eid)))
            .copied()
            .collect();
        Ok(allowed)
    }

    async fn on_event_send(
        &self,
        peer: &PeerId,
        entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError> {
        let map = self.peer_entities.read().await;
        // No selective sharing configured → allow all events
        if map.is_empty() {
            return Ok(events.to_vec());
        }
        let allowed = map.get(peer).is_some_and(|s| s.contains(entity));
        if allowed {
            Ok(events.to_vec())
        } else {
            Ok(Vec::new())
        }
    }

    async fn on_event_receive(
        &self,
        peer: &PeerId,
        entity: &EntityId,
        events: &[Event],
    ) -> Result<Vec<Event>, SyncError> {
        let map = self.peer_entities.read().await;
        // No selective sharing configured → accept all events
        if map.is_empty() {
            return Ok(events.to_vec());
        }
        // Only accept events for entities we share with this peer
        let allowed = map.get(peer).is_some_and(|s| s.contains(entity));
        if allowed {
            Ok(events.to_vec())
        } else {
            Ok(Vec::new())
        }
    }

    fn entities_for_peer(&self, _peer: &PeerId) -> Option<Vec<EntityId>> {
        // Implemented via async method; orchestrator calls shared_entities() directly
        None
    }
}

/// Extracts the target entity ID from an ACL event payload, if present.
fn acl_target_entity(payload: &EventPayload) -> Option<EntityId> {
    let id_str = match payload {
        EventPayload::AclGrantPeer { entity_id, .. }
        | EventPayload::AclRevokePeer { entity_id, .. }
        | EventPayload::AclGrantTeam { entity_id, .. }
        | EventPayload::AclRevokeTeam { entity_id, .. }
        | EventPayload::AclSetDefault { entity_id, .. } => Some(entity_id.as_str()),
        _ => None,
    };
    id_str.and_then(|s| s.parse::<EntityId>().ok())
}
