package com.neodamul.socketsample;

import android.content.Context;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

public final class SampleConfig {
    public int clientId = 1;
    public String clientName = "android-native-client";
    public String host = "10.0.2.2";
    public int port = 10000;
    public boolean useControlServer = true;
    public final List<Endpoint> controlEndpoints = new ArrayList<>();
    public int receiveTimeoutSeconds = 10;
    public boolean allowUntrustedLocalCertificate = true;
    public String transportMode = "Tls";
    public String messageEncryptionSecret = "";

    public boolean useMessageEncryption() {
        return "MessageEncryption".equalsIgnoreCase(transportMode) ||
            "Encrypted".equalsIgnoreCase(transportMode) ||
            "PlainEncrypted".equalsIgnoreCase(transportMode);
    }

    public static SampleConfig load(Context context) {
        SampleConfig config = new SampleConfig();
        try (InputStream stream = context.getResources().openRawResource(R.raw.config)) {
            byte[] bytes = stream.readAllBytes();
            JSONObject json = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
            config.clientId = json.optInt("clientId", config.clientId);
            config.clientName = json.optString("clientName", config.clientName);
            config.host = json.optString("host", config.host);
            config.port = json.optInt("port", config.port);
            config.useControlServer = json.optBoolean("useControlServer", config.useControlServer);
            JSONArray endpoints = json.optJSONArray("controlEndpoints");
            if (endpoints != null) {
                for (int index = 0; index < endpoints.length(); index++) {
                    JSONObject endpoint = endpoints.optJSONObject(index);
                    if (endpoint != null) {
                        int endpointPort = endpoint.optInt("port", config.port);
                        if (endpointPort > 0 && endpointPort <= 65535) {
                            config.controlEndpoints.add(new Endpoint(
                                endpoint.optString("host", config.host),
                                endpointPort));
                        }
                    }
                }
            }
            config.receiveTimeoutSeconds = json.optInt("receiveTimeoutSeconds", config.receiveTimeoutSeconds);
            config.allowUntrustedLocalCertificate = json.optBoolean("allowUntrustedLocalCertificate", config.allowUntrustedLocalCertificate);
            config.transportMode = json.optString("transportMode", config.transportMode);
            config.messageEncryptionSecret = json.optString("messageEncryptionSecret", config.messageEncryptionSecret);
        } catch (Exception ignored) {
        }

        return config;
    }

    public List<Endpoint> effectiveControlEndpoints() {
        if (!controlEndpoints.isEmpty()) {
            return controlEndpoints;
        }

        ArrayList<Endpoint> endpoints = new ArrayList<>();
        endpoints.add(new Endpoint(host, port));
        return endpoints;
    }

    public static List<Endpoint> parseControlEndpoints(String value) {
        ArrayList<Endpoint> endpoints = new ArrayList<>();
        String[] items = value.split("[\\n,]+");
        for (String item : items) {
            String text = item.trim();
            int separator = text.lastIndexOf(':');
            if (separator <= 0 || separator == text.length() - 1) {
                continue;
            }

            try {
                int port = Integer.parseInt(text.substring(separator + 1));
                if (port > 0 && port <= 65535) {
                    endpoints.add(new Endpoint(text.substring(0, separator), port));
                }
            } catch (NumberFormatException ignored) {
            }
        }

        return endpoints;
    }

    public static String formatControlEndpoints(List<Endpoint> endpoints) {
        StringBuilder builder = new StringBuilder();
        for (Endpoint endpoint : endpoints) {
            if (builder.length() > 0) {
                builder.append('\n');
            }

            builder.append(endpoint.host).append(':').append(endpoint.port);
        }

        return builder.toString();
    }

    public static final class Endpoint {
        public final String host;
        public final int port;

        public Endpoint(String host, int port) {
            this.host = host;
            this.port = port;
        }
    }
}
