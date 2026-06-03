#!/usr/bin/env sh
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SRC_DIR="$ROOT_DIR/app/src/main/java/com/neodamul/socketsample"
BUILD_DIR="$ROOT_DIR/build/validation"
CLASS_DIR="$BUILD_DIR/classes"
TEST_SRC="$BUILD_DIR/AndroidProtocolValidation.java"
SDK_ROOT="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"

if [ -z "$SDK_ROOT" ] && [ -d "/opt/homebrew/share/android-commandlinetools/platforms" ]; then
    SDK_ROOT="/opt/homebrew/share/android-commandlinetools"
fi

if [ -n "$SDK_ROOT" ]; then
    export ANDROID_HOME="$SDK_ROOT"
fi

mkdir -p "$CLASS_DIR"

javac -d "$CLASS_DIR" "$SRC_DIR/SocketFrame.java" "$SRC_DIR/ProtoCodec.java" "$SRC_DIR/SocketMessageProtector.java"

cat > "$TEST_SRC" <<'JAVA'
import com.neodamul.socketsample.ProtoCodec;
import com.neodamul.socketsample.SocketFrame;
import com.neodamul.socketsample.SocketMessageProtector;
import java.io.ByteArrayInputStream;
import java.io.DataInputStream;
import java.nio.charset.StandardCharsets;

public final class AndroidProtocolValidation {
    public static void main(String[] args) throws Exception {
        byte[] payload = ProtoCodec.clientMessageSend(101, 202, "hello-android");
        ProtoCodec.ClientDelivery delivery = ProtoCodec.decodeDelivery(payload);
        if (delivery.sourceClientId != 101 || delivery.targetClientId != 202 || !"hello-android".equals(delivery.content)) {
            throw new IllegalStateException("Client message protobuf payload did not round-trip.");
        }

        SocketFrame frame = new SocketFrame(101, 2002, "payload".getBytes(StandardCharsets.UTF_8));
        SocketFrame decoded = SocketFrame.read(new DataInputStream(new ByteArrayInputStream(frame.encode())));
        if (decoded.clientId != 101 || decoded.messageId != 2002 || !"payload".equals(new String(decoded.payload, StandardCharsets.UTF_8))) {
            throw new IllegalStateException("Socket frame did not round-trip.");
        }

        SocketMessageProtector protector = new SocketMessageProtector("android-validation-secret");
        SocketFrame protectedFrame = protector.protect(frame);
        SocketFrame decrypted = protector.read(new DataInputStream(new ByteArrayInputStream(protectedFrame.encode())));
        if (decrypted.clientId != 101 || decrypted.messageId != 2002 || !"payload".equals(new String(decrypted.payload, StandardCharsets.UTF_8))) {
            throw new IllegalStateException("Protected socket frame did not round-trip.");
        }
    }
}
JAVA

javac -cp "$CLASS_DIR" -d "$CLASS_DIR" "$TEST_SRC"
java -cp "$CLASS_DIR" AndroidProtocolValidation

if [ "${1:-}" = "--protocol-only" ]; then
    exit 0
fi

if [ "${1:-}" = "--apk" ]; then
    if [ -z "$SDK_ROOT" ]; then
        echo "ANDROID_HOME or ANDROID_SDK_ROOT is required for APK build validation." >&2
        exit 1
    fi

    "$ROOT_DIR/gradlew" -p "$ROOT_DIR" :app:assembleDebug
elif [ -n "$SDK_ROOT" ]; then
    "$ROOT_DIR/gradlew" -p "$ROOT_DIR" :app:assembleDebug
else
    echo "Android SDK was not detected; protocol validation passed. Run ./validate.sh --apk after installing Android SDK to build the APK."
fi
