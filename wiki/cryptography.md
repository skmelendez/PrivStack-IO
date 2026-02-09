# Cryptography

PrivStack encrypts all data at rest using a two-tier key architecture. The design allows password changes without re-encrypting every entity and enables fine-grained sharing of individual items.

## Key Architecture

```
User Password
      |
      v (Argon2id)
Master Key (256-bit, never stored)
      |
      v (ChaCha20-Poly1305)
Per-Entity Key (random 256-bit, stored encrypted)
      |
      v (ChaCha20-Poly1305)
Entity Data (encrypted at rest)
```

### Master Key

Derived from the user's password using Argon2id. The master key is never written to disk — it exists only in memory while the vault is unlocked and is zeroized on lock or process exit.

### Per-Entity Keys

Each entity gets its own random 256-bit key at creation time. This key is encrypted (wrapped) with the master key and stored alongside the entity.

Benefits:
- **Password change** — only the per-entity key wrappers need re-encrypting, not every entity's data
- **Selective sharing** — share an entity by sharing its wrapped key, without exposing the master key
- **Key rotation** — individual entity keys can be rotated independently

## Key Derivation (Argon2id)

Parameters follow the OWASP 2023 recommendations:

| Parameter | Value |
|---|---|
| Algorithm | Argon2id v1.3 |
| Memory | 19 MiB (19456 KiB) |
| Time cost | 2 iterations |
| Parallelism | 1 |
| Output length | 256 bits (32 bytes) |
| Salt | 128-bit random, generated at vault creation |

Target: derivation completes in under 1 second on modern hardware. Argon2id is a hybrid that resists both GPU cracking (memory-hard) and side-channel attacks.

## Symmetric Encryption (ChaCha20-Poly1305)

All encryption uses ChaCha20-Poly1305, an AEAD (Authenticated Encryption with Associated Data) cipher.

| Property | Value |
|---|---|
| Algorithm | ChaCha20-Poly1305 (RFC 8439) |
| Key size | 256 bits |
| Nonce size | 96 bits (12 bytes), random per encryption |
| Auth tag | 128 bits (16 bytes) |

### Encrypted Data Format

```
[12 bytes nonce][ciphertext][16 bytes authentication tag]
```

The entire blob is base64-encoded for storage in text columns.

### Authentication

ChaCha20-Poly1305 is authenticated — decryption fails with an error if any bit of the ciphertext or nonce has been tampered with. There is no silent corruption.

## Encryptor Abstraction

Storage layers interact with encryption through the `DataEncryptor` trait:

```rust
pub trait DataEncryptor: Send + Sync {
    fn encrypt_bytes(&self, entity_id: &str, data: &[u8]) -> Result<Vec<u8>>;
    fn decrypt_bytes(&self, data: &[u8]) -> Result<Vec<u8>>;
    fn reencrypt_bytes(&self, data: &[u8], old_key: &[u8], new_key: &[u8]) -> Result<Vec<u8>>;
    fn is_available(&self) -> bool;
}
```

A `PassthroughEncryptor` is used in tests and before the vault is unlocked (returns data unchanged).

## Memory Safety

- `DerivedKey` implements `Zeroize` and `ZeroizeOnDrop` — key bytes are overwritten with zeros when the key goes out of scope
- Debug formatting redacts key bytes to prevent accidental logging
- Keys are held in `Arc` behind the vault manager so they can be zeroized from a single location on lock

## Transport Encryption

P2P connections use the Noise protocol (via libp2p) for transport-level encryption. This is independent of the at-rest encryption — data is double-encrypted in transit (Noise for the transport, ChaCha20-Poly1305 for the payload).

## Vault System

The vault is a higher-level abstraction built on top of the crypto primitives.

### Initialization

1. Generate random 128-bit salt
2. Derive master key from password + salt via Argon2id
3. Encrypt a known verification token with the master key
4. Store salt and encrypted verification token in DuckDB metadata table

### Unlock

1. Read salt from metadata
2. Derive key from password + stored salt
3. Attempt to decrypt the verification token
4. If decryption succeeds and plaintext matches expected value, the password is correct
5. Hold derived key in memory

### Multi-Vault Support

Multiple vaults can coexist in a single DuckDB database, each with its own password and salt. Tables are prefixed by vault ID (e.g., `vault_personal_meta`, `vault_work_meta`). The `VaultManager` manages multiple `Vault` instances concurrently.

### State Machine

```
[Uninitialized] --initialize(password)--> [Locked]
[Locked]        --unlock(password)------> [Unlocked]
[Unlocked]      --lock()----------------> [Locked]
```
