use privstack_crypto::envelope::{
    decrypt_private_key, decrypt_private_key_with_mnemonic, encrypt_private_key,
    encrypt_private_key_with_mnemonic, generate_cloud_keypair, generate_recovery_mnemonic,
    mnemonic_to_key, open_dek, seal_dek,
};

#[test]
fn keypair_generation_produces_valid_keys() {
    let kp = generate_cloud_keypair();
    let pub_bytes = kp.public_bytes();
    let sec_bytes = kp.secret_bytes();
    assert_eq!(pub_bytes.len(), 32);
    assert_eq!(sec_bytes.len(), 32);
    // Public and secret keys must differ
    assert_ne!(pub_bytes, sec_bytes);
}

#[test]
fn keypair_roundtrip_from_secret_bytes() {
    let kp1 = generate_cloud_keypair();
    let sec = kp1.secret_bytes();
    let kp2 = privstack_crypto::envelope::CloudKeyPair::from_secret_bytes(sec);
    assert_eq!(kp1.public_bytes(), kp2.public_bytes());
    assert_eq!(kp1.secret_bytes(), kp2.secret_bytes());
}

#[test]
fn seal_open_dek_roundtrip() {
    let recipient = generate_cloud_keypair();
    let dek = b"this-is-a-32-byte-data-encr-key!";

    let envelope = seal_dek(dek, &recipient.public).unwrap();
    let recovered = open_dek(&envelope, &recipient.secret).unwrap();

    assert_eq!(recovered, dek);
}

#[test]
fn seal_open_empty_dek() {
    let recipient = generate_cloud_keypair();
    let dek = b"";

    let envelope = seal_dek(dek, &recipient.public).unwrap();
    let recovered = open_dek(&envelope, &recipient.secret).unwrap();

    assert_eq!(recovered, dek);
}

#[test]
fn seal_open_large_dek() {
    let recipient = generate_cloud_keypair();
    let dek = vec![0xABu8; 256];

    let envelope = seal_dek(&dek, &recipient.public).unwrap();
    let recovered = open_dek(&envelope, &recipient.secret).unwrap();

    assert_eq!(recovered, dek);
}

#[test]
fn wrong_recipient_key_fails_to_open() {
    let sender_target = generate_cloud_keypair();
    let wrong_recipient = generate_cloud_keypair();
    let dek = b"secret-dek-material-1234567890ab";

    let envelope = seal_dek(dek, &sender_target.public).unwrap();
    let result = open_dek(&envelope, &wrong_recipient.secret);

    assert!(result.is_err());
}

#[test]
fn tampered_ciphertext_fails() {
    let recipient = generate_cloud_keypair();
    let dek = b"secret-dek-material-1234567890ab";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    // Flip a byte in the ciphertext
    if let Some(byte) = envelope.ciphertext.first_mut() {
        *byte ^= 0xFF;
    }

    let result = open_dek(&envelope, &recipient.secret);
    assert!(result.is_err());
}

#[test]
fn tampered_nonce_fails() {
    let recipient = generate_cloud_keypair();
    let dek = b"secret-dek-material-1234567890ab";

    let mut envelope = seal_dek(dek, &recipient.public).unwrap();
    envelope.nonce[0] ^= 0xFF;

    let result = open_dek(&envelope, &recipient.secret);
    assert!(result.is_err());
}

#[test]
fn each_seal_produces_different_ciphertext() {
    let recipient = generate_cloud_keypair();
    let dek = b"same-dek-every-time-0123456789ab";

    let env1 = seal_dek(dek, &recipient.public).unwrap();
    let env2 = seal_dek(dek, &recipient.public).unwrap();

    // Different ephemeral keys and nonces
    assert_ne!(env1.ephemeral_public_key, env2.ephemeral_public_key);
    assert_ne!(env1.nonce, env2.nonce);
    assert_ne!(env1.ciphertext, env2.ciphertext);

    // Both decrypt to the same DEK
    assert_eq!(open_dek(&env1, &recipient.secret).unwrap(), dek);
    assert_eq!(open_dek(&env2, &recipient.secret).unwrap(), dek);
}

#[test]
fn passphrase_encrypt_decrypt_roundtrip() {
    let kp = generate_cloud_keypair();
    let passphrase = "correct-horse-battery-staple";

    let protected = encrypt_private_key(&kp.secret, passphrase).unwrap();
    let recovered = decrypt_private_key(&protected, passphrase).unwrap();

    assert_eq!(recovered.to_bytes(), kp.secret.to_bytes());
}

#[test]
fn wrong_passphrase_fails() {
    let kp = generate_cloud_keypair();
    let protected = encrypt_private_key(&kp.secret, "correct-passphrase").unwrap();

    let result = decrypt_private_key(&protected, "wrong-passphrase");
    assert!(result.is_err());
}

#[test]
fn mnemonic_generates_12_valid_words() {
    let mnemonic = generate_recovery_mnemonic().unwrap();
    let words: Vec<&str> = mnemonic.split_whitespace().collect();
    assert_eq!(words.len(), 12, "BIP39 mnemonic must be 12 words");
}

#[test]
fn mnemonic_produces_deterministic_key() {
    let mnemonic = generate_recovery_mnemonic().unwrap();
    let key1 = mnemonic_to_key(&mnemonic).unwrap();
    let key2 = mnemonic_to_key(&mnemonic).unwrap();
    assert_eq!(key1, key2, "Same mnemonic must produce same key");
}

#[test]
fn different_mnemonics_produce_different_keys() {
    let m1 = generate_recovery_mnemonic().unwrap();
    let m2 = generate_recovery_mnemonic().unwrap();
    assert_ne!(m1, m2, "Two random mnemonics should differ");

    let k1 = mnemonic_to_key(&m1).unwrap();
    let k2 = mnemonic_to_key(&m2).unwrap();
    assert_ne!(k1, k2, "Different mnemonics must produce different keys");
}

#[test]
fn invalid_mnemonic_rejected() {
    let result = mnemonic_to_key("not a valid mnemonic phrase at all");
    assert!(result.is_err());
}

#[test]
fn mnemonic_encrypt_decrypt_private_key() {
    let kp = generate_cloud_keypair();
    let mnemonic = generate_recovery_mnemonic().unwrap();

    let encrypted = encrypt_private_key_with_mnemonic(&kp.secret, &mnemonic).unwrap();
    let recovered = decrypt_private_key_with_mnemonic(&encrypted, &mnemonic).unwrap();

    assert_eq!(recovered.to_bytes(), kp.secret.to_bytes());
}

#[test]
fn wrong_mnemonic_fails_decrypt() {
    let kp = generate_cloud_keypair();
    let correct = generate_recovery_mnemonic().unwrap();
    let wrong = generate_recovery_mnemonic().unwrap();

    let encrypted = encrypt_private_key_with_mnemonic(&kp.secret, &correct).unwrap();
    let result = decrypt_private_key_with_mnemonic(&encrypted, &wrong);

    assert!(result.is_err());
}

#[test]
fn envelope_serialization_roundtrip() {
    let recipient = generate_cloud_keypair();
    let dek = b"serialize-test-dek-material-here";

    let envelope = seal_dek(dek, &recipient.public).unwrap();

    // Serialize to JSON and back
    let json = serde_json::to_string(&envelope).unwrap();
    let deserialized: privstack_crypto::SealedEnvelope = serde_json::from_str(&json).unwrap();

    assert_eq!(envelope.ephemeral_public_key, deserialized.ephemeral_public_key);
    assert_eq!(envelope.nonce, deserialized.nonce);
    assert_eq!(envelope.ciphertext, deserialized.ciphertext);

    // Deserialized envelope can still be opened
    let recovered = open_dek(&deserialized, &recipient.secret).unwrap();
    assert_eq!(recovered, dek);
}

#[test]
fn passphrase_protected_key_serialization() {
    let kp = generate_cloud_keypair();
    let passphrase = "serialize-test-passphrase";

    let protected = encrypt_private_key(&kp.secret, passphrase).unwrap();
    let json = serde_json::to_string(&protected).unwrap();
    let deserialized: privstack_crypto::PassphraseProtectedKey =
        serde_json::from_str(&json).unwrap();

    let recovered = decrypt_private_key(&deserialized, passphrase).unwrap();
    assert_eq!(recovered.to_bytes(), kp.secret.to_bytes());
}

// Property-based tests
mod proptests {
    use super::*;
    use proptest::prelude::*;

    proptest! {
        #[test]
        fn seal_open_always_roundtrips(dek in proptest::collection::vec(any::<u8>(), 0..256)) {
            let recipient = generate_cloud_keypair();
            let envelope = seal_dek(&dek, &recipient.public).unwrap();
            let recovered = open_dek(&envelope, &recipient.secret).unwrap();
            prop_assert_eq!(recovered, dek);
        }
    }
}
