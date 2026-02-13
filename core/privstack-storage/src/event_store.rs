//! Generic event store — persists sync events for entity replication.

use crate::error::StorageResult;
use duckdb::{params, Connection};
use privstack_types::{EntityId, Event, EventId, HybridTimestamp, PeerId};
use std::path::Path;
use std::sync::{Arc, Mutex};

/// Persists sync events for CRDT replication.
#[derive(Clone)]
pub struct EventStore {
    conn: Arc<Mutex<Connection>>,
}

impl EventStore {
    /// Opens or creates an event store at the given path.
    pub fn open(path: &Path) -> StorageResult<Self> {
        let conn = crate::open_duckdb_with_wal_recovery(path, "128MB", 1)?;
        initialize_event_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
        })
    }

    /// Opens an in-memory event store (for testing).
    pub fn open_in_memory() -> StorageResult<Self> {
        let conn = Connection::open_in_memory()?;
        initialize_event_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
        })
    }

    /// Saves an event.
    pub fn save_event(&self, event: &Event) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        let payload_json = serde_json::to_string(&event.payload)?;
        let deps_json = serde_json::to_string(&event.dependencies)?;

        conn.execute(
            r#"
            INSERT OR IGNORE INTO events (
                id, entity_id, peer_id,
                timestamp_wall, timestamp_logical,
                payload_json, dependencies_json
            ) VALUES (?, ?, ?, ?, ?, ?, ?)
            "#,
            params![
                event.id.to_string(),
                event.entity_id.to_string(),
                event.peer_id.to_string(),
                event.timestamp.wall_time() as i64,
                event.timestamp.logical() as i32,
                payload_json,
                deps_json,
            ],
        )?;
        Ok(())
    }

    /// Gets events for an entity, ordered by timestamp.
    pub fn get_events_for_entity(&self, entity_id: &EntityId) -> StorageResult<Vec<Event>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, entity_id, peer_id, timestamp_wall, timestamp_logical, payload_json, dependencies_json \
             FROM events WHERE entity_id = ? ORDER BY timestamp_wall, timestamp_logical"
        )?;

        let events = stmt
            .query_map(params![entity_id.to_string()], row_to_event)?
            .filter_map(|r| r.ok())
            .collect();

        Ok(events)
    }

    /// Gets events newer than a given timestamp from a specific peer.
    pub fn get_events_since(
        &self,
        peer_id: &PeerId,
        since: &HybridTimestamp,
    ) -> StorageResult<Vec<Event>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, entity_id, peer_id, timestamp_wall, timestamp_logical, payload_json, dependencies_json \
             FROM events WHERE peer_id = ? AND (timestamp_wall > ? OR (timestamp_wall = ? AND timestamp_logical > ?)) \
             ORDER BY timestamp_wall, timestamp_logical"
        )?;

        let events = stmt
            .query_map(
                params![
                    peer_id.to_string(),
                    since.wall_time() as i64,
                    since.wall_time() as i64,
                    since.logical() as i32,
                ],
                row_to_event,
            )?
            .filter_map(|r| r.ok())
            .collect();

        Ok(events)
    }

    /// Gets the latest event timestamp for a peer (for sync state tracking).
    pub fn get_latest_timestamp_for_peer(
        &self,
        peer_id: &PeerId,
    ) -> StorageResult<Option<HybridTimestamp>> {
        let conn = self.conn.lock().unwrap();
        let result = conn.query_row(
            "SELECT timestamp_wall, timestamp_logical FROM events WHERE peer_id = ? \
             ORDER BY timestamp_wall DESC, timestamp_logical DESC LIMIT 1",
            params![peer_id.to_string()],
            |row| {
                let wall: i64 = row.get(0)?;
                let logical: i32 = row.get(1)?;
                Ok(HybridTimestamp::new(wall as u64, logical as u32))
            },
        );

        match result {
            Ok(ts) => Ok(Some(ts)),
            Err(duckdb::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }
}

fn row_to_event(row: &duckdb::Row<'_>) -> duckdb::Result<Event> {
    let id_str: String = row.get(0)?;
    let entity_id_str: String = row.get(1)?;
    let peer_id_str: String = row.get(2)?;
    let wall: i64 = row.get(3)?;
    let logical: i32 = row.get(4)?;
    let payload_json: String = row.get(5)?;
    let deps_json: String = row.get(6)?;

    let id: EventId = id_str.parse().unwrap_or_default();
    let entity_id: EntityId = entity_id_str.parse().unwrap_or_default();
    let peer_id: PeerId = peer_id_str.parse().unwrap_or_default();
    let timestamp = HybridTimestamp::new(wall as u64, logical as u32);
    let payload = serde_json::from_str(&payload_json).unwrap_or(
        privstack_types::EventPayload::FullSnapshot {
            entity_type: "unknown".into(),
            json_data: "{}".into(),
        },
    );
    let dependencies: Vec<EventId> = serde_json::from_str(&deps_json).unwrap_or_default();

    Ok(Event {
        id,
        entity_id,
        peer_id,
        timestamp,
        payload,
        dependencies,
    })
}

fn initialize_event_schema(conn: &Connection) -> StorageResult<()> {
    conn.execute_batch(
        r#"
        CREATE TABLE IF NOT EXISTS events (
            id VARCHAR PRIMARY KEY,
            entity_id VARCHAR NOT NULL,
            peer_id VARCHAR NOT NULL,
            timestamp_wall BIGINT NOT NULL,
            timestamp_logical INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            dependencies_json TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX IF NOT EXISTS idx_events_entity ON events(entity_id);
        CREATE INDEX IF NOT EXISTS idx_events_peer ON events(peer_id);
        CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp_wall, timestamp_logical);

        CREATE TABLE IF NOT EXISTS sync_state (
            peer_id VARCHAR PRIMARY KEY,
            last_seen_wall BIGINT NOT NULL,
            last_seen_logical INTEGER NOT NULL,
            updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );
        "#,
    )?;
    Ok(())
}
