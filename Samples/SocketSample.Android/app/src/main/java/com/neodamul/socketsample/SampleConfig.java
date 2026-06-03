package com.neodamul.socketsample;

import android.content.Context;
import org.json.JSONObject;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

public final class SampleConfig {
    public int clientId = 1;
    public String clientName = "android-native-client";
    public String host = "10.0.2.2";
    public int port = 5000;
    public int receiveTimeoutSeconds = 10;
    public boolean allowUntrustedLocalCertificate = true;

    public static SampleConfig load(Context context) {
        SampleConfig config = new SampleConfig();
        try (InputStream stream = context.getResources().openRawResource(R.raw.config)) {
            byte[] bytes = stream.readAllBytes();
            JSONObject json = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
            config.clientId = json.optInt("clientId", config.clientId);
            config.clientName = json.optString("clientName", config.clientName);
            config.host = json.optString("host", config.host);
            config.port = json.optInt("port", config.port);
            config.receiveTimeoutSeconds = json.optInt("receiveTimeoutSeconds", config.receiveTimeoutSeconds);
            config.allowUntrustedLocalCertificate = json.optBoolean("allowUntrustedLocalCertificate", config.allowUntrustedLocalCertificate);
        } catch (Exception ignored) {
        }

        return config;
    }
}
