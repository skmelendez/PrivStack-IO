//! Envelope encryption for cloud sync and sharing.
//!
//! Uses X25519 key exchange + XSalsa20-Poly1305 for encrypting entity DEKs
//! (Data Encryption Keys). Each DEK is sealed with a recipient's public key
//! using an ephemeral keypair, allowing anonymous encryption.
//!
//! Also provides passphrase-protected private key storage and BIP39 mnemonic
//! recovery for multi-device key synchronization.

use crate::error::{CryptoError, CryptoResult};
use crate::key::{DerivedKey, KdfParams, Salt};
use crate::{decrypt, encrypt, EncryptedData};
use crypto_box::aead::Aead;
use crypto_box::{PublicKey, SalsaBox, SecretKey};
use rand::RngCore;
use serde::{Deserialize, Serialize};

/// X25519 keypair for cloud sync envelope encryption.
///
/// The secret key implements `ZeroizeOnDrop` automatically (from crypto_box).
pub struct CloudKeyPair {
    pub secret: SecretKey,
    pub public: PublicKey,
}

impl CloudKeyPair {
    /// Returns the public key as raw 32-byte array.
    pub fn public_bytes(&self) -> [u8; 32] {
        *self.public.as_bytes()
    }

    /// Returns the secret key as raw 32-byte array.
    pub fn secret_bytes(&self) -> [u8; 32] {
        self.secret.to_bytes()
    }

    /// Reconstructs a keypair from raw secret key bytes.
    pub fn from_secret_bytes(bytes: [u8; 32]) -> Self {
        let secret = SecretKey::from(bytes);
        let public = secret.public_key();
        Self { secret, public }
    }
}

/// Envelope-encrypted DEK sealed with a recipient's X25519 public key.
///
/// Uses ephemeral X25519 key exchange + XSalsa20-Poly1305. The ephemeral
/// public key is included so the recipient can reconstruct the shared secret.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SealedEnvelope {
    /// Ephemeral X25519 public key (sender side of DH).
    pub ephemeral_public_key: [u8; 32],
    /// XSalsa20 nonce (24 bytes).
    pub nonce: [u8; 24],
    /// Encrypted DEK (XSalsa20-Poly1305 ciphertext + Poly1305 tag).
    pub ciphertext: Vec<u8>,
}

/// Private key encrypted with a passphrase (Argon2id -> ChaCha20-Poly1305).
///
/// Bundles the Argon2id salt with the encrypted data so the passphrase
/// is the only input needed for decryption.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PassphraseProtectedKey {
    pub salt: [u8; 16],
    pub encrypted: EncryptedData,
}

/// Generates a new X25519 keypair for cloud sync.
pub fn generate_cloud_keypair() -> CloudKeyPair {
    let secret = SecretKey::generate(&mut rand::rngs::OsRng);
    let public = secret.public_key();
    CloudKeyPair { secret, public }
}

/// Seals (encrypts) a DEK for a recipient using anonymous envelope encryption.
///
/// An ephemeral X25519 keypair is generated for each seal operation, ensuring
/// forward secrecy. The sender's identity is not revealed.
pub fn seal_dek(dek: &[u8], recipient_pk: &PublicKey) -> CryptoResult<SealedEnvelope> {
    let ephemeral = SecretKey::generate(&mut rand::rngs::OsRng);
    let ephemeral_pk = ephemeral.public_key();

    let salsa_box = SalsaBox::new(recipient_pk, &ephemeral);

    let mut nonce_bytes = [0u8; 24];
    rand::rngs::OsRng.fill_bytes(&mut nonce_bytes);

    let ciphertext = salsa_box
        .encrypt(crypto_box::Nonce::from_slice(&nonce_bytes), dek)
        .map_err(|e| CryptoError::Encryption(format!("envelope seal failed: {e}")))?;

    Ok(SealedEnvelope {
        ephemeral_public_key: *ephemeral_pk.as_bytes(),
        nonce: nonce_bytes,
        ciphertext,
    })
}

/// Opens (decrypts) a sealed DEK envelope using the recipient's secret key.
pub fn open_dek(envelope: &SealedEnvelope, recipient_sk: &SecretKey) -> CryptoResult<Vec<u8>> {
    let ephemeral_pk = PublicKey::from(envelope.ephemeral_public_key);
    let salsa_box = SalsaBox::new(&ephemeral_pk, recipient_sk);

    salsa_box
        .decrypt(
            crypto_box::Nonce::from_slice(&envelope.nonce),
            envelope.ciphertext.as_ref(),
        )
        .map_err(|_| {
            CryptoError::Decryption(
                "envelope open failed (wrong key or tampered data)".to_string(),
            )
        })
}

/// Encrypts a private key with a passphrase using Argon2id -> ChaCha20-Poly1305.
///
/// The Argon2id salt is stored alongside the ciphertext so only the passphrase
/// is needed for decryption.
pub fn encrypt_private_key(
    sk: &SecretKey,
    passphrase: &str,
) -> CryptoResult<PassphraseProtectedKey> {
    let salt = Salt::random();
    let derived = crate::derive_key(passphrase, &salt, &KdfParams::default())?;
    let encrypted = encrypt(&derived, &sk.to_bytes())?;

    Ok(PassphraseProtectedKey {
        salt: *salt.as_bytes(),
        encrypted,
    })
}

/// Decrypts a passphrase-protected private key.
pub fn decrypt_private_key(
    protected: &PassphraseProtectedKey,
    passphrase: &str,
) -> CryptoResult<SecretKey> {
    let salt = Salt::from_bytes(protected.salt);
    let derived = crate::derive_key(passphrase, &salt, &KdfParams::default())?;
    let plaintext = decrypt(&derived, &protected.encrypted)?;

    if plaintext.len() != 32 {
        return Err(CryptoError::InvalidKeyLength {
            expected: 32,
            actual: plaintext.len(),
        });
    }

    let mut bytes = [0u8; 32];
    bytes.copy_from_slice(&plaintext);
    Ok(SecretKey::from(bytes))
}

/// Generates a 12-word BIP39 mnemonic for offline key recovery.
pub fn generate_recovery_mnemonic() -> CryptoResult<String> {
    // Generate 128 bits of entropy for a 12-word mnemonic
    let mut entropy = [0u8; 16];
    rand::rngs::OsRng.fill_bytes(&mut entropy);

    let mnemonic = bip39::Mnemonic::from_entropy(&entropy)
        .map_err(|e| CryptoError::KeyDerivation(format!("mnemonic generation failed: {e}")))?;

    Ok(mnemonic.to_string())
}

/// Derives a 32-byte key from a BIP39 mnemonic phrase.
///
/// Validates the mnemonic, then uses Argon2id with a fixed domain salt to
/// derive a 256-bit key. The mnemonic's 128-bit entropy makes brute-force
/// infeasible even with a fixed salt.
pub fn mnemonic_to_key(mnemonic: &str) -> CryptoResult<[u8; 32]> {
    // Validate it's a proper BIP39 mnemonic
    let _: bip39::Mnemonic = mnemonic
        .parse()
        .map_err(|e| CryptoError::KeyDerivation(format!("invalid mnemonic: {e}")))?;

    // Domain-separated fixed salt (safe because mnemonic has 128 bits entropy)
    let salt = Salt::from_bytes(*b"privstack-mnemo\0");
    let derived = crate::derive_key(mnemonic, &salt, &KdfParams::default())?;
    Ok(*derived.as_bytes())
}

/// Encrypts a private key with a BIP39 mnemonic for offline recovery.
pub fn encrypt_private_key_with_mnemonic(
    sk: &SecretKey,
    mnemonic: &str,
) -> CryptoResult<EncryptedData> {
    let key_bytes = mnemonic_to_key(mnemonic)?;
    let derived = DerivedKey::from_bytes(key_bytes);
    encrypt(&derived, &sk.to_bytes())
}

/// Decrypts a private key using a BIP39 mnemonic.
pub fn decrypt_private_key_with_mnemonic(
    encrypted: &EncryptedData,
    mnemonic: &str,
) -> CryptoResult<SecretKey> {
    let key_bytes = mnemonic_to_key(mnemonic)?;
    let derived = DerivedKey::from_bytes(key_bytes);
    let plaintext = decrypt(&derived, encrypted)?;

    if plaintext.len() != 32 {
        return Err(CryptoError::InvalidKeyLength {
            expected: 32,
            actual: plaintext.len(),
        });
    }

    let mut bytes = [0u8; 32];
    bytes.copy_from_slice(&plaintext);
    Ok(SecretKey::from(bytes))
}
