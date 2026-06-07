import SwiftUI

struct SampleContentView: View {
    @State private var config: SampleConfig
    @State private var targetClientId: String
    @State private var message = "hello"
    @State private var state = ClientState()
    @State private var client: NativeSocketClient
    @State private var receiveTask: Task<Void, Never>?
    @State private var healthCheckTask: Task<Void, Never>?

    init(
        config initialConfig: SampleConfig = SampleConfig.fromProcessArguments(),
        targetClientId initialTargetClientId: String = SampleConfig.targetClientIdFromProcessArguments())
    {
        _config = State(initialValue: initialConfig)
        _targetClientId = State(initialValue: initialTargetClientId)
        _client = State(initialValue: NativeSocketClient(config: initialConfig))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Socket Sample Client")
                .font(.title2)
                .fontWeight(.semibold)

            controlsPanel
                .layoutPriority(1)

            statusPanel
        }
        .padding()
        .frame(minWidth: 680, minHeight: 560)
        .onAppear {
            if config.autoConnect && !state.isConnected {
                run {
                    try await connect()
                }
            }
        }
    }

    private var controlsPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                connectionPanel
                messagePanel
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var connectionPanel: some View {
        GroupBox {
            VStack(spacing: 10) {
                HStack {
                    field("Client ID", text: Binding(
                        get: { String(config.clientId) },
                        set: { config.clientId = UInt32($0) ?? config.clientId }))
                    field("Client Name", text: $config.clientName)
                }
                HStack {
                    field("Host", text: $config.host)
                    field("Port", text: Binding(
                        get: { String(config.port) },
                        set: { config.port = UInt16($0) ?? config.port }))
                }
                compactMultilineField("Control Endpoints", text: Binding(
                    get: { config.controlEndpoints.map(\.displayValue).joined(separator: "\n") },
                    set: { config.controlEndpoints = SampleConfig.parseControlEndpoints($0) }))
                HStack {
                    Toggle("Use ControlServer route", isOn: $config.useControlServer)
                    Toggle("Allow local self-signed certificate", isOn: $config.allowUntrustedLocalCertificate)
                    Spacer()
                }
                HStack {
                    field("Transport", text: $config.transportMode)
                    field("Message Secret", text: $config.messageEncryptionSecret)
                }
                HStack {
                    Spacer()
                    Button("Connect") { run { try await connect() } }
                    Button("Disconnect") { disconnect() }
                }
            }
        }
    }

    private var messagePanel: some View {
        GroupBox {
            VStack(spacing: 10) {
                HStack {
                    field("Target Client ID", text: $targetClientId)
                    field("Message", text: $message)
                }
                HStack {
                    Spacer()
                    Button("Send") { run { try await send() } }
                }
            }
        }
    }

    private var statusPanel: some View {
        GroupBox("Status") {
            VStack(alignment: .leading, spacing: 6) {
                statusRow("Status", state.status)
                statusRow("Connected", String(state.isConnected))
                statusRow("Registered", String(state.isRegistered))
                statusRow("Server", state.connectedServer)
                statusRow("Last Received", lastReceivedDisplay)
                statusRow("Last Error", state.lastError)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var lastReceivedDisplay: String {
        guard !state.lastReceived.isEmpty else {
            return ""
        }

        guard !state.lastReceivedAt.isEmpty else {
            return state.lastReceived
        }

        return "\(state.lastReceived) @ \(state.lastReceivedAt)"
    }

    private func statusRow(_ title: String, _ value: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(title)
                .font(.caption)
                .fontWeight(.semibold)
                .foregroundStyle(.secondary)
                .frame(width: 92, alignment: .leading)
            Text(value.isEmpty ? "-" : value)
                .font(.system(.caption, design: .monospaced))
                .lineLimit(2)
                .truncationMode(.middle)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func field(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading) {
            Text(title).font(.caption).fontWeight(.semibold)
            TextField(title, text: text)
                .textFieldStyle(.roundedBorder)
        }
    }

    private func compactMultilineField(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading) {
            Text(title).font(.caption).fontWeight(.semibold)
            TextEditor(text: text)
                .frame(height: 46)
                .overlay(
                    RoundedRectangle(cornerRadius: 6)
                        .stroke(Color.secondary.opacity(0.35)))
        }
    }

    private func connect() async throws {
        stopReceiveLoop()
        client.update(config: config)
        let connectedServer = try await client.connect()
        try await client.register()
        state.isConnected = true
        state.isRegistered = true
        state.status = "Connected and registered"
        state.connectedServer = connectedServer
        state.lastError = ""
        startReceiveLoop()
        startHealthCheckLoop()
    }

    private func send() async throws {
        try await client.sendMessage(targetClientId: UInt32(targetClientId) ?? 0, content: message)
        state.status = "Message sent"
        state.lastError = ""
    }

    private func disconnect() {
        stopReceiveLoop()
        stopHealthCheckLoop()
        client.disconnect()
        state.isConnected = false
        state.isRegistered = false
        state.connectedServer = ""
        state.status = "Disconnected"
    }

    private func startReceiveLoop() {
        stopReceiveLoop()
        receiveTask = Task {
            while !Task.isCancelled {
                do {
                    let event = try await client.receiveEvent()
                    switch event {
                    case .delivery(let delivery):
                        state.lastReceived = "\(delivery.sourceClientId): \(delivery.content)"
                        state.lastReceivedAt = Date().formatted(date: .omitted, time: .standard)
                        state.status = "Message received"
                        state.lastError = ""
                    case .ack(let ack):
                        state.status = ack.delivered
                            ? "Message delivered to \(ack.targetClientId)"
                            : "Message not delivered to \(ack.targetClientId)"
                        state.lastError = ack.delivered ? "" : "Message was not delivered."
                    case .error(let message):
                        state.status = "Message failed"
                        state.lastError = message
                    case .ignored:
                        continue
                    }
                } catch {
                    if !Task.isCancelled {
                        state.isConnected = false
                        state.isRegistered = false
                        state.connectedServer = ""
                        state.status = "Disconnected"
                        state.lastError = String(describing: error)
                        stopHealthCheckLoop()
                    }
                    return
                }
            }
        }
    }

    private func stopReceiveLoop() {
        receiveTask?.cancel()
        receiveTask = nil
    }

    private func startHealthCheckLoop() {
        stopHealthCheckLoop()
        healthCheckTask = Task {
            let interval = max(config.healthCheckIntervalSeconds, 1)
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: interval * 1_000_000_000)
                    try await client.sendHealthCheck()
                } catch {
                    if !Task.isCancelled {
                        state.isConnected = false
                        state.isRegistered = false
                        state.connectedServer = ""
                        state.status = "Disconnected"
                        state.lastError = String(describing: error)
                        stopReceiveLoop()
                    }
                    return
                }
            }
        }
    }

    private func stopHealthCheckLoop() {
        healthCheckTask?.cancel()
        healthCheckTask = nil
    }

    private func run(_ action: @escaping () async throws -> Void) {
        Task {
            do {
                try await action()
            } catch {
                state.lastError = String(describing: error)
                state.status = "Failed"
            }
        }
    }
}
