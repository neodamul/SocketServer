# Message Envelope Optimization

Implementation status and remaining proposal for shrinking control-plane and server-to-server **relay** message size by reducing high-overhead envelope fields, in preference to generic compression. Builds on [Relay Traffic Sizing](RelayTrafficSizing.md) and [Protocols](Protocols.md).

## Why not just compress
Relay/control messages are small; the **envelope** (ids, token, timestamps) dominates for small content (a `SERVER_RELAY_MESSAGE` frame ≈ `112 + content`). Generic per-message compression is a **net negative** for the common case:
- The envelope is mostly **incompressible**: `message_token` is a 32-hex GUID (high entropy), and `client_id`/`ttl`/`created_at` are varints. Only `content` compresses.
- Compression adds **CPU on every message, both sides**, while the system is already **TLS-handshake CPU-bound**; the internal relay link is fast, so trading CPU for a few saved bytes loses.
- Small messages (<~200 B content) often grow (compressor framing overhead 4–18 B).

**Compression is worth it only** for large text/JSON content (≥ ~512 B) or a slow/metered/WAN relay link. If used: gate by size + "keep only if smaller", use **LZ4 or zstd** (not gzip/deflate), compress **before** TLS encryption, and mind CRIME/BREACH when content mixes secrets with attacker-influenced data.

## Structural envelope shrink (preferred — zero CPU, no CRIME risk)
Remove the exact bytes compression cannot touch:

1. **`message_token`: 32-hex string → 16 raw bytes** (proto `bytes`). A GUID is 128 bits = 16 bytes; the 32-char hex only doubles it for human readability. The token is **opaque** (echoed in ACK/DELIVER/ERROR, byte-compared, logged) — never parsed. Save **~16 B/message** (and on ACK/ERROR/DELIVER too). Format to hex only when logging.
2. **`source_instance_id`: ~22-char string → numeric server index** (reuse the existing `server_id` int32, or a cluster-wide server index). The relay receiver uses it for **logging/correlation only**, not for data-path routing (relay already targets the destination endpoint directly). Resolve index → name via the **registry snapshot the receiver already caches**. Save **~20 B/message**.
3. **`cluster_id`: omit on server↔server relay** — both endpoints are in the same cluster (or send a small id). Save **~16–18 B/message**.
4. **`name` (server display name): implemented.** `ProtoServerRegisterRequest.name` (field 4) and `ProtoBackendServerSnapshot.name` (field 5) were redundant with `server_id` + `instance_id` and have been removed from the wire model. The field numbers are reserved in protobuf, `BackendServerRegistry` stores `instance_id` as the server identity, and the dashboard uses `server.instanceId` without a `server.name` fallback. Local SocketServer config may still carry `servers[].name` as a process/display input; it is not sent in the control protocol.

## Effect — small relay message (content 50 B)
| field | now | after |
| --- | --- | --- |
| `message_token` | 34 B | 18 B |
| `source_instance_id` | 24 B | ~3 B |
| `cluster_id` | 18 B | 0–2 B |
| **frame total** (~156 B) | — | **~103 B (~33% smaller), 0 CPU** |

The `name` removal already makes `SERVER_REGISTER` and every `BackendServerSnapshot` smaller (a few bytes per snapshot × per-heartbeat × servers). For small messages, the remaining structural changes still beat compression, which saved ~0% here.

## Cost / migration
- **Proto wire change** (field type change / field removal) is **not backward compatible** → deploy all nodes together, or add new field numbers and **dual-read** during a migration window.
- Minor wire-readability loss: logs format `bytes → hex` and `index → name` via the registry.
- Needs a **cluster-wide stable server index** if `server_id` is not unique per instance.

## See also
- [Protocols → Server relay](Protocols.md#server-relay) · [Relay Traffic Sizing](RelayTrafficSizing.md) (sizes, broadcast amplification, compression bandwidth math).
