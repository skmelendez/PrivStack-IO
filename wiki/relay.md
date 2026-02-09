# Relay Server

The relay is a lightweight, stateless Rust server that enables PrivStack clients to discover each other and connect through NATs.

## What It Does

1. **DHT Bootstrap** — acts as a Kademlia DHT seed node so clients can find each other across the internet
2. **NAT Traversal** — relays traffic between peers that cannot establish direct connections (e.g., both behind symmetric NATs)
3. **Identity API** — exposes an HTTP endpoint so clients can discover the relay's peer ID and addresses at runtime

The relay stores no user data. It sees encrypted traffic and routing metadata only.

## Network Stack

| Layer | Protocol | Port |
|---|---|---|
| Transport | QUIC v1 (UDP) | 4001 |
| HTTP API | TCP | 4002 |

### libp2p Behaviours

```rust
struct RelayBehaviour {
    relay: relay::Behaviour,                    // Circuit relay for NAT traversal
    kademlia: kad::Behaviour<MemoryStore>,      // DHT routing table
    identify: identify::Behaviour,              // Peer information exchange
}
```

- **Relay** — accepts relay reservations from clients and forwards traffic between them
- **Kademlia** — runs in **server mode** so it always responds to DHT queries; uses in-memory store (no persistence needed); 60-second query timeout
- **Identify** — exchanges protocol version and agent version with connecting peers

The relay listens on both IPv4 (`0.0.0.0`) and IPv6 (`::`).

## HTTP Identity API

**`GET /api/v1/identity`**

Returns:

```json
{
  "peer_id": "12D3KooW...",
  "addresses": [
    "/ip4/0.0.0.0/udp/4001/quic-v1",
    "/ip6/::/udp/4001/quic-v1"
  ],
  "protocol_version": "/privstack/relay/1.0.0",
  "agent_version": "privstack-relay/0.1.0"
}
```

Clients call this endpoint on startup to discover how to connect to the relay, eliminating the need to hardcode peer IDs in the application.

Implemented with Axum. Returns 404 for unknown routes.

## Peer Handling

When a client connects:

1. The relay adds the client to the Kademlia routing table with its connection address
2. When the client sends identify information, all reported listen addresses are added to Kademlia
3. Other clients can now discover this peer via DHT queries (scoped by sync code hash)
4. If a client requests a relay circuit, the relay forwards traffic between the two peers

## Configuration

| Flag | Default | Description |
|---|---|---|
| `--port` | 4001 | UDP/QUIC listen port |
| `--http-port` | 4002 | HTTP API listen port |
| `--identity` | `relay-identity.key` | Path to ED25519 identity key file |
| `--verbose` / `-v` | off | Enable debug-level logging |

### Identity Key

On first run, the relay generates an ED25519 keypair and saves it to the identity file. On subsequent runs, it loads the existing key. This ensures the relay's peer ID is stable across restarts.

## Deployment

The relay is designed to run as a systemd service on a public VPS.

### Firewall

```bash
ufw allow 4001/udp    # QUIC P2P
ufw allow 4002/tcp    # HTTP identity API
```

### Systemd Service

```ini
[Unit]
Description=PrivStack P2P Relay
After=network.target

[Service]
Type=simple
User=privstack
ExecStart=/usr/local/bin/privstack-relay --identity /var/lib/privstack/relay-identity.key
Restart=always
RestartSec=5
WorkingDirectory=/var/lib/privstack

[Install]
WantedBy=multi-user.target
```

### Operations

```bash
systemctl status privstack-relay        # Check status
journalctl -u privstack-relay -f        # Stream logs
systemctl restart privstack-relay       # Restart
```

## Build

```bash
cd relay
cargo build --release
```

The release profile enables LTO, single codegen unit, abort-on-panic, and symbol stripping for a small, optimized binary.

## Metrics

The relay tracks two counters in memory (logged, not exported):

- `peers_served` — total unique peers that have connected
- `relayed_connections` — total relay circuits established
