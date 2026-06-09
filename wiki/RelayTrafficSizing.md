# Relay Traffic Sizing

Size estimate for the control-plane and server-to-server **relay** messages exchanged between ControlServer and SocketServer, at a worked example of **40,000 connected clients**. Grounded in `SocketCommon/Proto/SocketMessages.proto` and the 12-byte frame header (see [Protocols → Common frame](Protocols.md#common-frame)).

## Assumptions
- 40,000 clients = **4 SocketServers × 10,000** (current default sizing), **2 ControlServers** (active-active).
- Frame = **12 B header + protobuf payload** (plaintext basis, identical to the app's message-byte counters; see [Protocols → Transport security](Protocols.md#transport-security)). TLS adds ~25–40 B per record on the wire, counted separately.
- protobuf (proto3) omits zero/empty fields; varints: `clientId` 40000 → 3 B, `unix_ms` → 6 B, plus a 1 B tag (2 B for field numbers ≥ 16).

## Per-message size (frame = 12 B + payload)
| Message | Hop | payload | frame |
| --- | --- | --- | --- |
| `SERVER_RELAY_MESSAGE` (content 256 B) | socket→socket | ~356 B | **~368 B** |
| `SERVER_RELAY_MESSAGE` (content 50 B) | socket→socket | ~150 B | ~162 B |
| `SERVER_RELAY_ACK` (delivered) | socket→socket | ~79 B | ~91 B |
| `SERVER_RELAY_ERROR` (`TargetNotConnected`) | socket→socket | ~75 B | ~87 B |
| `CLIENT_LOCATION_REQUEST` (fallback lookup) | socket→control | ~8 B | ~20 B |
| `CLIENT_LOCATION_RESPONSE` | control→socket | ~68 B | ~80 B |
| `SERVER_HEARTBEAT` | socket→control | ~190 B | ~202 B |
| `SESSION_OPENED` / `SESSION_CLOSED` | socket→control | ~106 B | ~118 B |
| `CLIENT_LOCATION_UPSERT` | control→control | ~91 B | ~103 B |

The relay messages themselves are small (tens to a few hundred bytes); `content` is the main variable (capped by `MaxPayloadLength` = 4 KB on the whole payload).

## Cross-server message cost (broadcast model)
When the target client is on a **different** server, the source server **broadcasts `SERVER_RELAY_MESSAGE` to all other servers** in parallel (N−1 = 3 here); only the holder returns `SERVER_RELAY_ACK`, the rest return `SERVER_RELAY_ERROR` (`TargetNotConnected`). See [Protocols → Server relay](Protocols.md#server-relay) and [Architecture → SocketServer](Architecture.md#socketserver).

```text
content 256 B: 3×368 (broadcast) + 91 (ack) + 2×87 (error) ≈ 1,369 B  (~1.34 KB)
content  50 B: 3×162           + 91       + 2×87        ≈   751 B
```
With a random target across 4 servers, the cross-server probability ≈ **3/4** (the other 1/4 is same-server local delivery → 0 relay).

## Aggregate relay bandwidth (scales with message rate R)
Relay-plane bandwidth ≈ `(R × 3/4) × 1.37 KB` (content 256 B), where R = total client-to-client messages/s across all clients:

| Per-client send rate | total R | cross-server/s | relay bandwidth |
| --- | --- | --- | --- |
| 1 / min | 667/s | 500/s | **~0.69 MB/s (~5.5 Mbps)** |
| 1 / 10 s | 4,000/s | 3,000/s | ~4.1 MB/s (~33 Mbps) |
| 1 / s | 40,000/s | 30,000/s | ~41 MB/s (~330 Mbps) |

## Side traffic (not the relay hot path)
- **Connection setup (one-time, 40k connects)**: `SESSION_OPENED` 40,000 × 118 B × 2 ControlServers ≈ 9.4 MB, plus control-to-control `CLIENT_LOCATION_UPSERT` 40,000 × 103 B ≈ 4.1 MB → **~13–14 MB**, spread over the connection ramp.
- **Steady-state heartbeat**: 4 × 202 B × 2 ControlServers / 30 s + periodic snapshot ≈ **~0.1 KB/s** — negligible.

## Takeaways
- Individual relay messages are tiny; load is driven by **message rate** and **broadcast amplification**, not message size.
- **Broadcast amplification ∝ server count N**: each cross-server message costs ~(N−1) sends + (N−1) replies. At 4 nodes that is ~3.7× a single message; at **30 nodes (300k target) it is ~36×** → the same message rate produces ~9–10× the relay traffic of the 4-node case.
- Beyond a handful of nodes the **targeted fallback** (one `CLIENT_LOCATION_REQUEST/RESPONSE` + one targeted `SERVER_RELAY` ≈ 0.5 KB, independent of N) is far cheaper than broadcast-first. This is the point to revisit the broadcast-first relay policy. See [Operations → Scale notes](Operations.md#scale-notes).

> Method note: sizes are protobuf field-sum estimates (string fields use representative lengths: `cluster_id`≈16, `instance_id`≈22, `host`≈9, `message_token`≈32–36, `remote_end_point`≈20). Plug in real `content` size and message rate for exact figures.
