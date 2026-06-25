# Relay Traffic Sizing

Size estimate for the control-plane and server-to-server **relay** messages exchanged between ControlServer and SocketServer, at a worked example of **40,000 connected clients**. Grounded in `SocketCommon/Proto/SocketMessages.proto` and the 12-byte frame header (see [Protocols → Common frame](Protocols.md#common-frame)).

## Assumptions
- 40,000 clients = **4 SocketServers × 10,000** (current default sizing), **2 ControlServers** (active-active).
- Frame = **12 B header + protobuf payload** (plaintext basis, identical to the app's message-byte counters; see [Protocols → Transport security](Protocols.md#transport-security)). TLS adds ~25–40 B per record on the wire, counted separately.
- protobuf (proto3) omits zero/empty fields; varints: `clientId` 40000 → 3 B, `unix_ms` → 6 B, plus a 1 B tag (2 B for field numbers ≥ 16).

## Per-message size (frame = 12 B + payload)
| Message | Hop | payload | frame |
| --- | --- | --- | --- |
| `SERVER_RELAY_MESSAGE` (content 256 B) | socket→socket | ~306 B | **~318 B** |
| `SERVER_RELAY_MESSAGE` (content 50 B) | socket→socket | ~100 B | ~112 B |
| `SERVER_RELAY_ACK` (delivered) | socket→socket | ~63 B | ~75 B |
| `SERVER_RELAY_ERROR` (`TargetNotConnected`) | socket→socket | ~59 B | ~71 B |
| `CLIENT_LOCATION_REQUEST` (fallback lookup) | socket→control | ~8 B | ~20 B |
| `CLIENT_LOCATION_RESPONSE` | control→socket | ~68 B | ~80 B |
| `SERVER_HEARTBEAT` | socket→control | ~190 B | ~202 B |
| `SESSION_OPENED` / `SESSION_CLOSED` | socket→control | ~106 B | ~118 B |
| `CLIENT_LOCATION_UPSERT` | control→control | ~91 B | ~103 B |

The relay messages themselves are small (tens to a few hundred bytes); `content` is the main variable (capped by `MaxPayloadLength` = 4 KB on the whole payload).

## Cross-server message cost (broadcast fallback model)
When the target client is on a **different** server and targeted lookup/cache cannot route it, the source server can broadcast `SERVER_RELAY_MESSAGE` to all other servers in parallel (N−1 = 3 here); only the holder returns `SERVER_RELAY_ACK`, the rest return `SERVER_RELAY_ERROR` (`TargetNotConnected`). See [Protocols → Server relay](Protocols.md#server-relay) and [Architecture → SocketServer](Architecture.md#socketserver).

```text
content 256 B: 3×318 (broadcast) + 75 (ack) + 2×71 (error) ≈ 1,171 B  (~1.14 KB)
content  50 B: 3×112           + 75       + 2×71        ≈   553 B
```
With a random target across 4 servers, the cross-server probability ≈ **3/4** (the other 1/4 is same-server local delivery → 0 relay).

## Aggregate relay bandwidth (scales with message rate R)
Relay-plane bandwidth ≈ `(R × 3/4) × 1.17 KB` (content 256 B), where R = total client-to-client messages/s across all clients:

| Per-client send rate | total R | cross-server/s | relay bandwidth |
| --- | --- | --- | --- |
| 1 / min | 667/s | 500/s | **~0.59 MB/s (~4.7 Mbps)** |
| 1 / 10 s | 4,000/s | 3,000/s | ~3.5 MB/s (~28 Mbps) |
| 1 / s | 40,000/s | 30,000/s | ~35 MB/s (~281 Mbps) |

## Parametric model (content size × rate)
Closed form (frame bytes), letting `C` = client message `content` size in bytes:

```text
SERVER_RELAY frame  ≈ 62 + C        (compact envelope: message_token_bytes(16),
                                      source_server_id, src/tgt client_id, ttl, created_at)
SERVER_RELAY_ACK    ≈ 75            SERVER_RELAY_ERROR ≈ 71
Cross-server cost   B(N,C) = (N-1)·(62+C) + 75 + (N-2)·71       (broadcast to N-1, 1 ack, N-2 errors)
                    B(4,C) = 403 + 3C
Relay bandwidth     BW = R · (N-1)/N · B(N,C)        R = total client→client msgs/s
```

At the current N = 4 (cross fraction 3/4):

| content C | relay frame (62+C) | B(4,C) per cross-msg | BW @ R=4,000/s | BW @ R=40,000/s |
| --- | --- | --- | --- | --- |
| 50 B | 112 B | 553 B | ~1.7 MB/s | ~16.6 MB/s |
| 256 B | 318 B | 1,171 B | ~3.5 MB/s | ~35 MB/s |
| 1024 B | 1,086 B | 3,475 B | ~10.4 MB/s | ~104 MB/s |
| ~4000 B (≈ payload max) | ~4,062 B | ~12,403 B | ~37 MB/s | ~372 MB/s |

Plug in real measured `content` and rate for exact figures; `BW` scales linearly in both `R` and (via `B`) `C`.

## Broadcast vs targeted break-even
The targeted fallback (ControlServer location lookup, then one direct relay) costs, **independent of N**:

```text
T_lookup(C) = 20 (LOCATION_REQUEST) + 80 (LOCATION_RESPONSE) + (62+C) (relay) + 75 (ack) = 237 + C
T_cached(C) = (62+C) + 75 = 137 + C         (source already has the client location cached, no lookup)
```

Break-even `B(N,C) = T_lookup(C)` is still between **N = 2** and **N = 3** (C = 256). Broadcast is byte-optimal only for **N = 2**; from **N ≥ 3 the targeted path sends fewer bytes**, and the gap grows ~linearly with N:

| N (servers) | B(N,256) | vs T_lookup (493 B) |
| --- | --- | --- |
| 2 | 393 B | 0.80× (broadcast cheaper) |
| 3 | 782 B | 1.6× |
| 4 | 1,171 B | 2.4× |
| 8 | 2,727 B | 5.5× |
| 16 | 5,839 B | 12× |
| 30 (300k target) | 11,285 B | 23× |

So even at the current 4 nodes, broadcast already sends ~2.4× the bytes of a targeted relay per cross-server message. The current path is **targeted/cache first** and keeps broadcast as a cache-miss fallback. At large N, prefer **targeted with client-location caching** (`T_cached` ≈ 137 + C, no lookup) to cut relay bytes ~10–23×; reserve broadcast for small clusters or as the cache-miss fallback.

## Wire-level overhead (TLS) — cached-target bandwidth
All tables above are **application-frame** bytes (12 B header + protobuf plaintext, the app's byte-counter basis). On the wire each frame also carries transport overhead (see [Protocols → Transport security](Protocols.md#transport-security)):

```text
TLS 1.3 record   ≈ +22 B/frame   (5 B record header + 16 B AEAD tag + 1 B inner content type;
                                   persistent relay channel → handshake amortized; within the
                                   ~22-40 B/record range in Assumptions)
MessageEncryption ≈ +61 B/frame  (version 1 + nonce 12 + AES-GCM tag 16 + HMAC 32)
TCP/IP (optional) ≈ +40 B/segment (IPv4+TCP), if counting full on-wire bytes
```

A cached cross-server message is **2 frames** (one `SERVER_RELAY_MESSAGE` + one `SERVER_RELAY_ACK`), so over TLS:

```text
T_cached(wire) ≈ (203 + C) + 2×22 = 247 + C        (MessageEncryption: 203 + C + 2×61 = 325 + C)
```

Cached-target wire bandwidth (TLS, N = 4, `BW = R · 3/4 · (247 + C)`):

| content C | per cross-msg (wire) | R=667/s | R=4,000/s | R=40,000/s |
| --- | --- | --- | --- | --- |
| 50 B | 297 B | ~0.15 MB/s | ~0.89 MB/s | ~8.9 MB/s (~71 Mbps) |
| 256 B | 503 B | ~0.25 MB/s | ~1.5 MB/s | ~15.1 MB/s (~121 Mbps) |
| 1024 B | 1,271 B | ~0.64 MB/s | ~3.8 MB/s | ~38.1 MB/s (~305 Mbps) |
| ~4000 B (≈ payload max) | 4,247 B | ~2.1 MB/s | ~12.7 MB/s | ~127 MB/s (~1.0 Gbps) |

TLS overhead is relatively larger for small messages (+17% at 50 B, +10% at 256 B) and negligible for large ones (~1% at 4 KB). To get the broadcast wire figures, add `+22 B` to each of its N−1 relay frames + 1 ack + N−2 error frames (i.e. `+22 B × (2N−2)` per cross-server message).

## Side traffic (not the relay hot path)
- **Connection setup (one-time, 40k connects)**: `SESSION_OPENED` 40,000 × 118 B × 2 ControlServers ≈ 9.4 MB, plus control-to-control `CLIENT_LOCATION_UPSERT` 40,000 × 103 B ≈ 4.1 MB → **~13–14 MB**, spread over the connection ramp.
- **Steady-state heartbeat**: 4 × 202 B × 2 ControlServers / 30 s + periodic snapshot ≈ **~0.1 KB/s** — negligible.

## Takeaways
- Individual relay messages are tiny; load is driven by **message rate** and **broadcast amplification**, not message size.
- **Broadcast amplification ∝ server count N**: each cross-server message costs ~(N−1) sends + (N−1) replies. At 4 nodes that is ~3.7× a single message; at **30 nodes (300k target) it is ~36×** → the same message rate produces ~9–10× the relay traffic of the 4-node case.
- Beyond a handful of nodes the **targeted fallback** (one `CLIENT_LOCATION_REQUEST/RESPONSE` + one targeted `SERVER_RELAY` ≈ 0.5 KB, independent of N) is far cheaper than broadcast-first. This is the point to revisit the broadcast-first relay policy. See [Operations → Scale notes](Operations.md#scale-notes).

> Method note: sizes are protobuf field-sum estimates (string fields use representative lengths: `cluster_id`≈16, `instance_id`≈22, `host`≈9, `message_token`≈32–36, `remote_end_point`≈20). Plug in real `content` size and message rate for exact figures.
