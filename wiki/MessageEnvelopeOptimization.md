# Message Envelope Optimization

Implementation status for shrinking control-plane and server-to-server **relay** message size by reducing high-overhead envelope fields, in preference to generic compression. Builds on [Relay Traffic Sizing](RelayTrafficSizing.md) and [Protocols](Protocols.md).

## Why not just compress
Relay/control messages are small; the **envelope** (ids, token, timestamps) dominates for small content. Generic per-message compression is a **net negative** for the common case:
- The envelope is mostly **incompressible**: `message_token_bytes` is a 16-byte GUID token, and `client_id`/`ttl`/`created_at` are varints. Only `content` compresses.
- Compression adds **CPU on every message, both sides**, while the system is already **TLS-handshake CPU-bound**; the internal relay link is fast, so trading CPU for a few saved bytes loses.
- Small messages (<~200 B content) often grow (compressor framing overhead 4–18 B).

**Compression is worth it only** for large text/JSON content (≥ ~512 B) or a slow/metered/WAN relay link. If used: gate by size + "keep only if smaller", use **LZ4 or zstd** (not gzip/deflate), compress **before** TLS encryption, and mind CRIME/BREACH when content mixes secrets with attacker-influenced data.

## Structural envelope shrink (implemented — zero CPU, no CRIME risk)
The current protobuf serializer removes the exact bytes compression cannot touch:

1. **`message_token`: 32-hex string → 16 raw bytes** (proto `bytes`). A GUID is 128 bits = 16 bytes; the 32-char hex only doubles it for human readability. The token remains a string in the public model, but new protobuf writes use `message_token_bytes` for 32-hex tokens and decode bytes back to lowercase hex. Legacy non-hex tokens still use the old string field as a fallback.
2. **`source_instance_id`: string → numeric `source_server_id`**. Server-to-server relay writes `source_server_id` from the SocketServer's configured `server_id`. Upgraded readers still accept the old `source_instance_id`, but new relay writes omit it when `source_server_id` is present.
3. **`cluster_id`: omitted on server↔server relay** — relay endpoints are already in the same configured cluster. Upgraded readers still accept the old field, but new relay writes leave it empty so proto3 omits it from the wire.
4. **`name` (server display name): implemented.** `ProtoServerRegisterRequest.name` (field 4) and `ProtoBackendServerSnapshot.name` (field 5) were redundant with `server_id` + `instance_id` and have been removed from the wire model. The field numbers are reserved in protobuf, `BackendServerRegistry` stores `instance_id` as the server identity, and the dashboard uses `server.instanceId` without a `server.name` fallback. Local SocketServer config may still carry `servers[].name` as a process/display input; it is not sent in the control protocol.

## Effect — small relay message (content 50 B)
| field | now | after |
| --- | --- | --- |
| `message_token` | 34 B string | 18 B bytes |
| `source_instance_id` | ~24 B string | ~2 B `source_server_id` |
| `cluster_id` | ~18 B string | 0 B |
| **frame total** | — | **about 50 B smaller for small relay messages, 0 CPU** |

The `name` removal already makes `SERVER_REGISTER` and every `BackendServerSnapshot` smaller (a few bytes per snapshot × per-heartbeat × servers). For small messages, the remaining structural changes still beat compression, which saved ~0% here.

## Migration behavior
- The implementation uses **new field numbers + dual-read** instead of changing old field types in place.
- Upgraded readers prefer `message_token_bytes` and fall back to legacy `message_token`.
- New relay writes use `source_server_id` and omit `source_instance_id`/`cluster_id`; upgraded readers still accept legacy fields.
- This is **not old-reader compatible**: old binaries ignore `message_token_bytes` and will see an empty token when reading compact new writes. Deploy all SocketCommon consumers together, or upgrade every reader before enabling compact writers.
- Minor wire-readability loss: logs show the hex token reconstructed from bytes and include `source_server_id`.
- `server_id` must remain stable and unique enough for cluster-local relay correlation.

## See also
- [Protocols → Server relay](Protocols.md#server-relay) · [Relay Traffic Sizing](RelayTrafficSizing.md) (sizes, broadcast amplification, compression bandwidth math).
