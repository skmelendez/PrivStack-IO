//! Cloud sync engine for PrivStack.
//!
//! Provides S3-backed encrypted sync with:
//! - Adaptive outbox batching (context-aware flush intervals)
//! - STS credential management with auto-refresh
//! - API client for the Express control plane
//! - Per-entity sharing via envelope encryption
//! - Blob sync for file attachments
//! - Compaction for storage efficiency

pub mod api_client;
pub mod blob_sync;
pub mod compaction;
pub mod config;
pub mod credential_manager;
pub mod envelope;
pub mod error;
pub mod outbox;
pub mod s3_transport;
pub mod sharing;
pub mod sync_engine;
pub mod types;

pub use config::CloudConfig;
pub use error::CloudError;
pub use types::*;
