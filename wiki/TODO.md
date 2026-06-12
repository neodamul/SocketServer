# TODO — Open work for Codex

> Working handoff list (tracked in git so it is visible from every worktree). Detailed step/AC live in the `*PLAN*.md` files in the **main worktree root** (`/Users/neodamul/Development/SocketServer/`, git-excluded). Mark items done here as they land.

## Owned by Codex

- [ ] **Live-stack heartbeat config** — SocketServer reports heartbeat via nginx `127.0.0.1:10000` (single endpoint), and nginx stream `proxy_timeout` previously equalled the 30s heartbeat interval → idle-kill race. Point `controlServers` directly at `127.0.0.1:10001` + `127.0.0.1:10002` (A/B direct report) and restart. The repo default `SocketServer/config.json` `controlServers` also points at `10000` — fix the runtime/config accordingly. Detail: `LIVE_STACK_HEARTBEAT_FIX_PLAN.md`.

- [ ] **Load test route-perf verification** — nginx capacity fix is **already applied** (by Claude: `worker_processes auto`, `worker_connections 65535`, `multi_accept on`, `listen 10000 backlog=4096`, `proxy_connect_timeout 5s`). Run a controlled 10k route ramp and confirm AC: route retries drop from ~8,200 to ~0, wall-clock < ~1 min (floor = mTLS TLS1.3 handshake CPU). Optional Fix B: point the load test route directly at 10001/10002 (bypass nginx). Detail: `LOADTEST_ROUTE_PERF_PLAN.md`.

- [ ] **Relay client-location cache (cache-first, fallback)** — implement the per-SocketServer in-process client-location cache so cross-server client→client relay skips the per-message ControlServer lookup on a hit, with safe fallback on miss/stale. Not yet implemented (no `ClientLocationCache` in `SocketServer/Model`). Detail + steps S1–S6 + AC: `RELAY_LOCATION_CACHE_PLAN.md`.

- [ ] **Load test (LoadUtil) runs & validation** — owned by Codex per the user.

## Doc/code sync (do alongside the relay change)

- [ ] **Wiki drift — `wiki/Protocols.md` "Server relay"** still describes *broadcast-first, lookup-as-fallback*. The actual code order is **local delivery → ControlServer location lookup (targeted relay) → broadcast fallback** (`SocketServer/Model/TcpServer.cs` `HandleClientMessageAsync`). Correct the doc in the same PR as the relay cache change. (Tracked as S6 in `RELAY_LOCATION_CACHE_PLAN.md`.)

## Environment / host (user-level)

- [ ] **Machine storage ~96% used** > `degradedStoragePercent` (90) → ControlServer may classify SocketServers `Degraded` and exclude them from routing. Free disk space, or raise `degradedStoragePercent` (e.g. 98) in the runtime ControlServer config for local dev.

---

## Done (recent, for reference)
- ✅ TcpClient connect made truly async (`ConnectAsync`) — PR #47.
- ✅ nginx route admission capacity (worker_processes auto, worker_connections 65535, backlog 4096) — applied to `/opt/homebrew/etc/nginx/nginx.conf`.
- ✅ Load test UI: incremental client scaling, per-server clients popup, collapsible State clients.
- ✅ Dashboard + Load test UI professional dark theme.
- ✅ Same-ClientId register policy (`retryAfterSeconds`) + client auto-reconnect.
