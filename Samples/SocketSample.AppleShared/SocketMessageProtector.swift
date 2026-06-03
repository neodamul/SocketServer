import CryptoKit
import Foundation

struct SocketMessageProtector {
    static let nonceLength = 12
    static let tagLength = 16
    static let hmacLength = 32
    static let envelopeVersion: UInt8 = 1
    static let maxProtectedPayloadLength = SocketFrame.maxPayloadLength + 1 + nonceLength + tagLength + hmacLength

    private let encryptionKey: SymmetricKey
    private let authenticationKey: SymmetricKey

    init(secret: String) throws {
        guard !secret.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw NativeSocketError.messageFailed("Message encryption secret is required.")
        }

        let secretBytes = Data(base64Encoded: secret) ?? Data(secret.utf8)
        let encryptionKeyData = Data(HMAC<SHA256>.authenticationCode(
            for: Data("SocketServer:AES-GCM-256".utf8),
            using: SymmetricKey(data: secretBytes)))
        let authenticationKeyData = Data(HMAC<SHA256>.authenticationCode(
            for: Data("SocketServer:HMAC-SHA256".utf8),
            using: SymmetricKey(data: secretBytes)))
        encryptionKey = SymmetricKey(data: encryptionKeyData)
        authenticationKey = SymmetricKey(data: authenticationKeyData)
    }

    func protect(_ frame: SocketFrame) throws -> SocketFrame {
        var nonceBytes = Data(count: Self.nonceLength)
        let status = nonceBytes.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, Self.nonceLength, buffer.baseAddress!)
        }
        guard status == errSecSuccess else {
            throw NativeSocketError.messageFailed("Nonce generation failed.")
        }

        let nonce = try AES.GCM.Nonce(data: nonceBytes)
        let sealed = try AES.GCM.seal(
            frame.payload,
            using: encryptionKey,
            nonce: nonce,
            authenticating: associatedData(clientId: frame.clientId, messageId: frame.messageId))

        var envelope = Data([Self.envelopeVersion])
        envelope.append(nonceBytes)
        envelope.append(sealed.tag)
        envelope.append(sealed.ciphertext)

        let protectedHeader = header(
            clientId: frame.clientId,
            messageId: frame.messageId,
            payloadLength: UInt32(envelope.count + Self.hmacLength))
        let mac = Data(HMAC<SHA256>.authenticationCode(for: protectedHeader + envelope, using: authenticationKey))
        envelope.append(mac)
        return SocketFrame(clientId: frame.clientId, messageId: frame.messageId, payload: envelope)
    }

    func unprotect(_ protectedFrame: SocketFrame) throws -> SocketFrame {
        let payload = protectedFrame.payload
        guard payload.count >= 1 + Self.nonceLength + Self.tagLength + Self.hmacLength,
              payload[0] == Self.envelopeVersion else {
            throw NativeSocketError.invalidFrame
        }

        let envelopeLength = payload.count - Self.hmacLength
        let envelope = payload.prefix(envelopeLength)
        let receivedHmac = payload.suffix(Self.hmacLength)
        let protectedHeader = header(
            clientId: protectedFrame.clientId,
            messageId: protectedFrame.messageId,
            payloadLength: UInt32(payload.count))
        guard HMAC<SHA256>.isValidAuthenticationCode(
            receivedHmac,
            authenticating: protectedHeader + envelope,
            using: authenticationKey) else {
            throw NativeSocketError.invalidFrame
        }

        let nonceBytes = envelope.dropFirst().prefix(Self.nonceLength)
        let tag = envelope.dropFirst(1 + Self.nonceLength).prefix(Self.tagLength)
        let cipherText = envelope.dropFirst(1 + Self.nonceLength + Self.tagLength)
        guard cipherText.count <= SocketFrame.maxPayloadLength else {
            throw NativeSocketError.invalidFrame
        }

        let sealedBox = try AES.GCM.SealedBox(
            nonce: AES.GCM.Nonce(data: nonceBytes),
            ciphertext: cipherText,
            tag: tag)
        let plainPayload = try AES.GCM.open(
            sealedBox,
            using: encryptionKey,
            authenticating: associatedData(clientId: protectedFrame.clientId, messageId: protectedFrame.messageId))
        return SocketFrame(
            clientId: protectedFrame.clientId,
            messageId: protectedFrame.messageId,
            payload: plainPayload)
    }

    private func associatedData(clientId: UInt32, messageId: UInt32) -> Data {
        var data = Data()
        data.appendUInt32BE(clientId)
        data.appendUInt32BE(messageId)
        return data
    }

    private func header(clientId: UInt32, messageId: UInt32, payloadLength: UInt32) -> Data {
        var data = Data()
        data.appendUInt32BE(clientId)
        data.appendUInt32BE(messageId)
        data.appendUInt32BE(payloadLength)
        return data
    }
}
