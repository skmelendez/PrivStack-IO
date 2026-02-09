# Entity Model

Entities are the fundamental data unit in PrivStack. Every piece of user data — a note, a task, a contact, a calendar event — is an entity.

## Entity Structure

```rust
pub struct Entity {
    pub id: String,           // UUID v7 (time-ordered)
    pub entity_type: String,  // e.g., "page", "task", "contact"
    pub data: serde_json::Value,  // Arbitrary JSON payload
    pub created_at: i64,      // Milliseconds since Unix epoch
    pub modified_at: i64,
    pub created_by: String,   // PeerId of creating device
}
```

Entities are intentionally schema-less at the storage level — the `data` field holds arbitrary JSON. Structure is imposed by schemas registered by plugins.

## Entity Schemas

Each plugin registers one or more `EntitySchema` declarations that tell the core how to index and merge entities of a given type.

### Indexed Fields

Schemas declare which JSON paths should be extracted into indexed columns for search and filtering:

```rust
pub struct IndexedField {
    pub name: String,           // Column name in DuckDB
    pub json_pointer: String,   // JSON pointer (e.g., "/title", "/due_date")
    pub field_type: FieldType,  // How to index and query
}
```

### Field Types

| Type | Description |
|---|---|
| `Text` | Full-text searchable string |
| `Tag` | Categorical label (exact match) |
| `DateTime` | Timestamp for range queries |
| `Number` | Numeric value |
| `Bool` | Boolean flag |
| `Vector` | Embedding vector for similarity search |
| `Counter` | CRDT counter (PN-Counter) |
| `Relation` | Link to another entity by ID |
| `Decimal` | High-precision number |
| `Json` | Nested JSON (stored as text) |
| `Enum` | Constrained set of values |
| `GeoPoint` | Latitude/longitude pair |
| `Duration` | Time duration |

### Merge Strategy

Each schema declares how concurrent modifications should be resolved:

| Strategy | Behavior |
|---|---|
| `LwwDocument` | Last-writer-wins on the entire document. Simplest; remote replaces local if its `modified_at` is newer. |
| `LwwPerField` | Last-writer-wins per top-level JSON field. If the remote document is newer overall, each field is compared and the newer version kept. Finer granularity than whole-document LWW. |
| `Custom` | The plugin provides a `PluginDomainHandler::merge()` function that receives both versions and returns the merged result. Used for domain-specific logic like budget reconciliation. |

## Domain Handlers

Plugins can optionally implement the `PluginDomainHandler` trait to participate in the entity lifecycle:

```rust
pub trait PluginDomainHandler: Send + Sync {
    fn validate(&self, entity: &Entity) -> Result<()>;
    fn on_after_load(&self, entity: &mut Entity) -> Result<()>;
    fn merge(&self, local: &Entity, remote: &Entity) -> Result<Entity>;
}
```

| Method | When Called | Purpose |
|---|---|---|
| `validate` | Before persist | Enforce invariants, reject invalid data |
| `on_after_load` | After read | Compute derived fields, enrich data |
| `merge` | During sync conflict | Custom conflict resolution |

## Identifiers

All IDs use UUID v7, which provides:

- **Time-ordered** — IDs sort chronologically, useful for pagination and natural ordering
- **Globally unique** — no coordination needed between devices
- **Compatible** — standard UUID format works with any database or transport

Three ID types with newtype wrappers for type safety:

| Type | Purpose |
|---|---|
| `EntityId` | Identifies a data entity |
| `PeerId` | Identifies a device/peer (compatible with libp2p PeerId) |
| `EventId` | Identifies a single mutation event |

## Timestamps

PrivStack uses a Hybrid Logical Clock (`HybridTimestamp`) that combines wall-clock time with a logical counter:

```rust
pub struct HybridTimestamp {
    pub millis: u64,   // Wall-clock milliseconds since Unix epoch
    pub counter: u32,  // Logical counter for same-millisecond ordering
}
```

This ensures:

- Events created on the same device are always ordered (logical counter increments)
- Events from different devices with synchronized clocks are ordered by wall time
- Clock skew is handled gracefully — the `receive()` method merges remote and local timestamps, advancing the logical counter when wall clocks disagree

Based on the "Logical Physical Clocks" paper by Kulkarni et al.

## Entity Registry

The core maintains an `EntityRegistry` that maps entity types to their schemas and optional domain handlers. When an FFI call arrives, the registry routes it to the correct schema for index extraction, merge strategy selection, and validation.

## Events

Every mutation to an entity produces an `EventPayload`:

| Event | Description |
|---|---|
| `EntityCreated` | New entity with initial data |
| `EntityUpdated` | Modified entity with new data |
| `EntityDeleted` | Entity removed |
| `FullSnapshot` | Complete entity state (treated as update during sync) |
| `AclGrantPeer` | Grant access to a specific peer |
| `AclRevokePeer` | Revoke peer access |
| `AclGrantTeam` | Grant access to a team |
| `AclRevokeTeam` | Revoke team access |
| `AclSetDefault` | Set default access level for an entity |
| `TeamAddPeer` | Add a peer to a team |
| `TeamRemovePeer` | Remove a peer from a team |

Each event carries a dependency list (vector of `EventId`s) for causal ordering during sync.
