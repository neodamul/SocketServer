import Foundation

struct SampleConfig: Codable, Equatable {
    var clientId: UInt32 = 1
    var clientName: String = "apple-native-client"
    var host: String = "127.0.0.1"
    var port: UInt16 = 5000
    var receiveTimeoutSeconds: Int = 10
    var allowUntrustedLocalCertificate: Bool = true
    var transportMode: String = "Tls"
    var messageEncryptionSecret: String = ""

    var usesMessageEncryption: Bool {
        transportMode.caseInsensitiveCompare("MessageEncryption") == .orderedSame ||
            transportMode.caseInsensitiveCompare("Encrypted") == .orderedSame ||
            transportMode.caseInsensitiveCompare("PlainEncrypted") == .orderedSame
    }
}

struct ClientState {
    var isConnected = false
    var isRegistered = false
    var status = "Disconnected"
    var lastReceived = ""
    var lastError = ""
}
