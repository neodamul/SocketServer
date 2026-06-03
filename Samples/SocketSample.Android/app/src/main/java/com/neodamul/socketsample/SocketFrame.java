package com.neodamul.socketsample;

import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.IOException;

public final class SocketFrame {
    public static final int HEADER_LENGTH = 12;
    public static final int MAX_PAYLOAD_LENGTH = 4096;

    public final long clientId;
    public final long messageId;
    public final byte[] payload;

    public SocketFrame(long clientId, long messageId, byte[] payload) {
        this.clientId = clientId;
        this.messageId = messageId;
        this.payload = payload == null ? new byte[0] : payload;
    }

    public byte[] encode() {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        writeUInt32(output, clientId);
        writeUInt32(output, messageId);
        writeUInt32(output, payload.length);
        output.writeBytes(payload);
        return output.toByteArray();
    }

    public static SocketFrame read(DataInputStream input) throws IOException {
        byte[] header = input.readNBytes(HEADER_LENGTH);
        if (header.length != HEADER_LENGTH) {
            throw new IOException("Frame header was incomplete.");
        }

        long clientId = readUInt32(header, 0);
        long messageId = readUInt32(header, 4);
        int payloadLength = (int)readUInt32(header, 8);
        if (payloadLength > MAX_PAYLOAD_LENGTH) {
            throw new IOException("Payload is too large.");
        }

        byte[] payload = input.readNBytes(payloadLength);
        if (payload.length != payloadLength) {
            throw new IOException("Payload was incomplete.");
        }

        return new SocketFrame(clientId, messageId, payload);
    }

    private static void writeUInt32(ByteArrayOutputStream output, long value) {
        output.write((byte)((value >> 24) & 0xff));
        output.write((byte)((value >> 16) & 0xff));
        output.write((byte)((value >> 8) & 0xff));
        output.write((byte)(value & 0xff));
    }

    private static long readUInt32(byte[] data, int offset) {
        return ((long)(data[offset] & 0xff) << 24) |
            ((long)(data[offset + 1] & 0xff) << 16) |
            ((long)(data[offset + 2] & 0xff) << 8) |
            ((long)(data[offset + 3] & 0xff));
    }
}
