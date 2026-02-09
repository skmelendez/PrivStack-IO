# CRDTs

PrivStack uses Conflict-free Replicated Data Types to guarantee that data converges across all devices, regardless of network partitions, message ordering, or concurrent edits.

All CRDT implementations satisfy three properties:
- **Commutative** — operations can be applied in any order
- **Associative** — grouping of operations doesn't matter
- **Idempotent** — applying the same operation twice has no additional effect

These properties mean the sync engine never needs to coordinate or lock — it simply ships events and applies them, and all peers converge to the same state.

## Vector Clock

Tracks logical time across all peers. Each peer maintains a counter, and the clock is a map of `PeerId -> u64`.

```rust
pub struct VectorClock {
    entries: HashMap<PeerId, u64>,
}
```

### Causal Ordering

Comparing two vector clocks yields one of four results:

| Result | Meaning |
|---|---|
| `Before` | Clock A happened before clock B (A is dominated by B) |
| `After` | Clock A happened after clock B |
| `Concurrent` | Neither dominates — events happened independently |
| `Equal` | Identical clocks |

This is used by the sync engine to determine which events a peer is missing: if a peer's clock for an entity is `Before` the local clock, the peer needs events.

### Operations

- `increment(peer_id)` — advance this peer's counter
- `update(peer_id, value)` — set a peer's counter to a specific value
- `merge(other)` — take the max of each peer's counter from both clocks
- `dominates(other)` — true if every entry in self >= corresponding entry in other

## LWW Register (Last-Writer-Wins)

Stores a single value with a timestamp. When two writes conflict, the one with the higher timestamp wins. Ties are broken deterministically by peer ID.

```rust
pub struct LWWRegister<T> {
    value: T,
    timestamp: HybridTimestamp,
    peer_id: PeerId,
}
```

### Merge Rule

```
if remote.timestamp > local.timestamp → take remote
if remote.timestamp == local.timestamp && remote.peer_id > local.peer_id → take remote
otherwise → keep local
```

Used for: single-value properties like document titles, block types, user presence indicators, settings values.

## PN-Counter (Positive-Negative Counter)

A distributed counter that supports both increment and decrement across multiple peers without coordination.

```rust
pub struct PNCounter {
    positive: HashMap<PeerId, u64>,
    negative: HashMap<PeerId, u64>,
}
```

### How It Works

Each peer tracks its own increments and decrements separately. The counter's value is `sum(all positive) - sum(all negative)`.

Merge takes the per-peer max of both positive and negative maps:

```
merged.positive[peer] = max(local.positive[peer], remote.positive[peer])
merged.negative[peer] = max(local.negative[peer], remote.negative[peer])
```

Used for: distributed counters, budget tracking, any numeric value that multiple devices can modify concurrently.

## OR-Set (Observed-Remove Set)

A set with add-wins semantics: if one device adds an element while another removes it concurrently, the add wins.

Each add generates a unique tag (UUID v7). Removing an element removes all its current tags. If a concurrent add creates a new tag, that tag survives the remove because the removing device didn't observe it.

```rust
pub struct ORSet<T> {
    elements: HashMap<T, HashSet<Tag>>,
    tombstones: HashSet<Tag>,
}
```

### Semantics

| Operation | Effect |
|---|---|
| `add(x)` | Creates new unique tag for `x` |
| `remove(x)` | Moves all of `x`'s tags to tombstones |
| `contains(x)` | True if `x` has any live (non-tombstoned) tags |
| `merge(other)` | Union of elements, union of tombstones, remove tombstoned tags |

Used for: document collections, tag lists, block children, any set where concurrent add and remove should favor the add.

## RGA (Replicated Growable Array)

An ordered sequence CRDT for collaborative editing of lists and text. Based on the approach used by Yjs and Automerge.

Each element in the array has a unique ID:

```rust
pub struct ElementId {
    timestamp: HybridTimestamp,
    peer_id: PeerId,
    seq: u32,
}
```

### Insertion

Every insert specifies "insert after element X." The new element gets a unique ID derived from the inserting peer's clock. A special root element (timestamp=0, peer=nil, seq=0) anchors the beginning of the array.

### Deletion

Deletes are tombstones — the element's value is set to `None` but the element remains in the structure to preserve ordering for future inserts.

### Ordering

When concurrent inserts target the same position, they are ordered deterministically by `(timestamp, peer_id, seq)` using lexicographic comparison. This ensures all peers see the same final order.

Used for: ordered task lists, text editing, any sequence where multiple devices may insert at the same position concurrently.

## Property-Based Testing

The CRDT implementations are tested with property-based tests (via `proptest`) that verify:

- Commutativity: `merge(a, b) == merge(b, a)`
- Associativity: `merge(merge(a, b), c) == merge(a, merge(b, c))`
- Idempotency: `merge(a, a) == a`
- Convergence across 3+ peers with concurrent operations
