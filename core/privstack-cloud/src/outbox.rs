//! Adaptive event batching with context-aware flush strategy.
//!
//! The outbox buffers local events and flushes them to S3 at intervals
//! determined by the current collaboration context:
//! - **Solo mode**: 60s intervals or 50KB threshold (crash protection)
//! - **Collab mode**: 5s intervals or 5KB threshold (near-real-time)
//! - **Empty buffers**: Never flushed ($0.00 cost when idle)

use privstack_types::Event;
use std::time::{Duration, Instant};

/// Flush mode determined by collaboration context.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FlushMode {
    /// Entity has no other active users — relaxed flush timing.
    Solo,
    /// Another user has presence/lock on entity — aggressive flush.
    Collab,
}

/// Adaptive event outbox that batches events before S3 upload.
pub struct Outbox {
    pending_events: Vec<Event>,
    pending_size: usize,
    flush_mode: FlushMode,
    last_flush: Instant,
    collab_cooldown: Option<Instant>,
}

const SOLO_FLUSH_INTERVAL: Duration = Duration::from_secs(60);
const COLLAB_FLUSH_INTERVAL: Duration = Duration::from_secs(5);
const SOLO_SIZE_THRESHOLD: usize = 50 * 1024; // 50KB
const COLLAB_SIZE_THRESHOLD: usize = 5 * 1024; // 5KB
const COLLAB_COOLDOWN: Duration = Duration::from_secs(300); // 5 min

impl Outbox {
    pub fn new() -> Self {
        Self {
            pending_events: Vec::new(),
            pending_size: 0,
            flush_mode: FlushMode::Solo,
            last_flush: Instant::now(),
            collab_cooldown: None,
        }
    }

    /// Adds an event to the outbox buffer.
    pub fn push(&mut self, event: Event) {
        let size = serde_json::to_vec(&event).map(|v| v.len()).unwrap_or(128);
        self.pending_size += size;
        self.pending_events.push(event);
    }

    /// Returns true if the outbox should flush now.
    pub fn should_flush(&self) -> bool {
        // Never flush an empty buffer
        if self.pending_events.is_empty() {
            return false;
        }

        // Always flush if buffer exceeds max threshold
        if self.pending_size > SOLO_SIZE_THRESHOLD {
            return true;
        }

        match self.flush_mode {
            FlushMode::Solo => {
                self.time_since_last_flush() >= SOLO_FLUSH_INTERVAL
            }
            FlushMode::Collab => {
                self.time_since_last_flush() >= COLLAB_FLUSH_INTERVAL
                    || self.pending_size > COLLAB_SIZE_THRESHOLD
            }
        }
    }

    /// Updates flush mode based on entity collaboration state.
    pub fn update_flush_mode(&mut self, entity_has_other_users: bool) {
        if entity_has_other_users {
            self.flush_mode = FlushMode::Collab;
            self.collab_cooldown = Some(Instant::now() + COLLAB_COOLDOWN);
        } else if self.collab_cooldown.is_none_or(|t| Instant::now() > t) {
            self.flush_mode = FlushMode::Solo;
        }
        // Stay in Collab mode for 5 min after last collab activity
    }

    /// Takes all pending events, resetting the buffer.
    pub fn take_pending(&mut self) -> Vec<Event> {
        self.last_flush = Instant::now();
        self.pending_size = 0;
        std::mem::take(&mut self.pending_events)
    }

    /// Returns the number of pending events.
    pub fn pending_count(&self) -> usize {
        self.pending_events.len()
    }

    /// Returns the estimated buffer size in bytes.
    pub fn buffer_size(&self) -> usize {
        self.pending_size
    }

    /// Returns true if the buffer is empty.
    pub fn is_empty(&self) -> bool {
        self.pending_events.is_empty()
    }

    /// Returns the current flush mode.
    pub fn flush_mode(&self) -> FlushMode {
        self.flush_mode
    }

    fn time_since_last_flush(&self) -> Duration {
        self.last_flush.elapsed()
    }
}

impl Default for Outbox {
    fn default() -> Self {
        Self::new()
    }
}
