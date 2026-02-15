//! Adversarial tests for the outbox batching system.
//!
//! Tests event ordering guarantees, exact flush threshold boundaries,
//! collab mode cooldown timing, buffer size accumulation accuracy,
//! and edge cases around empty/single-event buffers.

use privstack_cloud::outbox::{FlushMode, Outbox};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};


fn make_event_for_entity(entity_id: EntityId) -> Event {
    Event::new(
        entity_id,
        PeerId::new(),
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "test".into(),
            json_data: "{}".into(),
        },
    )
}

fn make_event() -> Event {
    make_event_for_entity(EntityId::new())
}

fn make_sized_event(size_hint: usize) -> Event {
    let data = "x".repeat(size_hint);
    Event::new(
        EntityId::new(),
        PeerId::new(),
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "test".into(),
            json_data: data,
        },
    )
}

// ── Event Ordering ──

#[test]
fn take_pending_preserves_insertion_order() {
    let mut outbox = Outbox::new();

    // Push events with identifiable entity IDs
    let ids: Vec<EntityId> = (0..10).map(|_| EntityId::new()).collect();
    let id_strings: Vec<String> = ids.iter().map(|id| id.to_string()).collect();

    for id in &ids {
        outbox.push(make_event_for_entity(*id));
    }

    let events = outbox.take_pending();
    let result_ids: Vec<String> = events.iter().map(|e| e.entity_id.to_string()).collect();

    assert_eq!(result_ids, id_strings, "events must be returned in insertion order");
}

#[test]
fn multiple_take_pending_cycles_maintain_order() {
    let mut outbox = Outbox::new();

    // Cycle 1
    let id1 = EntityId::new();
    let id2 = EntityId::new();
    outbox.push(make_event_for_entity(id1));
    outbox.push(make_event_for_entity(id2));
    let batch1 = outbox.take_pending();
    assert_eq!(batch1.len(), 2);
    assert_eq!(batch1[0].entity_id, id1);
    assert_eq!(batch1[1].entity_id, id2);

    // Cycle 2
    let id3 = EntityId::new();
    outbox.push(make_event_for_entity(id3));
    let batch2 = outbox.take_pending();
    assert_eq!(batch2.len(), 1);
    assert_eq!(batch2[0].entity_id, id3);
}

// ── Solo Mode Flush Thresholds ──

#[test]
fn solo_mode_does_not_flush_below_50kb() {
    let mut outbox = Outbox::new();
    // Push events but stay under 50KB
    for _ in 0..5 {
        outbox.push(make_sized_event(1000)); // ~1KB each
    }

    assert!(outbox.buffer_size() < 50 * 1024);
    assert!(
        !outbox.should_flush(),
        "solo mode should not flush under 50KB threshold (interval not elapsed)"
    );
}

#[test]
fn solo_mode_flushes_at_50kb_threshold() {
    let mut outbox = Outbox::new();
    // Push enough to exceed 50KB
    for _ in 0..12 {
        outbox.push(make_sized_event(5000)); // ~5KB each → ~60KB total
    }

    assert!(outbox.buffer_size() > 50 * 1024);
    assert!(
        outbox.should_flush(),
        "solo mode should flush when buffer exceeds 50KB"
    );
}

// ── Collab Mode Flush Thresholds ──

#[test]
fn collab_mode_flushes_above_5kb() {
    let mut outbox = Outbox::new();
    outbox.update_flush_mode(true); // enter collab

    for _ in 0..3 {
        outbox.push(make_sized_event(2000)); // ~2KB each → ~6KB
    }

    assert!(outbox.buffer_size() > 5 * 1024);
    assert!(
        outbox.should_flush(),
        "collab mode should flush when buffer exceeds 5KB"
    );
}

#[test]
fn collab_mode_does_not_flush_below_5kb_without_time_elapsed() {
    let mut outbox = Outbox::new();
    outbox.update_flush_mode(true);

    outbox.push(make_sized_event(100)); // tiny event

    assert!(outbox.buffer_size() < 5 * 1024);
    assert!(
        !outbox.should_flush(),
        "collab mode should not flush below 5KB threshold when interval hasn't elapsed"
    );
}

// ── Collab Mode Cooldown ──

#[test]
fn collab_mode_stays_active_during_5min_cooldown() {
    let mut outbox = Outbox::new();

    outbox.update_flush_mode(true); // trigger collab
    assert_eq!(outbox.flush_mode(), FlushMode::Collab);

    // Try to go back to solo immediately
    outbox.update_flush_mode(false);
    assert_eq!(
        outbox.flush_mode(),
        FlushMode::Collab,
        "should remain in collab during 5-minute cooldown"
    );
}

#[test]
fn collab_mode_resets_cooldown_on_new_collab_activity() {
    let mut outbox = Outbox::new();

    outbox.update_flush_mode(true); // start collab
    outbox.update_flush_mode(false); // try to leave → stays collab (cooldown)
    outbox.update_flush_mode(true); // new collab activity → reset cooldown
    outbox.update_flush_mode(false); // try again → still in cooldown

    assert_eq!(
        outbox.flush_mode(),
        FlushMode::Collab,
        "cooldown should reset when new collab activity occurs"
    );
}

// ── Buffer Size Accuracy ──

#[test]
fn buffer_size_resets_after_take() {
    let mut outbox = Outbox::new();
    outbox.push(make_event());
    assert!(outbox.buffer_size() > 0);

    outbox.take_pending();
    assert_eq!(outbox.buffer_size(), 0);
}

#[test]
fn buffer_size_accumulates_correctly() {
    let mut outbox = Outbox::new();

    outbox.push(make_event());
    let size_after_1 = outbox.buffer_size();

    outbox.push(make_event());
    let size_after_2 = outbox.buffer_size();

    outbox.push(make_event());
    let size_after_3 = outbox.buffer_size();

    assert!(size_after_2 > size_after_1);
    assert!(size_after_3 > size_after_2);
}

#[test]
fn buffer_size_matches_estimated_serialized_size() {
    let mut outbox = Outbox::new();
    let event = make_sized_event(1000);
    let expected_size = serde_json::to_vec(&event).unwrap().len();

    outbox.push(event);

    // Buffer size should be close to serialized size (within JSON overhead)
    let actual = outbox.buffer_size();
    assert!(
        actual >= expected_size - 10 && actual <= expected_size + 10,
        "buffer_size ({actual}) should approximate serialized size ({expected_size})"
    );
}

// ── Edge Cases ──

#[test]
fn take_pending_on_empty_returns_empty_vec() {
    let mut outbox = Outbox::new();
    let events = outbox.take_pending();
    assert!(events.is_empty());
}

#[test]
fn double_take_returns_empty_second_time() {
    let mut outbox = Outbox::new();
    outbox.push(make_event());

    let batch1 = outbox.take_pending();
    assert_eq!(batch1.len(), 1);

    let batch2 = outbox.take_pending();
    assert!(batch2.is_empty());
}

#[test]
fn pending_count_and_is_empty_consistent() {
    let mut outbox = Outbox::new();

    assert!(outbox.is_empty());
    assert_eq!(outbox.pending_count(), 0);

    outbox.push(make_event());
    assert!(!outbox.is_empty());
    assert_eq!(outbox.pending_count(), 1);

    outbox.push(make_event());
    assert_eq!(outbox.pending_count(), 2);

    outbox.take_pending();
    assert!(outbox.is_empty());
    assert_eq!(outbox.pending_count(), 0);
}

#[test]
fn should_flush_never_for_empty_buffer() {
    let mut outbox = Outbox::new();

    // Solo mode, empty buffer
    assert!(!outbox.should_flush());

    // Collab mode, empty buffer
    outbox.update_flush_mode(true);
    assert!(!outbox.should_flush());
}

#[test]
fn many_small_events_accumulate_to_threshold() {
    let mut outbox = Outbox::new();

    // Push many tiny events to eventually exceed solo threshold
    let mut count = 0;
    while outbox.buffer_size() <= 50 * 1024 {
        outbox.push(make_sized_event(500));
        count += 1;
        if count > 200 {
            break; // safety valve
        }
    }

    assert!(
        outbox.should_flush(),
        "accumulated small events should eventually trigger flush"
    );
}

// ── Flush Mode Defaults ──

#[test]
fn new_outbox_defaults_to_solo_mode() {
    let outbox = Outbox::new();
    assert_eq!(outbox.flush_mode(), FlushMode::Solo);
}

#[test]
fn default_outbox_defaults_to_solo_mode() {
    let outbox = Outbox::default();
    assert_eq!(outbox.flush_mode(), FlushMode::Solo);
}
