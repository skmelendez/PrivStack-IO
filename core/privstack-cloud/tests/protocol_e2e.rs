//! Protocol-level E2E tests for cloud sync sharing.
//!
//! Exercises the complete crypto+sharing protocol against real MinIO S3:
//! seal/open envelopes, multi-recipient, revocation via DEK rotation,
//! bidirectional sync, blob access, and key recovery (passphrase + mnemonic).
//!
//! Requires: `docker compose -f docker-compose.test.yml up -d`

mod support;

use pretty_assertions::assert_eq;
use privstack_crypto::envelope::{
    decrypt_private_key, decrypt_private_key_with_mnemonic, encrypt_private_key,
    encrypt_private_key_with_mnemonic, generate_cloud_keypair, generate_recovery_mnemonic,
    open_dek, seal_dek,
};
use privstack_crypto::{decrypt, encrypt, generate_random_key, DerivedKey, EncryptedData};
use serial_test::serial;
use sha2::{Digest, Sha256};

// ─────────────────────── helpers ───────────────────────

/// Encrypt plaintext with DEK, serialize, upload to S3, return the key.
async fn upload_encrypted(
    transport: &privstack_cloud::s3_transport::S3Transport,
    creds: &privstack_cloud::StsCredentials,
    s3_key: &str,
    dek: &DerivedKey,
    plaintext: &[u8],
) {
    let encrypted = encrypt(dek, plaintext).expect("encrypt");
    let bytes = serde_json::to_vec(&encrypted).expect("serialize");
    transport.upload(creds, s3_key, bytes).await.expect("upload");
}

/// Download from S3, deserialize, decrypt with DEK.
async fn download_decrypt(
    transport: &privstack_cloud::s3_transport::S3Transport,
    creds: &privstack_cloud::StsCredentials,
    s3_key: &str,
    dek: &DerivedKey,
) -> Vec<u8> {
    let downloaded = transport.download(creds, s3_key).await.expect("download");
    let envelope: EncryptedData = serde_json::from_slice(&downloaded).expect("deserialize");
    decrypt(dek, &envelope).expect("decrypt")
}

// ─────────────────────── tests ────────────────────────

#[tokio::test]
#[serial]
async fn share_envelope_roundtrip_via_s3() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let owner = generate_cloud_keypair();
    let recipient = generate_cloud_keypair();
    let dek = generate_random_key();

    let plaintext = b"shared entity data for envelope roundtrip";
    let s3_key = format!("{prefix}/entity/batch_0_5.enc");
    upload_encrypted(&transport, &creds, &s3_key, &dek, plaintext).await;

    // Owner seals DEK for recipient
    let envelope = seal_dek(dek.as_bytes(), &recipient.public).expect("seal");
    let envelope_json = serde_json::to_string(&envelope).expect("serialize envelope");

    // Recipient opens envelope (simulating DB retrieval)
    let recovered_envelope = serde_json::from_str(&envelope_json).expect("deserialize envelope");
    let opened = open_dek(&recovered_envelope, &recipient.secret).expect("open");
    let recovered_dek = DerivedKey::from_bytes(opened.try_into().expect("32 bytes"));

    let recovered = download_decrypt(&transport, &creds, &s3_key, &recovered_dek).await;
    assert_eq!(recovered, plaintext);

    // Wrong key fails
    assert!(open_dek(&recovered_envelope, &owner.secret).is_err());
}

#[tokio::test]
#[serial]
async fn multi_recipient_all_decrypt() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let alice = generate_cloud_keypair();
    let bob = generate_cloud_keypair();
    let charlie = generate_cloud_keypair();
    let dek = generate_random_key();

    let plaintext = b"data shared with three recipients";
    let s3_key = format!("{prefix}/entity/batch_0_10.enc");
    upload_encrypted(&transport, &creds, &s3_key, &dek, plaintext).await;

    // Owner seals DEK for each recipient independently
    let env_alice = seal_dek(dek.as_bytes(), &alice.public).expect("seal alice");
    let env_bob = seal_dek(dek.as_bytes(), &bob.public).expect("seal bob");
    let env_charlie = seal_dek(dek.as_bytes(), &charlie.public).expect("seal charlie");

    // Each recipient opens their envelope and decrypts the data
    for (name, envelope, secret) in [
        ("alice", &env_alice, &alice.secret),
        ("bob", &env_bob, &bob.secret),
        ("charlie", &env_charlie, &charlie.secret),
    ] {
        let opened = open_dek(envelope, secret).unwrap_or_else(|_| panic!("{name} open failed"));
        let recovered_dek = DerivedKey::from_bytes(opened.try_into().expect("32 bytes"));
        let recovered = download_decrypt(&transport, &creds, &s3_key, &recovered_dek).await;
        assert_eq!(recovered, plaintext, "{name} plaintext mismatch");
    }

    // Cross-recipient: Alice's envelope cannot be opened by Bob
    assert!(open_dek(&env_alice, &bob.secret).is_err());
    assert!(open_dek(&env_bob, &charlie.secret).is_err());
}

#[tokio::test]
#[serial]
async fn revocation_dek_rotation() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let alice = generate_cloud_keypair();
    let bob = generate_cloud_keypair();
    let charlie = generate_cloud_keypair();

    // DEK v1: shared with all three
    let dek_v1 = generate_random_key();
    let data_v1 = b"version 1 data before revocation";
    let s3_key_v1 = format!("{prefix}/entity/batch_0_5.enc");
    upload_encrypted(&transport, &creds, &s3_key_v1, &dek_v1, data_v1).await;

    let env_alice_v1 = seal_dek(dek_v1.as_bytes(), &alice.public).expect("seal");
    let env_bob_v1 = seal_dek(dek_v1.as_bytes(), &bob.public).expect("seal");
    let env_charlie_v1 = seal_dek(dek_v1.as_bytes(), &charlie.public).expect("seal");

    // All three can decrypt v1
    for (envelope, secret) in [
        (&env_alice_v1, &alice.secret),
        (&env_bob_v1, &bob.secret),
        (&env_charlie_v1, &charlie.secret),
    ] {
        let opened = open_dek(envelope, secret).expect("open v1");
        let dk = DerivedKey::from_bytes(opened.try_into().unwrap());
        let recovered = download_decrypt(&transport, &creds, &s3_key_v1, &dk).await;
        assert_eq!(recovered, data_v1);
    }

    // REVOCATION: Owner rotates to DEK v2, seals for Alice and Bob only
    let dek_v2 = generate_random_key();
    let data_v2 = b"version 2 data after charlie revoked";
    let s3_key_v2 = format!("{prefix}/entity/batch_5_10.enc");
    upload_encrypted(&transport, &creds, &s3_key_v2, &dek_v2, data_v2).await;

    let env_alice_v2 = seal_dek(dek_v2.as_bytes(), &alice.public).expect("seal v2");
    let env_bob_v2 = seal_dek(dek_v2.as_bytes(), &bob.public).expect("seal v2");
    // Charlie does NOT get a v2 envelope

    // Alice decrypts v2
    let opened = open_dek(&env_alice_v2, &alice.secret).expect("alice open v2");
    let dk = DerivedKey::from_bytes(opened.try_into().unwrap());
    assert_eq!(download_decrypt(&transport, &creds, &s3_key_v2, &dk).await, data_v2);

    // Bob decrypts v2
    let opened = open_dek(&env_bob_v2, &bob.secret).expect("bob open v2");
    let dk = DerivedKey::from_bytes(opened.try_into().unwrap());
    assert_eq!(download_decrypt(&transport, &creds, &s3_key_v2, &dk).await, data_v2);

    // Charlie only has DEK v1 — decrypting v2 data fails
    let opened_v1 = open_dek(&env_charlie_v1, &charlie.secret).expect("charlie open v1");
    let dk_v1 = DerivedKey::from_bytes(opened_v1.try_into().unwrap());
    let downloaded = transport.download(&creds, &s3_key_v2).await.expect("download");
    let envelope: EncryptedData = serde_json::from_slice(&downloaded).expect("deserialize");
    assert!(decrypt(&dk_v1, &envelope).is_err(), "charlie must not decrypt v2 data");
}

#[tokio::test]
#[serial]
async fn bidirectional_sync_shared_dek() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let owner = generate_cloud_keypair();
    let recipient = generate_cloud_keypair();
    let dek = generate_random_key();

    // Seal DEK for recipient
    let envelope = seal_dek(dek.as_bytes(), &recipient.public).expect("seal");
    let opened = open_dek(&envelope, &recipient.secret).expect("open");
    let recipient_dek = DerivedKey::from_bytes(opened.try_into().unwrap());

    // Owner writes batch_0_5
    let owner_events = b"owner events [0..5]";
    let owner_key = format!("{prefix}/entity/batch_0_5.enc");
    upload_encrypted(&transport, &creds, &owner_key, &dek, owner_events).await;

    // Recipient writes batch_5_10 using same DEK
    let recipient_events = b"recipient events [5..10]";
    let recipient_key = format!("{prefix}/entity/batch_5_10.enc");
    upload_encrypted(&transport, &creds, &recipient_key, &recipient_dek, recipient_events).await;

    // Owner downloads recipient's batch → decrypts with DEK
    let recovered = download_decrypt(&transport, &creds, &recipient_key, &dek).await;
    assert_eq!(recovered, recipient_events);

    // Recipient downloads owner's batch → decrypts with DEK
    let recovered = download_decrypt(&transport, &creds, &owner_key, &recipient_dek).await;
    assert_eq!(recovered, owner_events);
}

#[tokio::test]
#[serial]
async fn blob_shared_access() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let recipient = generate_cloud_keypair();
    let dek = generate_random_key();

    let blob_data = b"binary file attachment content for blob test";
    let original_hash = Sha256::digest(blob_data);

    let s3_key = format!("{prefix}/blobs/attachment-001.enc");
    upload_encrypted(&transport, &creds, &s3_key, &dek, blob_data).await;

    // Seal DEK for recipient
    let envelope = seal_dek(dek.as_bytes(), &recipient.public).expect("seal");
    let opened = open_dek(&envelope, &recipient.secret).expect("open");
    let recovered_dek = DerivedKey::from_bytes(opened.try_into().unwrap());

    let recovered = download_decrypt(&transport, &creds, &s3_key, &recovered_dek).await;
    assert_eq!(Sha256::digest(&recovered), original_hash);
    assert_eq!(recovered, blob_data);
}

#[tokio::test]
#[serial]
async fn passphrase_key_recovery_via_s3() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let passphrase = "correct-horse-battery-staple";
    let keypair = generate_cloud_keypair();

    // Encrypt private key with passphrase and upload
    let protected = encrypt_private_key(&keypair.secret, passphrase).expect("encrypt pk");
    let protected_bytes = serde_json::to_vec(&protected).expect("serialize");
    let s3_key = format!("{prefix}/keys/private_key.enc");
    transport.upload(&creds, &s3_key, protected_bytes).await.expect("upload");

    // "New device": download and recover
    let downloaded = transport.download(&creds, &s3_key).await.expect("download");
    let recovered_protected = serde_json::from_slice(&downloaded).expect("deserialize");
    let recovered_sk = decrypt_private_key(&recovered_protected, passphrase).expect("decrypt pk");

    // Use recovered key to open a sealed envelope
    let dek = generate_random_key();
    let envelope = seal_dek(dek.as_bytes(), &keypair.public).expect("seal");
    let opened = open_dek(&envelope, &recovered_sk).expect("open with recovered key");
    assert_eq!(opened, dek.as_bytes().to_vec());
}

#[tokio::test]
#[serial]
async fn mnemonic_key_recovery_via_s3() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let mnemonic = generate_recovery_mnemonic().expect("generate mnemonic");
    let keypair = generate_cloud_keypair();

    // Encrypt private key with mnemonic and upload
    let encrypted = encrypt_private_key_with_mnemonic(&keypair.secret, &mnemonic).expect("encrypt");
    let encrypted_bytes = serde_json::to_vec(&encrypted).expect("serialize");
    let s3_key = format!("{prefix}/keys/private_key_recovery.enc");
    transport.upload(&creds, &s3_key, encrypted_bytes).await.expect("upload");

    // "Recovery": download and recover
    let downloaded = transport.download(&creds, &s3_key).await.expect("download");
    let recovered_encrypted = serde_json::from_slice(&downloaded).expect("deserialize");
    let recovered_sk =
        decrypt_private_key_with_mnemonic(&recovered_encrypted, &mnemonic).expect("decrypt");

    // Use recovered key to open a sealed envelope
    let dek = generate_random_key();
    let envelope = seal_dek(dek.as_bytes(), &keypair.public).expect("seal");
    let opened = open_dek(&envelope, &recovered_sk).expect("open with recovered key");
    assert_eq!(opened, dek.as_bytes().to_vec());
}
