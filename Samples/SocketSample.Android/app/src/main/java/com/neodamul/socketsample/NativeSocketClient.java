package com.neodamul.socketsample;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

public final class NativeSocketClient implements AutoCloseable {
    private static final long CLIENT_REGISTER = 2000;
    private static final long CLIENT_REGISTER_ACK = 2001;
    private static final long CLIENT_MESSAGE_SEND = 2002;
    private static final long CLIENT_MESSAGE_DELIVER = 2003;
    private static final long CLIENT_MESSAGE_ACK = 2004;
    private static final long CLIENT_MESSAGE_ERROR = 2005;

    private SampleConfig config;
    private SSLSocket socket;
    private DataInputStream input;
    private DataOutputStream output;

    public NativeSocketClient(SampleConfig config) {
        this.config = config;
    }

    public void update(SampleConfig config) {
        this.config = config;
    }

    public boolean isConnected() {
        return socket != null && socket.isConnected() && !socket.isClosed();
    }

    public void connect() throws Exception {
        close();
        SSLSocketFactory factory = createSocketFactory(config.allowUntrustedLocalCertificate);
        socket = (SSLSocket)factory.createSocket(config.host, config.port);
        socket.startHandshake();
        input = new DataInputStream(socket.getInputStream());
        output = new DataOutputStream(socket.getOutputStream());
    }

    public void register() throws Exception {
        send(new SocketFrame(config.clientId, CLIENT_REGISTER, ProtoCodec.clientRegister(config.clientId)));
        SocketFrame frame = SocketFrame.read(input);
        if (frame.messageId != CLIENT_REGISTER_ACK || !ProtoCodec.decodeRegisterAck(frame.payload)) {
            throw new IllegalStateException("Register failed.");
        }
    }

    public void sendMessage(long targetClientId, String content) throws Exception {
        send(new SocketFrame(
            config.clientId,
            CLIENT_MESSAGE_SEND,
            ProtoCodec.clientMessageSend(config.clientId, targetClientId, content)));
        SocketFrame frame = SocketFrame.read(input);
        if (frame.messageId == CLIENT_MESSAGE_ACK && ProtoCodec.decodeAckDelivered(frame.payload)) {
            return;
        }

        if (frame.messageId == CLIENT_MESSAGE_ERROR) {
            throw new IllegalStateException(ProtoCodec.decodeErrorMessage(frame.payload));
        }

        throw new IllegalStateException("Invalid message response.");
    }

    public ProtoCodec.ClientDelivery receiveMessage() throws Exception {
        socket.setSoTimeout(Math.max(1, config.receiveTimeoutSeconds) * 1000);
        SocketFrame frame = SocketFrame.read(input);
        if (frame.messageId != CLIENT_MESSAGE_DELIVER) {
            throw new IllegalStateException("Invalid delivery frame.");
        }

        return ProtoCodec.decodeDelivery(frame.payload);
    }

    private void send(SocketFrame frame) throws Exception {
        output.write(frame.encode());
        output.flush();
    }

    @Override
    public void close() {
        try {
            if (socket != null) {
                socket.close();
            }
        } catch (Exception ignored) {
        }

        socket = null;
        input = null;
        output = null;
    }

    private static SSLSocketFactory createSocketFactory(boolean allowUntrustedLocalCertificate) throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        TrustManager[] trustManagers = allowUntrustedLocalCertificate
            ? new TrustManager[] { new TrustAllManager() }
            : null;
        context.init(null, trustManagers, new SecureRandom());
        return context.getSocketFactory();
    }

    private static final class TrustAllManager implements X509TrustManager {
        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) {
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) {
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    }
}
