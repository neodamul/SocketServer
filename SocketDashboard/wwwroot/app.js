const fields = {
  refreshIntervalSeconds: document.getElementById("refreshIntervalSeconds"),
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
  totalMaxConnections: document.getElementById("totalMaxConnections"),
  totalCurrentConnections: document.getElementById("totalCurrentConnections"),
  totalAvailableConnections: document.getElementById("totalAvailableConnections"),
  serverInventoryCount: document.getElementById("serverInventoryCount"),
  controlServerInventoryCount: document.getElementById("controlServerInventoryCount"),
  controlServers: document.getElementById("controlServers"),
  clusterServers: document.getElementById("clusterServers"),
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

const DEFAULT_REFRESH_SECONDS = 30;
let refreshTimer = null;
let refreshInFlight = false;

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

function percent(value) {
  if (value === null || value === undefined) {
    return "-";
  }

  return `${Math.round((value || 0) * 10) / 10}%`;
}

function buildDashboardServerRow(server) {
  return {
    type: "Dashboard",
    instanceId: server.instanceId || "dashboardServer",
    health: server.isListening && server.isAcceptLoopRunning ? "Healthy" : "Unhealthy",
    host: server.ipAddress,
    port: server.port,
    maxConnections: server.maxConnections,
    currentConnections: server.connectedClientCount,
    availableConnections: server.availableConnections,
    resourceUsage: null
  };
}

function sameEndpoint(left, right) {
  return left.instanceId === right.instanceId &&
    left.host === right.host &&
    Number(left.port) === Number(right.port);
}

function renderServers(clusterServers, dashboardServer) {
  const dashboardRow = buildDashboardServerRow(dashboardServer);
  const socketRows = (clusterServers || [])
    .filter(server => !sameEndpoint(server, dashboardRow))
    .map(server => ({ ...server, type: "SocketServer" }));
  const rows = [...socketRows, dashboardRow];
  fields.serverInventoryCount.textContent = rows.length;

  if (rows.length === 0) {
    fields.clusterServers.innerHTML = "<tr><td colspan=\"10\">-</td></tr>";
    return;
  }

  fields.clusterServers.innerHTML = rows.map(server => `
    <tr>
      <td>${server.type}</td>
      <td>${server.instanceId}</td>
      <td>${server.health}</td>
      <td>${server.host}:${server.port}</td>
      <td>${server.maxConnections}</td>
      <td>${server.currentConnections}</td>
      <td>${server.availableConnections}</td>
      <td>${percent(server.resourceUsage?.cpuUsagePercent)}</td>
      <td>${percent(server.resourceUsage?.memoryUsagePercent)}</td>
      <td>${percent(server.resourceUsage?.storageUsagePercent)}</td>
    </tr>
  `).join("");
}

function renderControlServers(controlServers) {
  const rows = controlServers || [];
  fields.controlServerInventoryCount.textContent = rows.length;

  if (rows.length === 0) {
    fields.controlServers.innerHTML = "<tr><td colspan=\"8\">-</td></tr>";
    return;
  }

  fields.controlServers.innerHTML = rows.map(server => `
    <tr>
      <td>${server.host}:${server.port}</td>
      <td>${server.status}</td>
      <td>${server.serverCount}</td>
      <td>${server.healthyServerCount}</td>
      <td>${server.totalCurrentConnections}</td>
      <td>${server.totalAvailableConnections}</td>
      <td>${server.totalSessionCount}</td>
      <td>${localTime(server.checkedAt)}</td>
    </tr>
  `).join("");
}

async function refresh() {
  if (refreshInFlight) {
    return;
  }

  refreshInFlight = true;
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
    fields.totalMaxConnections.textContent = status.cluster.totalMaxConnections;
    fields.totalCurrentConnections.textContent = status.cluster.totalCurrentConnections;
    fields.totalAvailableConnections.textContent = status.cluster.totalAvailableConnections;
    renderControlServers(status.controlServers);
    renderServers(status.cluster.servers, server);
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
  } finally {
    refreshInFlight = false;
  }
}

function getRefreshIntervalMilliseconds() {
  const selectedSeconds = Number(fields.refreshIntervalSeconds?.value);
  const seconds = Number.isFinite(selectedSeconds) && selectedSeconds > 0
    ? selectedSeconds
    : DEFAULT_REFRESH_SECONDS;
  return seconds * 1000;
}

function scheduleRefresh() {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer);
  }

  refreshTimer = setInterval(refresh, getRefreshIntervalMilliseconds());
}

fields.refreshIntervalSeconds?.addEventListener("change", () => {
  scheduleRefresh();
  refresh();
});

refresh();
scheduleRefresh();
