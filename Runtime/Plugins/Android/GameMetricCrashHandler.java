package dev.gamemetric.sdk;

import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.nio.charset.Charset;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

/**
 * Catches uncaught Java exceptions on any Java/Kotlin thread (including ones from
 * third-party Android SDKs / JNI that Unity's managed logMessageReceived hook
 * never sees) and writes a crash record matching the SDK's on-disk contract:
 * one JSON file per crash under
 *   {persistentDataPath}/gamemetric/native-crashes/android-{millis}.json
 * The managed SDK picks it up, emits it, and deletes it on the next launch.
 *
 * This runs during an uncaught exception, where the JVM is still functional, so
 * ordinary file I/O is safe here (unlike a native signal handler). We always chain
 * to the previously-installed handler (Unity's), so normal crash handling and
 * process termination still happen.
 *
 * NOTE: This covers the Java layer only. Native NDK signals (SIGSEGV/SIGABRT in
 * il2cpp / C++ plugins) require a separate signal-based handler (Android Phase 2).
 */
public final class GameMetricCrashHandler implements Thread.UncaughtExceptionHandler {

    private final String crashDir;
    private final String buildId;
    private final Thread.UncaughtExceptionHandler previous;

    private GameMetricCrashHandler(String crashDir, String buildId, Thread.UncaughtExceptionHandler previous) {
        this.crashDir = crashDir;
        this.buildId = buildId;
        this.previous = previous;
    }

    /** Installs the handler once. Called from C# at Initialize. Idempotent. */
    public static synchronized void install(String crashDir, String buildId) {
        Thread.UncaughtExceptionHandler current = Thread.getDefaultUncaughtExceptionHandler();
        if (current instanceof GameMetricCrashHandler) {
            return; // already installed — don't wrap ourselves twice
        }
        Thread.setDefaultUncaughtExceptionHandler(new GameMetricCrashHandler(crashDir, buildId, current));
    }

    @Override
    public void uncaughtException(Thread thread, Throwable ex) {
        // Never let our own failure mask the original crash.
        try {
            writeRecord(thread, ex);
        } catch (Throwable ignored) {
        }

        // Chain so Unity's handler still runs and the process terminates as usual.
        if (previous != null) {
            previous.uncaughtException(thread, ex);
        } else {
            android.os.Process.killProcess(android.os.Process.myPid());
            System.exit(10);
        }
    }

    private void writeRecord(Thread thread, Throwable ex) throws Exception {
        File dir = new File(crashDir);
        if (!dir.exists()) {
            dir.mkdirs();
        }

        String type = ex.getClass().getName();
        String message = ex.getMessage() != null ? ex.getMessage() : type;

        StringBuilder stack = new StringBuilder();
        appendFrames(stack, ex);
        Throwable cause = ex.getCause();
        int guard = 0;
        while (cause != null && cause != ex && guard++ < 8) {
            stack.append("\nCaused by: ").append(cause.getClass().getName());
            if (cause.getMessage() != null) {
                stack.append(": ").append(cause.getMessage());
            }
            appendFrames(stack, cause);
            cause = cause.getCause();
        }

        SimpleDateFormat iso = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        iso.setTimeZone(TimeZone.getTimeZone("UTC"));

        StringBuilder json = new StringBuilder(512);
        json.append('{')
            .append("\"schema\":1,")
            .append("\"platform\":\"android\",")
            .append("\"type\":").append(quote(type)).append(',')
            .append("\"message\":").append(quote("[" + thread.getName() + "] " + message)).append(',')
            .append("\"stack\":").append(quote(stack.toString())).append(',')
            .append("\"build_id\":").append(quote(buildId)).append(',')
            .append("\"timestamp\":").append(quote(iso.format(new Date())))
            .append('}');

        // Write to a .tmp then rename, so the managed reader (which scans *.json)
        // never sees a half-written file.
        String name = "android-" + System.currentTimeMillis();
        File tmp = new File(dir, name + ".tmp");
        FileOutputStream fos = new FileOutputStream(tmp);
        try {
            OutputStreamWriter writer = new OutputStreamWriter(fos, Charset.forName("UTF-8"));
            writer.write(json.toString());
            writer.flush();
            fos.getFD().sync();
            writer.close();
        } finally {
            try { fos.close(); } catch (Throwable ignored) { }
        }
        tmp.renameTo(new File(dir, name + ".json"));
    }

    private static void appendFrames(StringBuilder sb, Throwable t) {
        StackTraceElement[] frames = t.getStackTrace();
        for (int i = 0; i < frames.length; i++) {
            sb.append("\n\tat ").append(frames[i].toString());
        }
    }

    private static String quote(String s) {
        if (s == null) {
            return "null";
        }
        StringBuilder b = new StringBuilder(s.length() + 2);
        b.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20) {
                        b.append(String.format("\\u%04x", (int) c));
                    } else {
                        b.append(c);
                    }
            }
        }
        b.append('"');
        return b.toString();
    }
}
