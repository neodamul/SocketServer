# SocketServer Wiki

> LLM-maintained knowledge base (Karpathy llm-wiki style). Management rules: see `AGENTS.md` → "Wiki management".
> One page per concept, cross-linked, **updated in the same PR as the code change**. Humans read; agents write.

## Index
- [Architecture](Architecture.md) — system topology, components, routing, relay, active-active, security profiles, dashboard.
- [Protocols](Protocols.md) — common frame, transport security, message protocols, control plane.
- [Configuration](Configuration.md) — config files, security, socket options, certificates, per-module settings.
- [Operations](Operations.md) — build/run, local port map, certificates, load test, scale sizing.
- [Relay Traffic Sizing](RelayTrafficSizing.md) — control/relay message sizes & broadcast amplification at a 40k-client example.
- [Message Envelope Optimization](MessageEnvelopeOptimization.md) — shrinking relay/control envelope fields (binary token, server index, drop name/cluster_id) vs compression.
- [Testing](Testing.md) — test suite scope, load test, log analysis, native samples.
- [Open Source Libraries](OpenSourceLibraries.md) — NuGet dependencies and licenses.

README keeps only quick-start/essentials; durable knowledge lives here.

> Open work / handoff: `wiki/TODO.md` (LOCAL, git-ignored working scratch — like the `*PLAN*.md` files; not tracked, not a concept page).
