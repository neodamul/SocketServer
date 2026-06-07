import SwiftUI

struct SampleContentView: View {
    @State private var config: SampleConfig
    @State private var targetClientId: String
    @State private var message = "hello"
    @State private var state = ClientState()
    @State private var client: NativeSocketClient
    @State private var receiveTask: Task<Void, Never>?

    init(
        config initialConfig: SampleConfig = SampleConfig.fromProcessArguments(),
        targetClientId initialTargetClientId: String = SampleConfig.targetClientIdFromProcessArguments())
    {
        _config = State(initialValue: initialConfig)
        _targetClientId = State(initialValue: initialTargetClientId)
        _client = State(initialValue: NativeSocketClient(config: initialConfig))
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Socket Sample Client")
                    .font(.title2)
                    .fontWeight(.semibold)

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
                        Toggle("Use ControlServer route", isOn: $config.useControlServer)
                        Toggle("Allow local self-signed certificate", isOn: $config.allowUntrustedLocalCertificate)
                        HStack {
                            field("Transport", text: $config.transportMode)
                            field("Message Secret", text: $config.messageEncryptionSecret)
                        }
                        HStack {
                            Button("Connect") { run { try await connect() } }
                            Button("Disconnect") { disconnect() }
                        }
                    }
                }

                GroupBox {
                    VStack(spacing: 10) {
                        HStack {
                            field("Target Client ID", text: $targetClientId)
                            field("Message", text: $message)
                        }
                        HStack {
                            Button("Send") { run { try await send() } }
                        }
                    }
                }

                GroupBox("Status") {
                    Text(statusText)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .font(.system(.body, design: .monospaced))
                }
            }
            .padding()
        }
        .frame(minWidth: 420, minHeight: 520)
        .onAppear {
            if config.autoConnect && !state.isConnected {
                run {
                    try await connect()
                }
            }
        }
    }

    private var statusText: String {
        """
        Status: \(state.status)
        Connected: \(state.isConnected)
        Registered: \(state.isRegistered)
        Connected Server: \(state.connectedServer)
        Last Received: \(state.lastReceived)
        Last Error: \(state.lastError)
        """
    }

    private func field(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading) {
            Text(title).font(.caption).fontWeight(.semibold)
            TextField(title, text: text)
                .textFieldStyle(.roundedBorder)
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
    }

    private func send() async throws {
        try await client.sendMessage(targetClientId: UInt32(targetClientId) ?? 0, content: message)
        state.status = "Message sent"
        state.lastError = ""
    }

    private func disconnect() {
        stopReceiveLoop()
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
                        state.status = "Receive loop stopped"
                        state.lastError = String(describing: error)
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
