package com.neodamul.socketsample;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.net.Socket;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

public final class NativeSocketClient implements AutoCloseable {
    private static final long ROUTE_REQUEST = 1200;
    private static final long ROUTE_RESPONSE = 1201;
    private static final long CLIENT_REGISTER = 2000;
    private static final long CLIENT_REGISTER_ACK = 2001;
    private static final long CLIENT_MESSAGE_SEND = 2002;
    private static final long CLIENT_MESSAGE_DELIVER = 2003;
    private static final long CLIENT_MESSAGE_ACK = 2004;
    private static final long CLIENT_MESSAGE_ERROR = 2005;

    private SampleConfig config;
    private Socket socket;
    private DataInputStream input;
    private DataOutputStream output;
    private SocketMessageProtector protector;

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
        protector = config.useMessageEncryption()
            ? new SocketMessageProtector(config.messageEncryptionSecret)
            : null;

        ConnectionTarget target = resolveConnectionTarget();
        openSocket(target.host, target.port);
        register();
    }

    private ConnectionTarget resolveConnectionTarget() throws Exception {
        if (!config.useControlServer) {
            return new ConnectionTarget(config.host, config.port);
        }

        try (Socket controlSocket = createSocket(config.host, config.port)) {
            DataInputStream controlInput = new DataInputStream(controlSocket.getInputStream());
            DataOutputStream controlOutput = new DataOutputStream(controlSocket.getOutputStream());
            send(
                controlOutput,
                new SocketFrame(config.clientId, ROUTE_REQUEST, ProtoCodec.routeRequest(config.clientId)));
            SocketFrame response = read(controlInput);
            if (response.messageId != ROUTE_RESPONSE) {
                throw new IllegalStateException("Invalid route response.");
            }

            ProtoCodec.RouteTarget route = ProtoCodec.decodeRouteResponse(response.payload);
            if (!route.success || route.host.isEmpty() || route.port <= 0 || route.port > 65535) {
                throw new IllegalStateException(route.errorMessage);
            }

            return new ConnectionTarget(route.host, (int)route.port);
        }
    }

    private void openSocket(String host, int port) throws Exception {
        socket = createSocket(host, port);
        input = new DataInputStream(socket.getInputStream());
        output = new DataOutputStream(socket.getOutputStream());
    }

    private Socket createSocket(String host, int port) throws Exception {
        if (protector == null) {
            SSLSocketFactory factory = createSocketFactory(config.allowUntrustedLocalCertificate);
            SSLSocket sslSocket = (SSLSocket)factory.createSocket(host, port);
            sslSocket.startHandshake();
            return sslSocket;
        }

        return new Socket(host, port);
    }

    private void register() throws Exception {
        send(new SocketFrame(config.clientId, CLIENT_REGISTER, ProtoCodec.clientRegister(config.clientId)));
        SocketFrame frame = read();
        if (frame.messageId != CLIENT_REGISTER_ACK || !ProtoCodec.decodeRegisterAck(frame.payload)) {
            throw new IllegalStateException("Register failed.");
        }
    }

    public void sendMessage(long targetClientId, String content) throws Exception {
        send(new SocketFrame(
            config.clientId,
            CLIENT_MESSAGE_SEND,
            ProtoCodec.clientMessageSend(config.clientId, targetClientId, content)));
    }

    public ProtoCodec.ClientDelivery receiveMessage() throws Exception {
        socket.setSoTimeout(Math.max(1, config.receiveTimeoutSeconds) * 1000);
        SocketFrame frame = read();
        if (frame.messageId != CLIENT_MESSAGE_DELIVER) {
            throw new IllegalStateException("Invalid delivery frame.");
        }

        return ProtoCodec.decodeDelivery(frame.payload);
    }

    public String receiveEvent() throws Exception {
        SocketFrame frame = read();
        if (frame.messageId == CLIENT_MESSAGE_DELIVER) {
            ProtoCodec.ClientDelivery delivery = ProtoCodec.decodeDelivery(frame.payload);
            return "Received from " + delivery.sourceClientId + ": " + delivery.content;
        }

        if (frame.messageId == CLIENT_MESSAGE_ACK) {
            long targetClientId = ProtoCodec.decodeAckTargetClientId(frame.payload);
            return ProtoCodec.decodeAckDelivered(frame.payload)
                ? "Message delivered to " + targetClientId
                : "Message not delivered to " + targetClientId;
        }

        if (frame.messageId == CLIENT_MESSAGE_ERROR) {
            return "Message failed: " + ProtoCodec.decodeErrorMessage(frame.payload);
        }

        return "";
    }

    private void send(SocketFrame frame) throws Exception {
        send(output, frame);
    }

    private void send(DataOutputStream targetOutput, SocketFrame frame) throws Exception {
        SocketFrame wireFrame = protector == null ? frame : protector.protect(frame);
        targetOutput.write(wireFrame.encode());
        targetOutput.flush();
    }

    private SocketFrame read() throws Exception {
        return read(input);
    }

    private SocketFrame read(DataInputStream targetInput) throws Exception {
        return protector == null ? SocketFrame.read(targetInput) : protector.read(targetInput);
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
        protector = null;
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

    private static final class ConnectionTarget {
        final String host;
        final int port;

        ConnectionTarget(String host, int port) {
            this.host = host;
            this.port = port;
        }
    }
}
