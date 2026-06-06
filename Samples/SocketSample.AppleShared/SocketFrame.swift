import Foundation

enum SocketMessageId {
    static let routeRequest: UInt32 = 1200
    static let routeResponse: UInt32 = 1201
    static let clientRegister: UInt32 = 2000
    static let clientRegisterAck: UInt32 = 2001
    static let clientMessageSend: UInt32 = 2002
    static let clientMessageDeliver: UInt32 = 2003
    static let clientMessageAck: UInt32 = 2004
    static let clientMessageError: UInt32 = 2005
}

struct SocketFrame {
    static let headerLength = 12
    static let maxPayloadLength = 4096

    let clientId: UInt32
    let messageId: UInt32
    let payload: Data

    func encode() -> Data {
        var data = Data()
        data.appendUInt32BE(clientId)
        data.appendUInt32BE(messageId)
        data.appendUInt32BE(UInt32(payload.count))
        data.append(payload)
        return data
    }

    static func decode(header: Data, payload: Data) throws -> SocketFrame {
        guard header.count == headerLength else {
            throw NativeSocketError.invalidFrame
        }

        return SocketFrame(
            clientId: header.readUInt32BE(at: 0),
            messageId: header.readUInt32BE(at: 4),
            payload: payload)
    }
}

extension Data {
    mutating func appendUInt32BE(_ value: UInt32) {
        append(UInt8((value >> 24) & 0xff))
        append(UInt8((value >> 16) & 0xff))
        append(UInt8((value >> 8) & 0xff))
        append(UInt8(value & 0xff))
    }

    func readUInt32BE(at offset: Int) -> UInt32 {
        UInt32(self[offset]) << 24 |
            UInt32(self[offset + 1]) << 16 |
            UInt32(self[offset + 2]) << 8 |
            UInt32(self[offset + 3])
    }
}
