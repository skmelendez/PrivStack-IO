# Storage

PrivStack uses DuckDB as its embedded storage backend across three specialized stores.

## Why DuckDB

DuckDB is an embedded OLAP (Online Analytical Processing) database. It was chosen over SQLite for:

- Columnar storage — efficient for analytics queries across many entities
- Native array and JSON types — good fit for entity data and event dependencies
- Vectorized query execution — fast aggregation for dashboards and search
- Future AI features — vector similarity search, embedding storage

## Entity Store

The primary store for all user data.

### Schema

```sql
CREATE TABLE entities (
    id              VARCHAR PRIMARY KEY,
    entity_type     VARCHAR,
    data_json       TEXT,           -- JSON payload (encrypted when vault is unlocked)
    title           VARCHAR,        -- Extracted from /title pointer
    body            VARCHAR,        -- Extracted from /body pointer
    tags            ARRAY,          -- Extracted from /tags pointer
    is_trashed      BOOLEAN,
    is_favorite     BOOLEAN,
    created_at      BIGINT,
    modified_at     BIGINT,
    created_by      VARCHAR,
    search_text     TEXT            -- Combined text for full-text search
)
```

### Field Extraction

When a plugin registers an entity schema with indexed fields, the store extracts values from the JSON payload using JSON pointers and writes them to dedicated columns. This enables:

- Queries on indexed fields without decrypting the full payload
- Full-text search via the `search_text` column
- Filtering by tags, dates, booleans, and numbers

### Encryption Integration

The `data_json` column holds encrypted data when the vault is unlocked. Indexed columns remain plaintext for query performance.

On read, the store checks if `data_json` appears to be base64-encoded. If so, it decrypts via the `DataEncryptor`. If not, it returns the raw JSON. This provides backward compatibility with unencrypted data.

### Additional Tables

**Vector embeddings:**
```sql
CREATE TABLE entity_vectors (
    entity_id   VARCHAR,
    field_name  VARCHAR,
    vector      FLOAT[],
    PRIMARY KEY (entity_id, field_name)
)
```

**Entity relations:**
```sql
CREATE TABLE entity_links (
    source_id       VARCHAR,
    target_id       VARCHAR,
    field_name      VARCHAR,
    source_type     VARCHAR,
    target_type     VARCHAR
)
```

## Event Store

An append-only log of all mutation events, used for sync replay and catchup.

```sql
CREATE TABLE events (
    id              UUID PRIMARY KEY,
    entity_id       UUID,
    peer_id         UUID,
    timestamp       BIGINT,
    payload_type    VARCHAR,        -- e.g., "EntityCreated", "EntityUpdated"
    payload_json    TEXT,
    dependencies    ARRAY(UUID)     -- Causal dependencies (previous EventIds)
)
```

Events are never deleted during normal operation. The event store is the source of truth for sync — peers exchange vector clocks and request missing events by querying this store.

## Blob Store

A namespace-scoped store for binary data (images, attachments, files).

```sql
CREATE TABLE blobs (
    namespace       VARCHAR,
    blob_id         VARCHAR,
    encrypted_data  BLOB,
    size            BIGINT,
    content_hash    VARCHAR,        -- SHA-256 of plaintext (before encryption)
    metadata_json   VARCHAR,
    created_at      BIGINT,
    modified_at     BIGINT,
    PRIMARY KEY (namespace, blob_id)
)
```

### Features

- **Namespace isolation** — each plugin gets its own namespace, preventing cross-plugin blob collisions
- **Content-hash dedup** — the SHA-256 hash is computed on the plaintext before encryption, so identical content is detected even after password changes (since re-encryption produces different ciphertext)
- **Optional encryption** — blobs are encrypted via the same `DataEncryptor` trait if a vault is active

## WAL Recovery

DuckDB uses a write-ahead log (WAL) for crash safety. If the application crashes or is killed, a `.wal` file may be left alongside the database.

PrivStack handles this with `open_duckdb_with_wal_recovery()`:

1. Attempt to open the database normally
2. If open fails and a `.wal` file exists, delete the WAL and retry
3. This handles unclean shutdown without data loss (the WAL may be incomplete, but the main database file is consistent)

## Data Directory

The default location varies by platform:

| Platform | Default Path |
|---|---|
| macOS | `~/.privstack` |
| Windows | `%APPDATA%/PrivStack` |
| Linux | `~/.privstack` |

Users can configure a custom data directory through the settings panel. The directory contains the DuckDB database file, plugin cache, and app settings JSON.

## Workspaces

PrivStack supports multiple data directories (workspaces). Switching workspaces triggers:

1. Shutdown of the current Rust runtime
2. Re-initialization with the new database path
3. Full plugin re-initialization (fresh state per workspace)
4. The SDK host acquires a write lock during the switch, blocking all FFI calls until the new workspace is ready
