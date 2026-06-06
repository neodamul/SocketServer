const fields = {
  refreshNow: document.getElementById("refreshNow"),
  refreshIntervalSeconds: document.getElementById("refreshIntervalSeconds"),
  health: document.getElementById("health"),
  selectedServerName: document.getElementById("selectedServerName"),
  isListening: document.getElementById("isListening"),
  acceptLoop: document.getElementById("acceptLoop"),
  connectedClients: document.getElementById("connectedClients"),
  maxConnections: document.getElementById("maxConnections"),
  acceptedClients: document.getElementById("acceptedClients"),
  closedClients: document.getElementById("closedClients"),
  rejectedClients: document.getElementById("rejectedClients"),
  idleTimeoutClients: document.getElementById("idleTimeoutClients"),
  receivedMessages: document.getElementById("receivedMessages"),
  receivedMessageBytes: document.getElementById("receivedMessageBytes"),
  sentMessages: document.getElementById("sentMessages"),
  sentMessageBytes: document.getElementById("sentMessageBytes"),
  saeaPool: document.getElementById("saeaPool"),
  totalMaxConnections: document.getElementById("totalMaxConnections"),
  totalCurrentConnections: document.getElementById("totalCurrentConnections"),
  totalAvailableConnections: document.getElementById("totalAvailableConnections"),
  controlServerCount: document.getElementById("controlServerCount"),
  socketServerCount: document.getElementById("socketServerCount"),
  dashboardServerCount: document.getElementById("dashboardServerCount"),
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
const SERVER_TYPE_ORDER = {
  Dashboard: 0,
  ControlServer: 1,
  SocketServer: 2
};
let refreshTimer = null;
let refreshInFlight = false;
let selectedServerKey = "";
let currentInventoryRows = [];

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

function displayValue(value) {
  return value === null || value === undefined || value === "" ? "-" : value;
}

function boolValue(value) {
  return typeof value === "boolean" ? yesNo(value) : "-";
}

function durationSeconds(value) {
  return value === null || value === undefined ? "-" : `${value}s`;
}

function bytes(value) {
  if (value === null || value === undefined || value === "") {
    return "-";
  }

  const size = Number(value);
  if (!Number.isFinite(size)) {
    return "-";
  }

  if (size >= 1024 * 1024 * 1024) {
    return `${Math.round((size / 1024 / 1024 / 1024) * 10) / 10} GB`;
  }

  if (size >= 1024 * 1024) {
    return `${Math.round((size / 1024 / 1024) * 10) / 10} MB`;
  }

  if (size >= 1024) {
    return `${Math.round((size / 1024) * 10) / 10} KB`;
  }

  return `${size} bytes`;
}

function serverRowKey(server) {
  return `${server.type}:${server.instanceId}:${server.host}:${server.port}`;
}

function sortInventoryRows(servers) {
  return [...servers].sort((left, right) => {
    const leftTypeOrder = SERVER_TYPE_ORDER[left.type] ?? 99;
    const rightTypeOrder = SERVER_TYPE_ORDER[right.type] ?? 99;
    if (leftTypeOrder !== rightTypeOrder) {
      return leftTypeOrder - rightTypeOrder;
    }

    return String(left.instanceId).localeCompare(String(right.instanceId), undefined, { numeric: true });
  });
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
    resourceUsage: null,
    isListening: server.isListening,
    isAcceptLoopRunning: server.isAcceptLoopRunning,
    totalAcceptedClients: server.totalAcceptedClients,
    totalClosedClients: server.totalClosedClients,
    totalRejectedClients: server.totalRejectedClients,
    totalIdleTimeoutClients: server.totalIdleTimeoutClients,
    totalReceivedMessages: server.totalReceivedMessages,
    totalSentMessages: server.totalSentMessages,
    totalReceivedMessageBytes: server.totalReceivedMessageBytes,
    totalSentMessageBytes: server.totalSentMessageBytes,
    listenBacklog: server.listenBacklog,
    pendingAcceptCount: server.pendingAcceptCount,
    idleTimeoutSeconds: server.idleTimeoutSeconds,
    noDelay: server.noDelay,
    maxPayloadLength: server.maxPayloadLength,
    socketAsyncEventArgsAvailableCount: server.socketAsyncEventArgsAvailableCount,
    socketAsyncEventArgsTotalCreatedCount: server.socketAsyncEventArgsTotalCreatedCount,
    socketAsyncEventArgsInUseCount: server.socketAsyncEventArgsInUseCount,
    socketAsyncEventArgsHighWatermarkInUseCount: server.socketAsyncEventArgsHighWatermarkInUseCount,
    socketAsyncEventArgsGrowthCount: server.socketAsyncEventArgsGrowthCount,
    startedAt: server.startedAt,
    updatedAt: server.updatedAt
  };
}

function healthText(value) {
  if (typeof value === "string") {
    return value;
  }

  const labels = ["Unknown", "Healthy", "Degraded", "Unhealthy"];
  return labels[Number(value)] || "Unknown";
}

function buildSocketServerRow(server) {
  return {
    type: "SocketServer",
    instanceId: server.instanceId || server.name || "-",
    health: healthText(server.health),
    host: server.host || server.ipAddress || "-",
    port: server.port ?? "-",
    maxConnections: server.maxConnections ?? "-",
    currentConnections: server.currentConnections ?? server.connectedClientCount ?? "-",
    availableConnections: server.availableConnections ?? "-",
    resourceUsage: server.resourceUsage || null,
    isListening: server.health === 1 || server.health === "Healthy",
    isAcceptLoopRunning: server.health === 1 || server.health === "Healthy",
    totalAcceptedClients: server.totalAcceptedClients ?? "-",
    totalClosedClients: server.totalClosedClients ?? "-",
    totalRejectedClients: server.totalRejectedClients ?? "-",
    totalIdleTimeoutClients: server.totalIdleTimeoutClients ?? "-",
    totalReceivedMessages: server.totalReceivedMessages ?? "-",
    totalSentMessages: server.totalSentMessages ?? "-",
    totalReceivedMessageBytes: server.totalReceivedMessageBytes ?? "-",
    totalSentMessageBytes: server.totalSentMessageBytes ?? "-",
    listenBacklog: server.listenBacklog ?? "-",
    pendingAcceptCount: server.pendingAcceptCount ?? "-",
    idleTimeoutSeconds: server.idleTimeoutSeconds ?? "-",
    noDelay: server.noDelay ?? "-",
    maxPayloadLength: server.maxPayloadLength ?? "-",
    socketAsyncEventArgsAvailableCount: server.socketAsyncEventArgsAvailableCount ?? "-",
    socketAsyncEventArgsTotalCreatedCount: server.socketAsyncEventArgsTotalCreatedCount ?? "-",
    socketAsyncEventArgsInUseCount: server.socketAsyncEventArgsInUseCount ?? "-",
    socketAsyncEventArgsHighWatermarkInUseCount: server.socketAsyncEventArgsHighWatermarkInUseCount ?? "-",
    socketAsyncEventArgsGrowthCount: server.socketAsyncEventArgsGrowthCount ?? "-",
    startedAt: server.startedAt,
    updatedAt: server.updatedAt || server.lastHeartbeatAt
  };
}

function buildControlServerRow(server) {
  return {
    type: "ControlServer",
    instanceId: `control:${server.host}:${server.port}`,
    health: server.status || (server.isHealthy ? "Healthy" : "Unavailable"),
    host: server.host || "-",
    port: server.port ?? "-",
    maxConnections: "-",
    currentConnections: server.totalCurrentConnections ?? "-",
    availableConnections: server.totalAvailableConnections ?? "-",
    resourceUsage: server.resourceUsage || null,
    isListening: server.isHealthy,
    isAcceptLoopRunning: server.isHealthy,
    totalAcceptedClients: "-",
    totalClosedClients: "-",
    totalRejectedClients: "-",
    totalIdleTimeoutClients: "-",
    totalReceivedMessages: "-",
    totalSentMessages: "-",
    totalReceivedMessageBytes: "-",
    totalSentMessageBytes: "-",
    listenBacklog: "-",
    pendingAcceptCount: "-",
    idleTimeoutSeconds: "-",
    noDelay: "-",
    maxPayloadLength: "-",
    socketAsyncEventArgsAvailableCount: "-",
    socketAsyncEventArgsTotalCreatedCount: "-",
    socketAsyncEventArgsInUseCount: "-",
    socketAsyncEventArgsHighWatermarkInUseCount: "-",
    socketAsyncEventArgsGrowthCount: "-",
    startedAt: "-",
    updatedAt: server.checkedAt
  };
}

function sameEndpoint(left, right) {
  return left.instanceId === right.instanceId &&
    (left.host || left.ipAddress) === right.host &&
    Number(left.port) === Number(right.port);
}

function renderServers(clusterServers, dashboardServer, controlServers) {
  const dashboardRow = buildDashboardServerRow(dashboardServer);
  const controlRows = (controlServers || []).map(buildControlServerRow);
  const socketRows = (clusterServers || [])
    .filter(server => !sameEndpoint(server, dashboardRow))
    .map(buildSocketServerRow);
  const rows = sortInventoryRows([dashboardRow, ...controlRows, ...socketRows].filter(Boolean));
  currentInventoryRows = rows.map(server => ({
    ...server,
    key: serverRowKey(server)
  }));
  fields.controlServerCount.textContent = controlRows.length;
  fields.socketServerCount.textContent = socketRows.length;
  fields.dashboardServerCount.textContent = dashboardRow ? 1 : 0;

  if (currentInventoryRows.length === 0) {
    fields.clusterServers.innerHTML = "<tr><td colspan=\"10\">-</td></tr>";
    renderSelectedServer(null);
    return;
  }

  if (!currentInventoryRows.some(server => server.key === selectedServerKey)) {
    selectedServerKey = dashboardRow ? serverRowKey(dashboardRow) : currentInventoryRows[0].key;
  }

  fields.clusterServers.innerHTML = currentInventoryRows.map(server => `
    <tr data-row-key="${server.key}" class="${server.key === selectedServerKey ? "selected-row" : ""}">
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
  renderSelectedServer(currentInventoryRows.find(server => server.key === selectedServerKey));
}

function renderSelectedServer(server) {
  if (!server) {
    fields.selectedServerName.textContent = "-";
    fields.isListening.textContent = "-";
    fields.acceptLoop.textContent = "-";
    fields.connectedClients.textContent = "-";
    fields.maxConnections.textContent = "-";
    fields.acceptedClients.textContent = "-";
    fields.closedClients.textContent = "-";
    fields.rejectedClients.textContent = "-";
    fields.idleTimeoutClients.textContent = "-";
    fields.receivedMessages.textContent = "-";
    fields.receivedMessageBytes.textContent = "-";
    fields.sentMessages.textContent = "-";
    fields.sentMessageBytes.textContent = "-";
    fields.saeaPool.textContent = "-";
    fields.address.textContent = "-";
    fields.backlog.textContent = "-";
    fields.pendingAcceptCount.textContent = "-";
    fields.idleTimeoutSeconds.textContent = "-";
    fields.noDelay.textContent = "-";
    fields.maxPayload.textContent = "-";
    fields.saeaCreated.textContent = "-";
    fields.saeaInUse.textContent = "-";
    fields.saeaHighWatermark.textContent = "-";
    fields.saeaGrowth.textContent = "-";
    fields.startedAt.textContent = "-";
    fields.updatedAt.textContent = "-";
    return;
  }

  fields.selectedServerName.textContent = `${server.type} / ${server.instanceId} / ${server.host}:${server.port}`;
  fields.isListening.textContent = boolValue(server.isListening);
  fields.acceptLoop.textContent = boolValue(server.isAcceptLoopRunning);
  fields.connectedClients.textContent = displayValue(server.currentConnections);
  fields.maxConnections.textContent = displayValue(server.maxConnections);
  fields.acceptedClients.textContent = displayValue(server.totalAcceptedClients);
  fields.closedClients.textContent = displayValue(server.totalClosedClients);
  fields.rejectedClients.textContent = displayValue(server.totalRejectedClients);
  fields.idleTimeoutClients.textContent = displayValue(server.totalIdleTimeoutClients);
  fields.receivedMessages.textContent = displayValue(server.totalReceivedMessages);
  fields.receivedMessageBytes.textContent = bytes(server.totalReceivedMessageBytes);
  fields.sentMessages.textContent = displayValue(server.totalSentMessages);
  fields.sentMessageBytes.textContent = bytes(server.totalSentMessageBytes);
  fields.saeaPool.textContent = displayValue(server.socketAsyncEventArgsAvailableCount);
  fields.address.textContent = `${server.host}:${server.port}`;
  fields.backlog.textContent = displayValue(server.listenBacklog);
  fields.pendingAcceptCount.textContent = displayValue(server.pendingAcceptCount);
  fields.idleTimeoutSeconds.textContent = typeof server.idleTimeoutSeconds === "number"
    ? durationSeconds(server.idleTimeoutSeconds)
    : displayValue(server.idleTimeoutSeconds);
  fields.noDelay.textContent = typeof server.noDelay === "boolean"
    ? yesNo(server.noDelay)
    : displayValue(server.noDelay);
  fields.maxPayload.textContent = typeof server.maxPayloadLength === "number"
    ? bytes(server.maxPayloadLength)
    : displayValue(server.maxPayloadLength);
  fields.saeaCreated.textContent = displayValue(server.socketAsyncEventArgsTotalCreatedCount);
  fields.saeaInUse.textContent = displayValue(server.socketAsyncEventArgsInUseCount);
  fields.saeaHighWatermark.textContent = displayValue(server.socketAsyncEventArgsHighWatermarkInUseCount);
  fields.saeaGrowth.textContent = displayValue(server.socketAsyncEventArgsGrowthCount);
  fields.startedAt.textContent = localTime(server.startedAt);
  fields.updatedAt.textContent = localTime(server.updatedAt);
}

async function refresh() {
  if (refreshInFlight) {
    return;
  }

  refreshInFlight = true;
  if (fields.refreshNow) {
    fields.refreshNow.disabled = true;
  }

  try {
    const response = await fetch("/api/server/status", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const status = await response.json();
    const server = status.server;
    const online = status.startSucceeded && server.isListening && server.isAcceptLoopRunning;

    setHealth(online);
    fields.totalMaxConnections.textContent = status.cluster.totalMaxConnections;
    fields.totalCurrentConnections.textContent = status.cluster.totalCurrentConnections;
    fields.totalAvailableConnections.textContent = status.cluster.totalAvailableConnections;
    renderServers(status.cluster.servers, server, status.controlServers);
  } catch {
    setHealth(false);
  } finally {
    refreshInFlight = false;
    if (fields.refreshNow) {
      fields.refreshNow.disabled = false;
    }
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

fields.clusterServers?.addEventListener("click", event => {
  const row = event.target.closest("tr[data-row-key]");
  if (!row) {
    return;
  }

  selectedServerKey = row.dataset.rowKey;
  for (const tableRow of fields.clusterServers.querySelectorAll("tr[data-row-key]")) {
    tableRow.classList.toggle("selected-row", tableRow.dataset.rowKey === selectedServerKey);
  }

  renderSelectedServer(currentInventoryRows.find(server => server.key === selectedServerKey));
});

fields.refreshIntervalSeconds?.addEventListener("change", () => {
  scheduleRefresh();
  refresh();
});

fields.refreshNow?.addEventListener("click", refresh);

refresh();
scheduleRefresh();
