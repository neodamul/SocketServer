package com.neodamul.socketsample;

import android.app.Activity;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private final ExecutorService commandExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService receiveExecutor = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private SampleConfig config;
    private NativeSocketClient client;
    private EditText clientId;
    private EditText host;
    private EditText port;
    private EditText transportMode;
    private EditText messageSecret;
    private EditText targetClientId;
    private EditText message;
    private CheckBox useControlServer;
    private CheckBox allowUntrusted;
    private TextView status;
    private volatile boolean registered;
    private volatile boolean receiveLoopRunning;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        config = SampleConfig.load(this);
        client = new NativeSocketClient(config);
        setContentView(createContentView());
        showStatus("Disconnected");
    }

    private ScrollView createContentView() {
        ScrollView scrollView = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(32, 32, 32, 32);
        scrollView.addView(root);

        clientId = input(String.valueOf(config.clientId));
        host = input(config.host);
        port = input(String.valueOf(config.port));
        transportMode = input(config.transportMode);
        messageSecret = input(config.messageEncryptionSecret);
        targetClientId = input("2");
        message = input("hello");
        useControlServer = new CheckBox(this);
        useControlServer.setText("Use ControlServer route");
        useControlServer.setChecked(config.useControlServer);
        allowUntrusted = new CheckBox(this);
        allowUntrusted.setText("Allow local self-signed certificate");
        allowUntrusted.setChecked(config.allowUntrustedLocalCertificate);
        status = new TextView(this);

        root.addView(label("Client ID"));
        root.addView(clientId);
        root.addView(label("Host"));
        root.addView(host);
        root.addView(label("Port"));
        root.addView(port);
        root.addView(useControlServer);
        root.addView(allowUntrusted);
        root.addView(label("Transport"));
        root.addView(transportMode);
        root.addView(label("Message Secret"));
        root.addView(messageSecret);
        root.addView(button("Save", () -> {
            readConfig();
            client.update(config);
            showStatus("Configured");
        }));
        root.addView(button("Connect", () -> run("Connected and registered", () -> {
            stopReceiveLoop();
            client.connect();
            registered = true;
            startReceiveLoop();
        })));
        root.addView(label("Target Client ID"));
        root.addView(targetClientId);
        root.addView(label("Message"));
        root.addView(message);
        root.addView(button("Send", () -> run("Message sent", () ->
            client.sendMessage(Long.parseLong(targetClientId.getText().toString()), message.getText().toString()))));
        root.addView(button("Disconnect", () -> {
            stopReceiveLoop();
            client.close();
            registered = false;
            showStatus("Disconnected");
        }));
        root.addView(status);
        return scrollView;
    }

    private TextView label(String text) {
        TextView label = new TextView(this);
        label.setText(text);
        label.setTextSize(13);
        return label;
    }

    private EditText input(String text) {
        EditText input = new EditText(this);
        input.setText(text);
        input.setSingleLine(true);
        input.setLayoutParams(new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));
        return input;
    }

    private Button button(String text, Runnable action) {
        Button button = new Button(this);
        button.setText(text);
        button.setOnClickListener(view -> action.run());
        return button;
    }

    private void readConfig() {
        config.clientId = Integer.parseInt(clientId.getText().toString());
        config.host = host.getText().toString();
        config.port = Integer.parseInt(port.getText().toString());
        config.useControlServer = useControlServer.isChecked();
        config.allowUntrustedLocalCertificate = allowUntrusted.isChecked();
        config.transportMode = transportMode.getText().toString();
        config.messageEncryptionSecret = messageSecret.getText().toString();
    }

    private void run(String successStatus, ThrowingRunnable action) {
        readConfig();
        client.update(config);
        commandExecutor.execute(() -> {
            try {
                action.run();
                mainHandler.post(() -> showStatus(successStatus));
            } catch (Exception exception) {
                mainHandler.post(() -> showStatus("Failed: " + exception.getMessage()));
            }
        });
    }

    private void startReceiveLoop() {
        receiveLoopRunning = true;
        receiveExecutor.execute(() -> {
            while (receiveLoopRunning && client.isConnected()) {
                try {
                    String line = client.receiveEvent();
                    if (!line.isEmpty()) {
                        mainHandler.post(() -> showStatus(line));
                    }
                } catch (Exception exception) {
                    if (receiveLoopRunning) {
                        mainHandler.post(() -> showStatus("Receive loop stopped: " + exception.getMessage()));
                    }

                    receiveLoopRunning = false;
                }
            }
        });
    }

    private void stopReceiveLoop() {
        receiveLoopRunning = false;
    }

    private void showStatus(String line) {
        status.setText(
            "Status: " + line + "\n" +
            "Connected: " + client.isConnected() + "\n" +
            "Registered: " + registered + "\n" +
            "Client ID: " + config.clientId + "\n" +
            "Endpoint: " + config.host + ":" + config.port + "\n" +
            "Use ControlServer: " + config.useControlServer + "\n" +
            "Transport: " + config.transportMode);
    }

    @Override
    protected void onDestroy() {
        stopReceiveLoop();
        client.close();
        commandExecutor.shutdownNow();
        receiveExecutor.shutdownNow();
        super.onDestroy();
    }

    private interface ThrowingRunnable {
        void run() throws Exception;
    }
}
