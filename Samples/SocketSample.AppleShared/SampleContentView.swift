import SwiftUI

struct SampleContentView: View {
    @State private var config = SampleConfig()
    @State private var targetClientId = "2"
    @State private var message = "hello"
    @State private var state = ClientState()
    @State private var client = NativeSocketClient(config: SampleConfig())

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
                        Toggle("Allow local self-signed certificate", isOn: $config.allowUntrustedLocalCertificate)
                        HStack {
                            field("Transport", text: $config.transportMode)
                            field("Message Secret", text: $config.messageEncryptionSecret)
                        }
                        HStack {
                            Button("Connect") { run { try await connect() } }
                            Button("Register") { run { try await register() } }
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
                            Button("Receive") { run { try await receive() } }
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
    }

    private var statusText: String {
        """
        Status: \(state.status)
        Connected: \(state.isConnected)
        Registered: \(state.isRegistered)
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
        client.update(config: config)
        try await client.connect()
        state.isConnected = true
        state.status = "Connected"
        state.lastError = ""
    }

    private func register() async throws {
        try await client.register()
        state.isRegistered = true
        state.status = "Registered"
        state.lastError = ""
    }

    private func send() async throws {
        try await client.sendMessage(targetClientId: UInt32(targetClientId) ?? 0, content: message)
        state.status = "Message sent"
        state.lastError = ""
    }

    private func receive() async throws {
        let delivery = try await client.receiveMessage()
        state.lastReceived = "\(delivery.sourceClientId): \(delivery.content)"
        state.status = "Message received"
        state.lastError = ""
    }

    private func disconnect() {
        client.disconnect()
        state.isConnected = false
        state.isRegistered = false
        state.status = "Disconnected"
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
