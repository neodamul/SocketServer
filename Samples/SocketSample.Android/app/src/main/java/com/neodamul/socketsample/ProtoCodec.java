package com.neodamul.socketsample;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

public final class ProtoCodec {
    private ProtoCodec() {
    }

    public static byte[] clientRegister(long clientId) {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        writeVarintField(output, 1, clientId);
        writeVarintField(output, 2, System.currentTimeMillis());
        return output.toByteArray();
    }

    public static byte[] clientMessageSend(long sourceClientId, long targetClientId, String content) {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        writeStringField(output, 1, UUID.randomUUID().toString().replace("-", ""));
        writeVarintField(output, 2, sourceClientId);
        writeVarintField(output, 3, targetClientId);
        writeStringField(output, 4, content == null ? "" : content);
        writeVarintField(output, 5, 10);
        writeVarintField(output, 6, System.currentTimeMillis());
        return output.toByteArray();
    }

    public static boolean decodeRegisterAck(byte[] payload) {
        return parse(payload).bools.getOrDefault(2, false);
    }

    public static boolean decodeAckDelivered(byte[] payload) {
        return parse(payload).bools.getOrDefault(4, false);
    }

    public static String decodeErrorMessage(byte[] payload) {
        return parse(payload).strings.getOrDefault(5, "Message failed.");
    }

    public static ClientDelivery decodeDelivery(byte[] payload) {
        Parsed parsed = parse(payload);
        long source = parsed.varints.getOrDefault(2, 0L);
        long target = parsed.varints.getOrDefault(3, 0L);
        String content = parsed.strings.getOrDefault(4, "");
        return new ClientDelivery(source, target, content);
    }

    private static Parsed parse(byte[] payload) {
        Parsed parsed = new Parsed();
        int[] offset = new int[] { 0 };
        while (offset[0] < payload.length) {
            long key = readVarint(payload, offset);
            int field = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            if (wireType == 0) {
                long value = readVarint(payload, offset);
                parsed.varints.put(field, value);
                parsed.bools.put(field, value != 0);
            } else if (wireType == 2) {
                int length = (int)readVarint(payload, offset);
                if (offset[0] + length > payload.length) {
                    return parsed;
                }

                parsed.strings.put(field, new String(payload, offset[0], length, StandardCharsets.UTF_8));
                offset[0] += length;
            } else {
                return parsed;
            }
        }

        return parsed;
    }

    private static void writeVarintField(ByteArrayOutputStream output, int field, long value) {
        writeVarint(output, (field << 3));
        writeVarint(output, value);
    }

    private static void writeStringField(ByteArrayOutputStream output, int field, String value) {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        writeVarint(output, (field << 3) | 2);
        writeVarint(output, bytes.length);
        output.writeBytes(bytes);
    }

    private static void writeVarint(ByteArrayOutputStream output, long value) {
        long remaining = value;
        while (remaining >= 0x80) {
            output.write((int)((remaining & 0x7f) | 0x80));
            remaining >>= 7;
        }

        output.write((int)remaining);
    }

    private static long readVarint(byte[] data, int[] offset) {
        int shift = 0;
        long result = 0;
        while (offset[0] < data.length && shift <= 63) {
            int value = data[offset[0]++] & 0xff;
            result |= (long)(value & 0x7f) << shift;
            if ((value & 0x80) == 0) {
                return result;
            }

            shift += 7;
        }

        return result;
    }

    private static final class Parsed {
        final Map<Integer, Long> varints = new HashMap<>();
        final Map<Integer, Boolean> bools = new HashMap<>();
        final Map<Integer, String> strings = new HashMap<>();
    }

    public static final class ClientDelivery {
        public final long sourceClientId;
        public final long targetClientId;
        public final String content;

        ClientDelivery(long sourceClientId, long targetClientId, String content) {
            this.sourceClientId = sourceClientId;
            this.targetClientId = targetClientId;
            this.content = content;
        }
    }
}
