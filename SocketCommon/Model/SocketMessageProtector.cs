using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SocketCommon.Model;

internal sealed class SocketMessageProtector
{
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int HmacLength = 32;
    public const int EnvelopeVersion = 1;
    public const int EnvelopeOverhead = 1 + NonceLength + TagLength + HmacLength;
    public const int MaxProtectedPayloadLength = SocketMessageFrame.MaxPayloadLength + EnvelopeOverhead;

    private readonly byte[] encryptionKey;
    private readonly byte[] authenticationKey;

    private SocketMessageProtector(byte[] encryptionKey, byte[] authenticationKey)
    {
        this.encryptionKey = encryptionKey;
        this.authenticationKey = authenticationKey;
    }

    public static SocketMessageProtector FromSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Message encryption secret is required when TLS transport is disabled.");
        }

        byte[] secretBytes = DecodeSecret(secret);
        byte[] encryptionKey = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes("SocketServer:AES-GCM-256"));
        byte[] authenticationKey = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes("SocketServer:HMAC-SHA256"));
        return new SocketMessageProtector(encryptionKey, authenticationKey);
    }

    public SocketMessageFrame Protect(SocketMessageFrame frame)
    {
        byte[] nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        byte[] cipherText = new byte[frame.Payload.Length];
        byte[] tag = new byte[TagLength];
        byte[] aad = CreateAssociatedData(frame.ClientId, frame.MessageId);
        using (AesGcm aes = new(this.encryptionKey, TagLength))
        {
            aes.Encrypt(nonce, frame.Payload, cipherText, tag, aad);
        }

        byte[] envelopeWithoutHmac = new byte[1 + NonceLength + TagLength + cipherText.Length];
        envelopeWithoutHmac[0] = EnvelopeVersion;
        Buffer.BlockCopy(nonce, 0, envelopeWithoutHmac, 1, NonceLength);
        Buffer.BlockCopy(tag, 0, envelopeWithoutHmac, 1 + NonceLength, TagLength);
        Buffer.BlockCopy(cipherText, 0, envelopeWithoutHmac, 1 + NonceLength + TagLength, cipherText.Length);

        byte[] protectedHeader = CreateHeader(frame.ClientId, frame.MessageId, envelopeWithoutHmac.Length + HmacLength);
        byte[] hmac = ComputeHmac(protectedHeader, envelopeWithoutHmac);
        byte[] protectedPayload = new byte[envelopeWithoutHmac.Length + hmac.Length];
        Buffer.BlockCopy(envelopeWithoutHmac, 0, protectedPayload, 0, envelopeWithoutHmac.Length);
        Buffer.BlockCopy(hmac, 0, protectedPayload, envelopeWithoutHmac.Length, hmac.Length);

        return new SocketMessageFrame(frame.ClientId, frame.MessageId, protectedPayload, skipPayloadLimitValidation: true);
    }

    public bool TryUnprotect(SocketMessageFrame protectedFrame, out SocketMessageFrame frame)
    {
        frame = null;
        byte[] protectedPayload = protectedFrame.Payload;
        if (protectedPayload.Length < EnvelopeOverhead ||
            protectedPayload[0] != EnvelopeVersion)
        {
            return false;
        }

        int envelopeLength = protectedPayload.Length - HmacLength;
        byte[] envelopeWithoutHmac = new byte[envelopeLength];
        byte[] receivedHmac = new byte[HmacLength];
        Buffer.BlockCopy(protectedPayload, 0, envelopeWithoutHmac, 0, envelopeLength);
        Buffer.BlockCopy(protectedPayload, envelopeLength, receivedHmac, 0, HmacLength);

        byte[] protectedHeader = CreateHeader(protectedFrame.ClientId, protectedFrame.MessageId, protectedPayload.Length);
        byte[] computedHmac = ComputeHmac(protectedHeader, envelopeWithoutHmac);
        if (!CryptographicOperations.FixedTimeEquals(receivedHmac, computedHmac))
        {
            return false;
        }

        byte[] nonce = new byte[NonceLength];
        byte[] tag = new byte[TagLength];
        int cipherTextLength = envelopeLength - 1 - NonceLength - TagLength;
        if (cipherTextLength < 0 || cipherTextLength > SocketMessageFrame.MaxPayloadLength)
        {
            return false;
        }

        byte[] cipherText = new byte[cipherTextLength];
        Buffer.BlockCopy(envelopeWithoutHmac, 1, nonce, 0, NonceLength);
        Buffer.BlockCopy(envelopeWithoutHmac, 1 + NonceLength, tag, 0, TagLength);
        Buffer.BlockCopy(envelopeWithoutHmac, 1 + NonceLength + TagLength, cipherText, 0, cipherTextLength);

        byte[] plainPayload = new byte[cipherTextLength];
        try
        {
            using AesGcm aes = new(this.encryptionKey, TagLength);
            aes.Decrypt(nonce, cipherText, tag, plainPayload, CreateAssociatedData(protectedFrame.ClientId, protectedFrame.MessageId));
        }
        catch (CryptographicException)
        {
            return false;
        }

        frame = new SocketMessageFrame(protectedFrame.ClientId, protectedFrame.MessageId, plainPayload);
        return true;
    }

    private byte[] ComputeHmac(byte[] protectedHeader, byte[] envelopeWithoutHmac)
    {
        byte[] data = new byte[protectedHeader.Length + envelopeWithoutHmac.Length];
        Buffer.BlockCopy(protectedHeader, 0, data, 0, protectedHeader.Length);
        Buffer.BlockCopy(envelopeWithoutHmac, 0, data, protectedHeader.Length, envelopeWithoutHmac.Length);
        return HMACSHA256.HashData(this.authenticationKey, data);
    }

    private static byte[] CreateAssociatedData(uint clientId, uint messageId)
    {
        byte[] aad = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0, 4), clientId);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(4, 4), messageId);
        return aad;
    }

    private static byte[] CreateHeader(uint clientId, uint messageId, int payloadLength)
    {
        byte[] header = new byte[SocketMessageFrame.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), clientId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), messageId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)payloadLength);
        return header;
    }

    private static byte[] DecodeSecret(string secret)
    {
        string trimmed = secret.Trim();
        try
        {
            return Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(trimmed);
        }
    }
}
