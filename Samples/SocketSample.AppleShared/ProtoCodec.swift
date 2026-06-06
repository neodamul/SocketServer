import Foundation

struct ClientMessageDelivery {
    let sourceClientId: UInt32
    let targetClientId: UInt32
    let content: String
}

struct ClientMessageAckResult {
    let targetClientId: UInt32
    let delivered: Bool
}

struct RouteTarget {
    let success: Bool
    let host: String
    let port: UInt16
    let errorMessage: String
}

enum ProtoCodec {
    static func routeRequest(clientId: UInt32) -> Data {
        var data = Data()
        data.appendVarintField(1, UInt64(clientId))
        data.appendStringField(3, "MostAvailableConnections")
        return data
    }

    static func decodeRouteResponse(_ data: Data) -> RouteTarget {
        let parsed = fields(from: data)
        return RouteTarget(
            success: parsed.bools[1] ?? false,
            host: parsed.strings[5] ?? "",
            port: UInt16(parsed.varints[6] ?? 0),
            errorMessage: parsed.strings[8] ?? "Route failed.")
    }

    static func clientRegister(clientId: UInt32) -> Data {
        var data = Data()
        data.appendVarintField(1, UInt64(clientId))
        data.appendVarintField(2, UInt64(Date().unixMilliseconds))
        return data
    }

    static func clientMessageSend(sourceClientId: UInt32, targetClientId: UInt32, content: String) -> Data {
        var data = Data()
        data.appendStringField(1, UUID().uuidString.replacingOccurrences(of: "-", with: ""))
        data.appendVarintField(2, UInt64(sourceClientId))
        data.appendVarintField(3, UInt64(targetClientId))
        data.appendStringField(4, content)
        data.appendVarintField(5, 10)
        data.appendVarintField(6, UInt64(Date().unixMilliseconds))
        return data
    }

    static func decodeRegisterAck(_ data: Data) -> Bool {
        fields(from: data).bools[2] ?? false
    }

    static func decodeDelivery(_ data: Data) -> ClientMessageDelivery? {
        let parsed = fields(from: data)
        guard let source = parsed.varints[2],
              let target = parsed.varints[3],
              let content = parsed.strings[4] else {
            return nil
        }

        return ClientMessageDelivery(
            sourceClientId: UInt32(source),
            targetClientId: UInt32(target),
            content: content)
    }

    static func decodeAck(_ data: Data) -> ClientMessageAckResult {
        let parsed = fields(from: data)
        return ClientMessageAckResult(
            targetClientId: UInt32(parsed.varints[3] ?? 0),
            delivered: parsed.bools[4] ?? false)
    }

    static func decodeErrorMessage(_ data: Data) -> String {
        let parsed = fields(from: data)
        return parsed.strings[5] ?? "Message failed."
    }

    private static func fields(from data: Data) -> ParsedFields {
        var offset = 0
        var parsed = ParsedFields()
        while offset < data.count {
            guard let key = data.readVarint(offset: &offset) else {
                break
            }

            let field = Int(key >> 3)
            let wireType = Int(key & 0x7)
            switch wireType {
            case 0:
                guard let value = data.readVarint(offset: &offset) else { return parsed }
                parsed.varints[field] = value
                parsed.bools[field] = value != 0
            case 2:
                guard let length = data.readVarint(offset: &offset) else { return parsed }
                let end = offset + Int(length)
                guard end <= data.count else { return parsed }
                parsed.strings[field] = String(data: data[offset..<end], encoding: .utf8) ?? ""
                offset = end
            default:
                return parsed
            }
        }

        return parsed
    }
}

private struct ParsedFields {
    var varints: [Int: UInt64] = [:]
    var bools: [Int: Bool] = [:]
    var strings: [Int: String] = [:]
}

private extension Data {
    mutating func appendFieldKey(_ field: Int, wireType: Int) {
        appendVarint(UInt64((field << 3) | wireType))
    }

    mutating func appendVarintField(_ field: Int, _ value: UInt64) {
        appendFieldKey(field, wireType: 0)
        appendVarint(value)
    }

    mutating func appendStringField(_ field: Int, _ value: String) {
        let bytes = Data(value.utf8)
        appendFieldKey(field, wireType: 2)
        appendVarint(UInt64(bytes.count))
        append(bytes)
    }

    mutating func appendVarint(_ value: UInt64) {
        var remaining = value
        while remaining >= 0x80 {
            append(UInt8(remaining & 0x7f) | 0x80)
            remaining >>= 7
        }

        append(UInt8(remaining))
    }

    func readVarint(offset: inout Int) -> UInt64? {
        var shift = 0
        var result: UInt64 = 0
        while offset < count && shift <= 63 {
            let byte = self[offset]
            offset += 1
            result |= UInt64(byte & 0x7f) << shift
            if (byte & 0x80) == 0 {
                return result
            }

            shift += 7
        }

        return nil
    }
}

private extension Date {
    var unixMilliseconds: Int64 {
        Int64(timeIntervalSince1970 * 1000)
    }
}
