package com.neodamul.socketsample;

import java.io.DataInputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.Base64;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

public final class SocketMessageProtector {
    private static final int NONCE_LENGTH = 12;
    private static final int TAG_LENGTH = 16;
    private static final int HMAC_LENGTH = 32;
    private static final int ENVELOPE_VERSION = 1;
    private static final int MAX_PROTECTED_PAYLOAD_LENGTH =
        SocketFrame.MAX_PAYLOAD_LENGTH + 1 + NONCE_LENGTH + TAG_LENGTH + HMAC_LENGTH;

    private final byte[] encryptionKey;
    private final byte[] authenticationKey;
    private final SecureRandom random = new SecureRandom();

    public SocketMessageProtector(String secret) throws Exception {
        if (secret == null || secret.trim().isEmpty()) {
            throw new IllegalArgumentException("Message encryption secret is required.");
        }

        byte[] secretBytes = decodeSecret(secret.trim());
        this.encryptionKey = hmac(secretBytes, "SocketServer:AES-GCM-256".getBytes(StandardCharsets.UTF_8));
        this.authenticationKey = hmac(secretBytes, "SocketServer:HMAC-SHA256".getBytes(StandardCharsets.UTF_8));
    }

    public SocketFrame protect(SocketFrame frame) throws Exception {
        byte[] nonce = new byte[NONCE_LENGTH];
        random.nextBytes(nonce);

        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(encryptionKey, "AES"), new GCMParameterSpec(TAG_LENGTH * 8, nonce));
        cipher.updateAAD(associatedData(frame.clientId, frame.messageId));
        byte[] cipherWithTag = cipher.doFinal(frame.payload);
        int cipherTextLength = cipherWithTag.length - TAG_LENGTH;

        byte[] envelopeWithoutHmac = new byte[1 + NONCE_LENGTH + TAG_LENGTH + cipherTextLength];
        envelopeWithoutHmac[0] = (byte)ENVELOPE_VERSION;
        System.arraycopy(nonce, 0, envelopeWithoutHmac, 1, NONCE_LENGTH);
        System.arraycopy(cipherWithTag, cipherTextLength, envelopeWithoutHmac, 1 + NONCE_LENGTH, TAG_LENGTH);
        System.arraycopy(cipherWithTag, 0, envelopeWithoutHmac, 1 + NONCE_LENGTH + TAG_LENGTH, cipherTextLength);

        byte[] protectedHeader = header(frame.clientId, frame.messageId, envelopeWithoutHmac.length + HMAC_LENGTH);
        byte[] mac = hmac(authenticationKey, concat(protectedHeader, envelopeWithoutHmac));
        return new SocketFrame(frame.clientId, frame.messageId, concat(envelopeWithoutHmac, mac));
    }

    public SocketFrame read(DataInputStream input) throws Exception {
        byte[] header = readFully(input, SocketFrame.HEADER_LENGTH);

        long clientId = readUInt32(header, 0);
        long messageId = readUInt32(header, 4);
        int payloadLength = (int)readUInt32(header, 8);
        if (payloadLength > MAX_PROTECTED_PAYLOAD_LENGTH) {
            throw new IllegalStateException("Protected payload is too large.");
        }

        byte[] payload = readFully(input, payloadLength);

        return unprotect(new SocketFrame(clientId, messageId, payload));
    }

    private SocketFrame unprotect(SocketFrame protectedFrame) throws Exception {
        byte[] protectedPayload = protectedFrame.payload;
        if (protectedPayload.length < 1 + NONCE_LENGTH + TAG_LENGTH + HMAC_LENGTH ||
            (protectedPayload[0] & 0xff) != ENVELOPE_VERSION) {
            throw new IllegalStateException("Invalid protected payload.");
        }

        int envelopeLength = protectedPayload.length - HMAC_LENGTH;
        byte[] envelopeWithoutHmac = Arrays.copyOfRange(protectedPayload, 0, envelopeLength);
        byte[] receivedHmac = Arrays.copyOfRange(protectedPayload, envelopeLength, protectedPayload.length);
        byte[] protectedHeader = header(protectedFrame.clientId, protectedFrame.messageId, protectedPayload.length);
        byte[] computedHmac = hmac(authenticationKey, concat(protectedHeader, envelopeWithoutHmac));
        if (!constantTimeEquals(receivedHmac, computedHmac)) {
            throw new IllegalStateException("Protected payload HMAC validation failed.");
        }

        byte[] nonce = Arrays.copyOfRange(envelopeWithoutHmac, 1, 1 + NONCE_LENGTH);
        byte[] tag = Arrays.copyOfRange(envelopeWithoutHmac, 1 + NONCE_LENGTH, 1 + NONCE_LENGTH + TAG_LENGTH);
        byte[] cipherText = Arrays.copyOfRange(envelopeWithoutHmac, 1 + NONCE_LENGTH + TAG_LENGTH, envelopeLength);
        if (cipherText.length > SocketFrame.MAX_PAYLOAD_LENGTH) {
            throw new IllegalStateException("Plain payload is too large.");
        }

        byte[] cipherWithTag = concat(cipherText, tag);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(encryptionKey, "AES"), new GCMParameterSpec(TAG_LENGTH * 8, nonce));
        cipher.updateAAD(associatedData(protectedFrame.clientId, protectedFrame.messageId));
        byte[] payload = cipher.doFinal(cipherWithTag);
        return new SocketFrame(protectedFrame.clientId, protectedFrame.messageId, payload);
    }

    private static byte[] decodeSecret(String secret) {
        try {
            return Base64.getDecoder().decode(secret);
        } catch (IllegalArgumentException ignored) {
            return secret.getBytes(StandardCharsets.UTF_8);
        }
    }

    private static byte[] hmac(byte[] key, byte[] data) throws Exception {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        return mac.doFinal(data);
    }

    private static byte[] associatedData(long clientId, long messageId) {
        ByteBuffer buffer = ByteBuffer.allocate(8);
        buffer.putInt((int)clientId);
        buffer.putInt((int)messageId);
        return buffer.array();
    }

    private static byte[] header(long clientId, long messageId, int payloadLength) {
        ByteBuffer buffer = ByteBuffer.allocate(SocketFrame.HEADER_LENGTH);
        buffer.putInt((int)clientId);
        buffer.putInt((int)messageId);
        buffer.putInt(payloadLength);
        return buffer.array();
    }

    private static long readUInt32(byte[] data, int offset) {
        return ((long)(data[offset] & 0xff) << 24) |
            ((long)(data[offset + 1] & 0xff) << 16) |
            ((long)(data[offset + 2] & 0xff) << 8) |
            ((long)(data[offset + 3] & 0xff));
    }

    private static byte[] readFully(DataInputStream input, int length) throws Exception {
        byte[] buffer = new byte[length];
        input.readFully(buffer);
        return buffer;
    }

    private static byte[] concat(byte[] first, byte[] second) {
        byte[] result = new byte[first.length + second.length];
        System.arraycopy(first, 0, result, 0, first.length);
        System.arraycopy(second, 0, result, first.length, second.length);
        return result;
    }

    private static boolean constantTimeEquals(byte[] first, byte[] second) {
        if (first.length != second.length) {
            return false;
        }

        int diff = 0;
        for (int i = 0; i < first.length; i++) {
            diff |= first[i] ^ second[i];
        }

        return diff == 0;
    }
}
