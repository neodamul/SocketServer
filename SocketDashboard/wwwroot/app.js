const fields = {
  health: document.getElementById("health"),
  isListening: document.getElementById("isListening"),
  acceptLoop: document.getElementById("acceptLoop"),
  connectedClients: document.getElementById("connectedClients"),
  acceptedClients: document.getElementById("acceptedClients"),
  closedClients: document.getElementById("closedClients"),
  receivedMessages: document.getElementById("receivedMessages"),
  sentMessages: document.getElementById("sentMessages"),
  saeaPool: document.getElementById("saeaPool"),
  address: document.getElementById("address"),
  backlog: document.getElementById("backlog"),
  noDelay: document.getElementById("noDelay"),
  maxPayload: document.getElementById("maxPayload"),
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
    fields.acceptedClients.textContent = server.totalAcceptedClients;
    fields.closedClients.textContent = server.totalClosedClients;
    fields.receivedMessages.textContent = server.totalReceivedMessages;
    fields.sentMessages.textContent = server.totalSentMessages;
    fields.saeaPool.textContent = server.socketAsyncEventArgsAvailableCount;
    fields.address.textContent = `${server.ipAddress}:${server.port}`;
    fields.backlog.textContent = server.listenBacklog;
    fields.noDelay.textContent = yesNo(server.noDelay);
    fields.maxPayload.textContent = `${server.maxPayloadLength} bytes`;
    fields.startedAt.textContent = localTime(server.startedAt);
    fields.updatedAt.textContent = localTime(server.updatedAt);
  } catch {
    setHealth(false);
  }
}

refresh();
setInterval(refresh, 1000);
