//! Adversarial tests for envelope encryption (X25519 + XSalsa20-Poly1305).
//!
//! Validates that:
//! - Wrong recipient key rejects decryption
//! - Tampered ciphertext / nonce / ephemeral key are detected
//! - Empty and large DEKs round-trip correctly
//! - Serialization preserves envelope integrity

use privstack_crypto::envelope::{generate_cloud_keypair, open_dek, seal_dek};
use privstack_crypto::{CryptoError, SealedEnvelope};

#[test]
fn seal_and_open_dek_roundtrip() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef"; // 32-byte DEK

    let envelope = seal_dek(dek, &recipient.public).unwrap();
    let opened = open_dek(&envelope, &recipient.secret).unwrap();
    assert_eq!(opened, dek);
}

#[test]
fn open_with_wrong_secret_key_fails() {
    let intended_recipient = generate_cloud_keypair();
    let wrong_recipient = generate_cloud_keypair();
    let dek = b"secret-entity-encryption-key!!!0";

    let envelope = seal_dek(dek, &intended_recipient.public).unwrap();

    let err = open_dek(&envelope, &wrong_recipient.secret).unwrap_err();
    match err {
        CryptoError::Decryption(msg) => {
            assert!(
                msg.contains("wrong key") || msg.contains("tampered"),
                "should indicate wrong key or tampered data, got: {msg}"
            );
        }
        other => panic!("expected CryptoError::Decryption, got: {other:?}"),
    }
}

#[test]
fn tampered_ciphertext_detected() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();

    // Flip a byte in the ciphertext
    if let Some(byte) = envelope.ciphertext.first_mut() {
        *byte ^= 0xFF;
    }

    let err = open_dek(&envelope, &recipient.secret).unwrap_err();
    assert!(
        matches!(err, CryptoError::Decryption(_)),
        "tampered ciphertext should fail decryption"
    );
}

#[test]
fn truncated_ciphertext_fails() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    envelope.ciphertext.truncate(5); // keep only 5 bytes

    let err = open_dek(&envelope, &recipient.secret).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn empty_ciphertext_fails() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    envelope.ciphertext.clear();

    let err = open_dek(&envelope, &recipient.secret).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn tampered_nonce_detected() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    envelope.nonce[0] ^= 0xFF;

    let err = open_dek(&envelope, &recipient.secret).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn tampered_ephemeral_key_detected() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    envelope.ephemeral_public_key[0] ^= 0xFF;

    let err = open_dek(&envelope, &recipient.secret).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn all_zeros_ephemeral_key_fails() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    envelope.ephemeral_public_key = [0u8; 32];

    let err = open_dek(&envelope, &recipient.secret).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn empty_dek_seals_and_opens() {
    let recipient = generate_cloud_keypair();
    let dek = b""; // zero-length DEK

    let envelope = seal_dek(dek, &recipient.public).unwrap();
    let opened = open_dek(&envelope, &recipient.secret).unwrap();
    assert!(opened.is_empty());
}

#[test]
fn large_dek_seals_and_opens() {
    let recipient = generate_cloud_keypair();
    let dek = vec![0xAB; 1024]; // 1KB DEK

    let envelope = seal_dek(&dek, &recipient.public).unwrap();
    let opened = open_dek(&envelope, &recipient.secret).unwrap();
    assert_eq!(opened, dek);
}

#[test]
fn each_seal_produces_unique_ciphertext() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let envelope_a = seal_dek(dek, &recipient.public).unwrap();
    let envelope_b = seal_dek(dek, &recipient.public).unwrap();

    // Each seal uses a new ephemeral key and nonce
    assert_ne!(
        envelope_a.ephemeral_public_key, envelope_b.ephemeral_public_key,
        "each seal should use a unique ephemeral key"
    );
    assert_ne!(
        envelope_a.ciphertext, envelope_b.ciphertext,
        "ciphertext should differ due to randomized encryption"
    );

    // Both should still decrypt correctly
    assert_eq!(
        open_dek(&envelope_a, &recipient.secret).unwrap(),
        dek.to_vec()
    );
    assert_eq!(
        open_dek(&envelope_b, &recipient.secret).unwrap(),
        dek.to_vec()
    );
}

#[test]
fn envelope_serialization_roundtrip() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let envelope = seal_dek(dek, &recipient.public).unwrap();
    let json = serde_json::to_string(&envelope).unwrap();
    let deserialized: SealedEnvelope = serde_json::from_str(&json).unwrap();

    let opened = open_dek(&deserialized, &recipient.secret).unwrap();
    assert_eq!(opened, dek);
}

#[test]
fn tampered_serialized_envelope_detected() {
    let recipient = generate_cloud_keypair();
    let dek = b"0123456789abcdef0123456789abcdef";

    let envelope = seal_dek(dek, &recipient.public).unwrap();
    let mut json = serde_json::to_string(&envelope).unwrap();

    // Replace a portion of the ciphertext base64 in JSON
    json = json.replacen("ciphertext\":[", "ciphertext\":[255,", 1);

    // The tampered JSON should either fail to deserialize or fail to decrypt
    if let Ok(deserialized) = serde_json::from_str::<SealedEnvelope>(&json) {
        let result = open_dek(&deserialized, &recipient.secret);
        assert!(
            result.is_err(),
            "tampered serialized envelope should fail decryption"
        );
    }
    // If deserialization itself fails, that's also acceptable
}

#[test]
fn constructed_envelope_from_scratch_fails() {
    let recipient = generate_cloud_keypair();

    // Manually construct an envelope with garbage data
    let fake_envelope = SealedEnvelope {
        ephemeral_public_key: [0x42; 32],
        nonce: [0x00; 24],
        ciphertext: vec![0xDE, 0xAD, 0xBE, 0xEF],
    };

    let err = open_dek(&fake_envelope, &recipient.secret).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn different_recipients_cannot_cross_decrypt() {
    let alice = generate_cloud_keypair();
    let bob = generate_cloud_keypair();
    let dek = b"shared-entity-key-for-alice0000!";

    // Seal for Alice
    let envelope_for_alice = seal_dek(dek, &alice.public).unwrap();

    // Alice can open it
    assert_eq!(
        open_dek(&envelope_for_alice, &alice.secret).unwrap(),
        dek.to_vec()
    );

    // Bob cannot
    assert!(open_dek(&envelope_for_alice, &bob.secret).is_err());

    // Seal for Bob
    let envelope_for_bob = seal_dek(dek, &bob.public).unwrap();

    // Bob can open it
    assert_eq!(
        open_dek(&envelope_for_bob, &bob.secret).unwrap(),
        dek.to_vec()
    );

    // Alice cannot
    assert!(open_dek(&envelope_for_bob, &alice.secret).is_err());
}
