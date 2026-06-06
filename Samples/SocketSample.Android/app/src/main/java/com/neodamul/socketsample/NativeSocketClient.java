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
        protector = createProtector();
        ConnectionTarget target = resolveConnectionTarget(protector);
        socket = openSocket(target.host, target.port, protector);
        input = new DataInputStream(socket.getInputStream());
        output = new DataOutputStream(socket.getOutputStream());
    }

    public void register() throws Exception {
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

    public ClientEvent receiveEvent() throws Exception {
        socket.setSoTimeout(0);
        SocketFrame frame = read();
        if (frame.messageId == CLIENT_MESSAGE_DELIVER) {
            return ClientEvent.delivery(ProtoCodec.decodeDelivery(frame.payload));
        }

        if (frame.messageId == CLIENT_MESSAGE_ACK) {
            return ClientEvent.ack(ProtoCodec.decodeAck(frame.payload));
        }

        if (frame.messageId == CLIENT_MESSAGE_ERROR) {
            return ClientEvent.error(ProtoCodec.decodeErrorMessage(frame.payload));
        }

        return ClientEvent.ignored();
    }

    private ConnectionTarget resolveConnectionTarget(SocketMessageProtector activeProtector) throws Exception {
        if (!config.useControlServer) {
            return new ConnectionTarget(config.host, config.port);
        }

        Socket controlSocket = openSocket(config.host, config.port, activeProtector);
        try {
            DataInputStream controlInput = new DataInputStream(controlSocket.getInputStream());
            DataOutputStream controlOutput = new DataOutputStream(controlSocket.getOutputStream());
            send(
                controlOutput,
                activeProtector,
                new SocketFrame(config.clientId, ROUTE_REQUEST, ProtoCodec.routeRequest(config.clientId)));
            SocketFrame frame = read(controlInput, activeProtector);
            if (frame.messageId != ROUTE_RESPONSE) {
                throw new IllegalStateException("Invalid route response.");
            }

            ProtoCodec.RouteTarget route = ProtoCodec.decodeRouteResponse(frame.payload);
            if (!route.success || route.host.isEmpty() || route.port <= 0) {
                throw new IllegalStateException(route.errorMessage);
            }

            return new ConnectionTarget(
                ProtoCodec.resolveRouteHost(route.host, config.host),
                route.port);
        } finally {
            controlSocket.close();
        }
    }

    private Socket openSocket(String host, int port, SocketMessageProtector activeProtector) throws Exception {
        if (activeProtector == null) {
            SSLSocketFactory factory = createSocketFactory(config.allowUntrustedLocalCertificate);
            SSLSocket sslSocket = (SSLSocket)factory.createSocket(host, port);
            sslSocket.startHandshake();
            return sslSocket;
        }

        return new Socket(host, port);
    }

    private void send(SocketFrame frame) throws Exception {
        send(output, protector, frame);
    }

    private static void send(DataOutputStream output, SocketMessageProtector protector, SocketFrame frame) throws Exception {
        SocketFrame wireFrame = protector == null ? frame : protector.protect(frame);
        output.write(wireFrame.encode());
        output.flush();
    }

    private SocketFrame read() throws Exception {
        return read(input, protector);
    }

    private static SocketFrame read(DataInputStream input, SocketMessageProtector protector) throws Exception {
        return protector == null ? SocketFrame.read(input) : protector.read(input);
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

    private SocketMessageProtector createProtector() throws Exception {
        return config.useMessageEncryption()
            ? new SocketMessageProtector(config.messageEncryptionSecret)
            : null;
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

    public static final class ClientEvent {
        public static final int TYPE_DELIVERY = 1;
        public static final int TYPE_ACK = 2;
        public static final int TYPE_ERROR = 3;
        public static final int TYPE_IGNORED = 4;

        public final int type;
        public final ProtoCodec.ClientDelivery delivery;
        public final ProtoCodec.ClientAck ack;
        public final String errorMessage;

        private ClientEvent(int type, ProtoCodec.ClientDelivery delivery, ProtoCodec.ClientAck ack, String errorMessage) {
            this.type = type;
            this.delivery = delivery;
            this.ack = ack;
            this.errorMessage = errorMessage;
        }

        static ClientEvent delivery(ProtoCodec.ClientDelivery delivery) {
            return new ClientEvent(TYPE_DELIVERY, delivery, null, "");
        }

        static ClientEvent ack(ProtoCodec.ClientAck ack) {
            return new ClientEvent(TYPE_ACK, null, ack, "");
        }

        static ClientEvent error(String message) {
            return new ClientEvent(TYPE_ERROR, null, null, message);
        }

        static ClientEvent ignored() {
            return new ClientEvent(TYPE_IGNORED, null, null, "");
        }
    }
}
