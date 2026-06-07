import Foundation

struct ControlEndpoint: Codable, Equatable {
    var host: String
    var port: UInt16

    var displayValue: String {
        "\(host):\(port)"
    }
}

struct SampleConfig: Codable, Equatable {
    var clientId: UInt32 = 1
    var clientName: String = "apple-native-client"
    var host: String = "127.0.0.1"
    var port: UInt16 = 10000
    var useControlServer: Bool = true
    var controlEndpoints: [ControlEndpoint] = []
    var autoConnect: Bool = false
    var receiveTimeoutSeconds: Int = 10
    var allowUntrustedLocalCertificate: Bool = true
    var transportMode: String = "Tls"
    var messageEncryptionSecret: String = ""
    var certificateDirectory: String = ProcessInfo.processInfo.environment["SOCKET_CERTIFICATE_DIR"] ?? ""
    var certificatePassword: String = ProcessInfo.processInfo.environment["SOCKET_CERTIFICATE_PASSWORD"] ?? ""
    var healthCheckIntervalSeconds: UInt64 = 30

    var usesMessageEncryption: Bool {
        transportMode.caseInsensitiveCompare("MessageEncryption") == .orderedSame ||
            transportMode.caseInsensitiveCompare("Encrypted") == .orderedSame ||
            transportMode.caseInsensitiveCompare("PlainEncrypted") == .orderedSame
    }

    var effectiveControlEndpoints: [ControlEndpoint] {
        controlEndpoints.isEmpty
            ? [ControlEndpoint(host: host, port: port)]
            : controlEndpoints
    }

    static func fromProcessArguments(_ arguments: [String] = ProcessInfo.processInfo.arguments) -> SampleConfig {
        var config = SampleConfig()
        for (name, value) in parseArguments(arguments) {
            switch name {
            case "client-id":
                config.clientId = UInt32(value) ?? config.clientId
            case "client-name":
                config.clientName = value
            case "host":
                config.host = value
            case "port":
                config.port = UInt16(value) ?? config.port
            case "use-control-server":
                config.useControlServer = parseBoolean(value)
            case "control-endpoints":
                config.controlEndpoints = parseControlEndpoints(value)
            case "auto-connect":
                config.autoConnect = parseBoolean(value)
            case "transport":
                config.transportMode = value
            case "message-secret":
                config.messageEncryptionSecret = value
            case "certificate-dir":
                config.certificateDirectory = value
            case "certificate-password":
                config.certificatePassword = value
            case "healthcheck-interval-seconds":
                if let seconds = UInt64(value), seconds > 0 {
                    config.healthCheckIntervalSeconds = seconds
                }
            default:
                continue
            }
        }

        return config
    }

    static func targetClientIdFromProcessArguments(_ arguments: [String] = ProcessInfo.processInfo.arguments) -> String {
        parseArguments(arguments)["target-client-id"] ?? "2"
    }

    private static func parseArguments(_ arguments: [String]) -> [String: String] {
        var values: [String: String] = [:]
        var index = 0
        while index < arguments.count {
            let argument = arguments[index]
            guard argument.hasPrefix("--") else {
                index += 1
                continue
            }

            let nameAndValue = String(argument.dropFirst(2)).split(separator: "=", maxSplits: 1).map(String.init)
            if nameAndValue.count == 2 {
                values[nameAndValue[0]] = nameAndValue[1]
                index += 1
                continue
            }

            if index + 1 < arguments.count {
                values[nameAndValue[0]] = arguments[index + 1]
                index += 2
                continue
            }

            index += 1
        }

        return values
    }

    private static func parseBoolean(_ value: String) -> Bool {
        value.caseInsensitiveCompare("true") == .orderedSame ||
            value == "1" ||
            value.caseInsensitiveCompare("yes") == .orderedSame ||
            value.caseInsensitiveCompare("on") == .orderedSame
    }

    static func parseControlEndpoints(_ value: String) -> [ControlEndpoint] {
        value
            .split { character in
                character == "," || character == "\n"
            }
            .compactMap { item -> ControlEndpoint? in
                let text = item.trimmingCharacters(in: .whitespacesAndNewlines)
                guard let separator = text.lastIndex(of: ":") else {
                    return nil
                }

                let host = String(text[..<separator])
                let portText = String(text[text.index(after: separator)...])
                guard !host.isEmpty, let port = UInt16(portText), port > 0 else {
                    return nil
                }

                return ControlEndpoint(host: host, port: port)
            }
    }
}

struct ClientState {
    var isConnected = false
    var isRegistered = false
    var status = "Disconnected"
    var connectedServer = ""
    var lastReceived = ""
    var lastReceivedAt = ""
    var lastError = ""
}
