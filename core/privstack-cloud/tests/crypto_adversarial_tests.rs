//! Adversarial tests for ChaCha20-Poly1305 encryption/decryption.
//!
//! Tests wrong-key decryption, ciphertext tampering, nonce corruption,
//! truncation attacks, and boundary conditions. These validate the
//! guarantees relied on by the cloud sync engine for per-entity encryption.

use privstack_crypto::{
    decrypt, decrypt_string, encrypt, encrypt_string, generate_random_key, CryptoError,
    DerivedKey, EncryptedData,
};

// ── Wrong Key ──

#[test]
fn decrypt_with_wrong_key_returns_error() {
    let key_a = generate_random_key();
    let key_b = generate_random_key();
    let plaintext = b"sensitive entity data that must not leak";

    let encrypted = encrypt(&key_a, plaintext).unwrap();
    let err = decrypt(&key_b, &encrypted).unwrap_err();

    match err {
        CryptoError::Decryption(msg) => {
            assert!(
                msg.contains("wrong key") || msg.contains("tampered"),
                "should indicate wrong key, got: {msg}"
            );
        }
        other => panic!("expected CryptoError::Decryption, got: {other:?}"),
    }
}

#[test]
fn decrypt_string_with_wrong_key_returns_error() {
    let key_a = generate_random_key();
    let key_b = generate_random_key();

    let encrypted = encrypt_string(&key_a, "secret text").unwrap();
    assert!(decrypt_string(&key_b, &encrypted).is_err());
}

// ── Ciphertext Tampering ──

#[test]
fn single_bit_flip_in_ciphertext_detected() {
    let key = generate_random_key();
    let encrypted = encrypt(&key, b"integrity-protected data").unwrap();

    let mut tampered = encrypted.clone();
    if let Some(byte) = tampered.ciphertext.last_mut() {
        *byte ^= 0x01; // single bit flip
    }

    assert!(
        decrypt(&key, &tampered).is_err(),
        "single bit flip must be detected by Poly1305 tag"
    );
}

#[test]
fn every_byte_position_tampering_detected() {
    let key = generate_random_key();
    let encrypted = encrypt(&key, b"test data for position tampering").unwrap();

    for i in 0..encrypted.ciphertext.len() {
        let mut tampered = encrypted.clone();
        tampered.ciphertext[i] ^= 0xFF;
        assert!(
            decrypt(&key, &tampered).is_err(),
            "tampering at byte {i} should be detected"
        );
    }
}

#[test]
fn appended_bytes_detected() {
    let key = generate_random_key();
    let mut encrypted = encrypt(&key, b"original data").unwrap();
    encrypted.ciphertext.push(0xFF); // append extra byte

    assert!(decrypt(&key, &encrypted).is_err());
}

// ── Nonce Tampering ──

#[test]
fn wrong_nonce_decryption_fails() {
    let key = generate_random_key();
    let mut encrypted = encrypt(&key, b"nonce-critical data").unwrap();
    encrypted.nonce[0] ^= 0xFF;

    assert!(decrypt(&key, &encrypted).is_err());
}

#[test]
fn all_zero_nonce_decryption_fails() {
    let key = generate_random_key();
    let mut encrypted = encrypt(&key, b"nonce should be random").unwrap();
    encrypted.nonce = [0u8; 12];

    // Replacing the nonce should cause authentication failure
    assert!(decrypt(&key, &encrypted).is_err());
}

// ── Truncation ──

#[test]
fn truncated_ciphertext_fails() {
    let key = generate_random_key();
    let mut encrypted = encrypt(&key, b"data that will be truncated").unwrap();
    encrypted.ciphertext.truncate(5);

    assert!(decrypt(&key, &encrypted).is_err());
}

#[test]
fn empty_ciphertext_fails() {
    let key = generate_random_key();
    let mut encrypted = encrypt(&key, b"will be emptied").unwrap();
    encrypted.ciphertext.clear();

    assert!(decrypt(&key, &encrypted).is_err());
}

// ── Boundary Conditions ──

#[test]
fn encrypt_decrypt_empty_plaintext() {
    let key = generate_random_key();
    let encrypted = encrypt(&key, b"").unwrap();
    let decrypted = decrypt(&key, &encrypted).unwrap();
    assert!(decrypted.is_empty());
}

#[test]
fn encrypt_decrypt_single_byte() {
    let key = generate_random_key();
    let encrypted = encrypt(&key, &[0x42]).unwrap();
    let decrypted = decrypt(&key, &encrypted).unwrap();
    assert_eq!(decrypted, vec![0x42]);
}

#[test]
fn encrypt_decrypt_large_plaintext() {
    let key = generate_random_key();
    let large = vec![0xAB; 1024 * 1024]; // 1MB
    let encrypted = encrypt(&key, &large).unwrap();
    let decrypted = decrypt(&key, &encrypted).unwrap();
    assert_eq!(decrypted, large);
}

#[test]
fn encrypt_produces_unique_ciphertexts() {
    let key = generate_random_key();
    let plaintext = b"same plaintext encrypted twice";

    let enc_a = encrypt(&key, plaintext).unwrap();
    let enc_b = encrypt(&key, plaintext).unwrap();

    assert_ne!(enc_a.nonce, enc_b.nonce, "nonces should differ");
    assert_ne!(enc_a.ciphertext, enc_b.ciphertext, "ciphertexts should differ");

    // Both should decrypt to the same plaintext
    assert_eq!(decrypt(&key, &enc_a).unwrap(), plaintext);
    assert_eq!(decrypt(&key, &enc_b).unwrap(), plaintext);
}

// ── Constructed / Malicious EncryptedData ──

#[test]
fn garbage_encrypted_data_fails() {
    let key = generate_random_key();
    let garbage = EncryptedData {
        nonce: [0xDE; 12],
        ciphertext: vec![0xAD, 0xBE, 0xEF, 0x00],
    };

    assert!(decrypt(&key, &garbage).is_err());
}

#[test]
fn ciphertext_from_different_plaintext_not_interchangeable() {
    let key = generate_random_key();
    let enc_a = encrypt(&key, b"message A").unwrap();
    let enc_b = encrypt(&key, b"message B").unwrap();

    // Swap ciphertexts but keep nonces — should fail auth
    let franken = EncryptedData {
        nonce: enc_a.nonce,
        ciphertext: enc_b.ciphertext.clone(),
    };

    assert!(decrypt(&key, &franken).is_err());
}

// ── Serialization ──

#[test]
fn encrypted_data_json_roundtrip() {
    let key = generate_random_key();
    let encrypted = encrypt(&key, b"serialize me").unwrap();

    let json = serde_json::to_vec(&encrypted).unwrap();
    let deserialized: EncryptedData = serde_json::from_slice(&json).unwrap();

    let decrypted = decrypt(&key, &deserialized).unwrap();
    assert_eq!(decrypted, b"serialize me");
}

#[test]
fn encrypted_data_base64_roundtrip() {
    let key = generate_random_key();
    let encrypted = encrypt(&key, b"base64 test").unwrap();

    let b64 = encrypted.to_base64();
    let restored = EncryptedData::from_base64(&b64).unwrap();

    let decrypted = decrypt(&key, &restored).unwrap();
    assert_eq!(decrypted, b"base64 test");
}

#[test]
fn invalid_base64_returns_error() {
    let err = EncryptedData::from_base64("not-valid-base64!!!").unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

#[test]
fn too_short_base64_returns_error() {
    // Less than NONCE_SIZE + TAG_SIZE bytes when decoded
    use base64::{engine::general_purpose::STANDARD, Engine};
    let short = STANDARD.encode([0u8; 10]); // only 10 bytes
    let err = EncryptedData::from_base64(&short).unwrap_err();
    assert!(matches!(err, CryptoError::Decryption(_)));
}

// ── Key Properties ──

#[test]
fn derived_key_debug_does_not_leak_bytes() {
    let key = generate_random_key();
    let debug_str = format!("{key:?}");
    assert!(
        debug_str.contains("REDACTED"),
        "debug output should not contain key bytes"
    );
    assert!(
        !debug_str.contains("0x"),
        "debug output should not contain hex bytes"
    );
}

#[test]
fn different_keys_from_same_generator() {
    let key_a = generate_random_key();
    let key_b = generate_random_key();
    assert_ne!(
        key_a.as_bytes(),
        key_b.as_bytes(),
        "random keys should be unique"
    );
}

// ── Cross-Entity Isolation ──

#[test]
fn entity_a_key_cannot_decrypt_entity_b_data() {
    let entity_a_key = generate_random_key();
    let entity_b_key = generate_random_key();

    let entity_a_data = encrypt(&entity_a_key, b"entity A private data").unwrap();
    let entity_b_data = encrypt(&entity_b_key, b"entity B private data").unwrap();

    // Cross-decryption must fail
    assert!(
        decrypt(&entity_b_key, &entity_a_data).is_err(),
        "entity B's key must not decrypt entity A's data"
    );
    assert!(
        decrypt(&entity_a_key, &entity_b_data).is_err(),
        "entity A's key must not decrypt entity B's data"
    );

    // Self-decryption must succeed
    assert_eq!(
        decrypt(&entity_a_key, &entity_a_data).unwrap(),
        b"entity A private data"
    );
    assert_eq!(
        decrypt(&entity_b_key, &entity_b_data).unwrap(),
        b"entity B private data"
    );
}

/// Simulates what the sync engine does: serialize events, encrypt with DEK,
/// serialize the EncryptedData, and then reverse it all.
#[test]
fn full_batch_encrypt_decrypt_pipeline() {
    use serde::{Deserialize, Serialize};

    #[derive(Debug, Serialize, Deserialize, PartialEq)]
    struct FakeEvent {
        id: String,
        data: String,
    }

    let events = vec![
        FakeEvent {
            id: "1".into(),
            data: "created".into(),
        },
        FakeEvent {
            id: "2".into(),
            data: "updated".into(),
        },
    ];

    let dek = generate_random_key();

    // Encrypt pipeline (matches sync_engine::flush_outbox)
    let serialized = serde_json::to_vec(&events).unwrap();
    let encrypted = encrypt(&dek, &serialized).unwrap();
    let encrypted_bytes = serde_json::to_vec(&encrypted).unwrap();

    // Decrypt pipeline (matches sync_engine::poll_and_apply)
    let deserialized_enc: EncryptedData = serde_json::from_slice(&encrypted_bytes).unwrap();
    let plaintext = decrypt(&dek, &deserialized_enc).unwrap();
    let deserialized_events: Vec<FakeEvent> = serde_json::from_slice(&plaintext).unwrap();

    assert_eq!(deserialized_events, events);
}

/// Same pipeline but with wrong DEK — must fail cleanly.
#[test]
fn full_batch_pipeline_wrong_dek_fails_cleanly() {
    let dek_correct = generate_random_key();
    let dek_wrong = generate_random_key();

    let serialized = serde_json::to_vec(&["event1", "event2"]).unwrap();
    let encrypted = encrypt(&dek_correct, &serialized).unwrap();
    let encrypted_bytes = serde_json::to_vec(&encrypted).unwrap();

    let deserialized_enc: EncryptedData = serde_json::from_slice(&encrypted_bytes).unwrap();
    let result = decrypt(&dek_wrong, &deserialized_enc);

    assert!(result.is_err(), "wrong DEK must fail, not silently produce garbage");
}
