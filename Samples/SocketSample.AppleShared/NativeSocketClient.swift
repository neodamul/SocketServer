import Foundation
import Network

enum NativeSocketError: Error {
    case connectFailed
    case invalidFrame
    case receiveFailed
    case registerFailed
    case messageFailed(String)
}

final class NativeSocketClient {
    private let queue = DispatchQueue(label: "socket-sample.native-client")
    private var connection: NWConnection?
    private var protector: SocketMessageProtector?
    private(set) var config: SampleConfig

    init(config: SampleConfig) {
        self.config = config
    }

    var isConnected: Bool {
        connection != nil
    }

    func update(config: SampleConfig) {
        self.config = config
    }

    func connect() async throws {
        disconnect()

        let target = try await resolveConnectionTarget()
        try await connectToHost(target.host, port: target.port)
    }

    private func resolveConnectionTarget() async throws -> (host: String, port: UInt16) {
        try prepareProtector()
        guard config.useControlServer else {
            return (config.host, config.port)
        }

        let controlConnection = try await openConnection(host: config.host, port: config.port)
        defer {
            controlConnection.cancel()
        }

        let request = SocketFrame(
            clientId: config.clientId,
            messageId: SocketMessageId.routeRequest,
            payload: ProtoCodec.routeRequest(clientId: config.clientId))
        try await send(request, using: controlConnection)
        let response = try await receiveFrame(using: controlConnection)
        guard response.messageId == SocketMessageId.routeResponse else {
            throw NativeSocketError.messageFailed("Invalid route response.")
        }

        let route = ProtoCodec.decodeRouteResponse(response.payload)
        guard route.success, !route.host.isEmpty, route.port > 0 else {
            throw NativeSocketError.messageFailed(route.errorMessage)
        }

        return (route.host, route.port)
    }

    private func connectToHost(_ host: String, port: UInt16) async throws {
        try prepareProtector()
        self.connection = try await openConnection(host: host, port: port)
    }

    private func prepareProtector() throws {
        protector = config.usesMessageEncryption
            ? try SocketMessageProtector(secret: config.messageEncryptionSecret)
            : nil
    }

    private func openConnection(host: String, port: UInt16) async throws -> NWConnection {
        let tcpOptions = NWProtocolTCP.Options()
        let parameters: NWParameters
        if config.usesMessageEncryption {
            parameters = NWParameters(tls: nil, tcp: tcpOptions)
        } else {
            let tlsOptions = NWProtocolTLS.Options()
            if config.allowUntrustedLocalCertificate {
                sec_protocol_options_set_verify_block(tlsOptions.securityProtocolOptions, { _, _, complete in
                    complete(true)
                }, queue)
            }

            parameters = NWParameters(tls: tlsOptions, tcp: tcpOptions)
        }

        let connection = NWConnection(
            host: NWEndpoint.Host(host),
            port: NWEndpoint.Port(rawValue: port) ?? 10000,
            using: parameters)

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    connection.stateUpdateHandler = nil
                    continuation.resume()
                case .failed(let error):
                    connection.stateUpdateHandler = nil
                    continuation.resume(throwing: error)
                default:
                    break
                }
            }
            connection.start(queue: queue)
        }

        return connection
    }

    func register() async throws {
        let payload = ProtoCodec.clientRegister(clientId: config.clientId)
        try await send(SocketFrame(
            clientId: config.clientId,
            messageId: SocketMessageId.clientRegister,
            payload: payload))
        let frame = try await receiveFrame()
        guard frame.messageId == SocketMessageId.clientRegisterAck,
              ProtoCodec.decodeRegisterAck(frame.payload) else {
            throw NativeSocketError.registerFailed
        }
    }

    func sendMessage(targetClientId: UInt32, content: String) async throws {
        let payload = ProtoCodec.clientMessageSend(
            sourceClientId: config.clientId,
            targetClientId: targetClientId,
            content: content)
        try await send(SocketFrame(
            clientId: config.clientId,
            messageId: SocketMessageId.clientMessageSend,
            payload: payload))
        let frame = try await receiveFrame()
        if frame.messageId == SocketMessageId.clientMessageAck,
           ProtoCodec.decodeAckDelivered(frame.payload) {
            return
        }

        if frame.messageId == SocketMessageId.clientMessageError {
            throw NativeSocketError.messageFailed(ProtoCodec.decodeErrorMessage(frame.payload))
        }

        throw NativeSocketError.messageFailed("Invalid message response.")
    }

    func receiveMessage() async throws -> ClientMessageDelivery {
        let frame = try await receiveFrame()
        guard frame.messageId == SocketMessageId.clientMessageDeliver,
              let delivery = ProtoCodec.decodeDelivery(frame.payload) else {
            throw NativeSocketError.receiveFailed
        }

        return delivery
    }

    func disconnect() {
        connection?.cancel()
        connection = nil
        protector = nil
    }

    private func send(_ frame: SocketFrame) async throws {
        guard let connection else {
            throw NativeSocketError.connectFailed
        }

        try await send(frame, using: connection)
    }

    private func send(_ frame: SocketFrame, using connection: NWConnection) async throws {
        let data = try (protector?.protect(frame) ?? frame).encode()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            })
        }
    }

    private func receiveFrame() async throws -> SocketFrame {
        guard let connection else {
            throw NativeSocketError.connectFailed
        }

        return try await receiveFrame(using: connection)
    }

    private func receiveFrame(using connection: NWConnection) async throws -> SocketFrame {
        let header = try await receive(length: SocketFrame.headerLength, using: connection)
        let payloadLength = Int(header.readUInt32BE(at: 8))
        let maxPayloadLength = protector == nil
            ? SocketFrame.maxPayloadLength
            : SocketMessageProtector.maxProtectedPayloadLength
        guard payloadLength <= maxPayloadLength else {
            throw NativeSocketError.invalidFrame
        }

        let payload = payloadLength == 0 ? Data() : try await receive(length: payloadLength, using: connection)
        let frame = try SocketFrame.decode(header: header, payload: payload)
        return try protector?.unprotect(frame) ?? frame
    }

    private func receive(length: Int) async throws -> Data {
        guard let connection else {
            throw NativeSocketError.connectFailed
        }

        return try await receive(length: length, using: connection)
    }

    private func receive(length: Int, using connection: NWConnection) async throws -> Data {
        var buffer = Data()
        while buffer.count < length {
            let chunk = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
                connection.receive(
                    minimumIncompleteLength: length - buffer.count,
                    maximumLength: length - buffer.count) { data, _, isComplete, error in
                        if let error {
                            continuation.resume(throwing: error)
                            return
                        }

                        if isComplete && (data == nil || data?.isEmpty == true) {
                            continuation.resume(throwing: NativeSocketError.receiveFailed)
                            return
                        }

                        continuation.resume(returning: data ?? Data())
                    }
            }

            if chunk.isEmpty {
                throw NativeSocketError.receiveFailed
            }

            buffer.append(chunk)
        }

        return buffer
    }
}
