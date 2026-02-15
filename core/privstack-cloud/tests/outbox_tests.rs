use privstack_cloud::outbox::{FlushMode, Outbox};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};

fn make_event() -> Event {
    Event::new(
        EntityId::new(),
        PeerId::new(),
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "test".into(),
            json_data: "{}".into(),
        },
    )
}

fn make_large_event(size_hint: usize) -> Event {
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

#[test]
fn new_outbox_is_empty() {
    let outbox = Outbox::new();
    assert!(outbox.is_empty());
    assert_eq!(outbox.pending_count(), 0);
    assert_eq!(outbox.buffer_size(), 0);
}

#[test]
fn default_equals_new() {
    let outbox = Outbox::default();
    assert!(outbox.is_empty());
    assert_eq!(outbox.flush_mode(), FlushMode::Solo);
}

#[test]
fn push_increments_count_and_size() {
    let mut outbox = Outbox::new();
    outbox.push(make_event());
    assert_eq!(outbox.pending_count(), 1);
    assert!(outbox.buffer_size() > 0);
}

#[test]
fn should_flush_empty_returns_false() {
    let outbox = Outbox::new();
    assert!(!outbox.should_flush());
}

#[test]
fn should_flush_large_solo_buffer_returns_true() {
    let mut outbox = Outbox::new();
    // Push enough to exceed solo threshold (50KB)
    for _ in 0..10 {
        outbox.push(make_large_event(6000));
    }
    assert!(outbox.buffer_size() > 50 * 1024);
    assert!(outbox.should_flush());
}

#[test]
fn should_flush_collab_above_threshold() {
    let mut outbox = Outbox::new();
    outbox.update_flush_mode(true);
    assert_eq!(outbox.flush_mode(), FlushMode::Collab);
    // Push enough to exceed collab threshold (5KB)
    for _ in 0..3 {
        outbox.push(make_large_event(2000));
    }
    assert!(outbox.buffer_size() > 5 * 1024);
    assert!(outbox.should_flush());
}

#[test]
fn take_pending_returns_all_and_resets() {
    let mut outbox = Outbox::new();
    outbox.push(make_event());
    outbox.push(make_event());
    assert_eq!(outbox.pending_count(), 2);

    let events = outbox.take_pending();
    assert_eq!(events.len(), 2);
    assert!(outbox.is_empty());
    assert_eq!(outbox.buffer_size(), 0);
}

#[test]
fn update_flush_mode_to_collab() {
    let mut outbox = Outbox::new();
    assert_eq!(outbox.flush_mode(), FlushMode::Solo);
    outbox.update_flush_mode(true);
    assert_eq!(outbox.flush_mode(), FlushMode::Collab);
}

#[test]
fn update_flush_mode_stays_collab_during_cooldown() {
    let mut outbox = Outbox::new();
    outbox.update_flush_mode(true);
    assert_eq!(outbox.flush_mode(), FlushMode::Collab);
    // Immediately try to switch back to solo — should stay in collab (5 min cooldown)
    outbox.update_flush_mode(false);
    assert_eq!(outbox.flush_mode(), FlushMode::Collab);
}

#[test]
fn buffer_size_accumulates() {
    let mut outbox = Outbox::new();
    outbox.push(make_event());
    let size1 = outbox.buffer_size();
    outbox.push(make_event());
    let size2 = outbox.buffer_size();
    assert!(size2 > size1);
}

#[test]
fn flush_mode_defaults_to_solo() {
    let outbox = Outbox::new();
    assert_eq!(outbox.flush_mode(), FlushMode::Solo);
}

#[test]
fn should_flush_solo_just_pushed_returns_false() {
    let mut outbox = Outbox::new();
    outbox.push(make_event());
    // Solo mode: 60s interval, small buffer — should not flush
    assert!(!outbox.should_flush());
}
