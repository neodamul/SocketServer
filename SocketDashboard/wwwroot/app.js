const fields = {
  health: document.getElementById("health"),
  isListening: document.getElementById("isListening"),
  acceptLoop: document.getElementById("acceptLoop"),
  connectedClients: document.getElementById("connectedClients"),
  maxConnections: document.getElementById("maxConnections"),
  acceptedClients: document.getElementById("acceptedClients"),
  closedClients: document.getElementById("closedClients"),
  rejectedClients: document.getElementById("rejectedClients"),
  idleTimeoutClients: document.getElementById("idleTimeoutClients"),
  receivedMessages: document.getElementById("receivedMessages"),
  sentMessages: document.getElementById("sentMessages"),
  saeaPool: document.getElementById("saeaPool"),
  address: document.getElementById("address"),
  backlog: document.getElementById("backlog"),
  pendingAcceptCount: document.getElementById("pendingAcceptCount"),
  idleTimeoutSeconds: document.getElementById("idleTimeoutSeconds"),
  noDelay: document.getElementById("noDelay"),
  maxPayload: document.getElementById("maxPayload"),
  saeaCreated: document.getElementById("saeaCreated"),
  saeaInUse: document.getElementById("saeaInUse"),
  saeaHighWatermark: document.getElementById("saeaHighWatermark"),
  saeaGrowth: document.getElementById("saeaGrowth"),
  startedAt: document.getElementById("startedAt"),
  updatedAt: document.getElementById("updatedAt")
};

function yesNo(value) {
  return value ? "ON" : "OFF";
}

function localTime(value) {
  return value ? new Date(value).toLocaleString() : "-";
}

function setHealth(ok) {
  fields.health.textContent = ok ? "Online" : "Offline";
  fields.health.classList.toggle("ok", ok);
  fields.health.classList.toggle("bad", !ok);
}

async function refresh() {
  try {
    const response = await fetch("/api/server/status", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const status = await response.json();
    const server = status.server;
    const online = status.startSucceeded && server.isListening && server.isAcceptLoopRunning;

    setHealth(online);
    fields.isListening.textContent = yesNo(server.isListening);
    fields.acceptLoop.textContent = yesNo(server.isAcceptLoopRunning);
    fields.connectedClients.textContent = server.connectedClientCount;
    fields.maxConnections.textContent = server.maxConnections;
    fields.acceptedClients.textContent = server.totalAcceptedClients;
    fields.closedClients.textContent = server.totalClosedClients;
    fields.rejectedClients.textContent = server.totalRejectedClients;
    fields.idleTimeoutClients.textContent = server.totalIdleTimeoutClients;
    fields.receivedMessages.textContent = server.totalReceivedMessages;
    fields.sentMessages.textContent = server.totalSentMessages;
    fields.saeaPool.textContent = server.socketAsyncEventArgsAvailableCount;
    fields.address.textContent = `${server.ipAddress}:${server.port}`;
    fields.backlog.textContent = server.listenBacklog;
    fields.pendingAcceptCount.textContent = server.pendingAcceptCount;
    fields.idleTimeoutSeconds.textContent = `${server.idleTimeoutSeconds}s`;
    fields.noDelay.textContent = yesNo(server.noDelay);
    fields.maxPayload.textContent = `${server.maxPayloadLength} bytes`;
    fields.saeaCreated.textContent = server.socketAsyncEventArgsTotalCreatedCount;
    fields.saeaInUse.textContent = server.socketAsyncEventArgsInUseCount;
    fields.saeaHighWatermark.textContent = server.socketAsyncEventArgsHighWatermarkInUseCount;
    fields.saeaGrowth.textContent = server.socketAsyncEventArgsGrowthCount;
    fields.startedAt.textContent = localTime(server.startedAt);
    fields.updatedAt.textContent = localTime(server.updatedAt);
  } catch {
    setHealth(false);
  }
}

refresh();
setInterval(refresh, 1000);
