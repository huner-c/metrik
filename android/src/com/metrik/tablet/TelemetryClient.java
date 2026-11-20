package com.metrik.tablet;

import android.os.SystemClock;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.Closeable;
import java.io.DataInputStream;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

final class TelemetryClient implements AutoCloseable {
    static final int PORT = 47937;
    private static final int USB_PORT = 47938;
    private static final int MAX_PAYLOAD = 4096;
    private static final long INTERVAL_MS = 1000;
    private static final int TIMEOUT_MS = 1700;

    interface Listener {
        void onResult(JSONObject sample);
    }

    private final InetAddress host;
    private final byte[] request;
    private final String pin;
    private final Listener listener;
    private final Thread worker = new Thread(new Runnable() {
        @Override public void run() {
            if (host == null) receiveUsb();
            else receiveLan();
        }
    }, "Metrik telemetry");
    private volatile DatagramSocket socket;
    private volatile ServerSocket usbListener;
    private volatile Socket usbPeer;
    private volatile boolean closed;

    TelemetryClient(InetAddress host, String pin, Listener listener) {
        this.host = host;
        this.pin = pin;
        this.request = ("METRIK/1 " + pin).getBytes(StandardCharsets.US_ASCII);
        this.listener = listener;
    }

    void start() {
        worker.start();
    }

    private void receiveLan() {
        while (!closed) {
            try {
                socket = new DatagramSocket();
                if (closed) return;
                socket.connect(host, PORT);
                socket.setSoTimeout(TIMEOUT_MS);
                poll(socket);
            } catch (SocketException ignored) {
                if (!closed) listener.onResult(null);
            } finally {
                if (socket != null) socket.close();
                socket = null;
            }
            if (!closed) waitForNext(INTERVAL_MS);
        }
    }

    private void poll(DatagramSocket link) {
        DatagramPacket outgoing = new DatagramPacket(request, request.length, host, PORT);
        byte[] buffer = new byte[MAX_PAYLOAD];
        DatagramPacket incoming = new DatagramPacket(buffer, buffer.length);
        while (!closed) {
            long start = SystemClock.elapsedRealtime();
            JSONObject sample = null;
            try {
                link.send(outgoing);
                incoming.setLength(buffer.length);
                link.receive(incoming);
                sample = decode(buffer, incoming.getLength());
            } catch (IOException | JSONException ignored) { }

            if (closed) return;
            listener.onResult(sample);
            long delay = INTERVAL_MS - (SystemClock.elapsedRealtime() - start);
            if (delay > 0) waitForNext(delay);
        }
    }

    private void receiveUsb() {
        while (!closed) {
            try (ServerSocket server = new ServerSocket()) {
                usbListener = server;
                if (closed) return;
                server.setReuseAddress(true);
                InetAddress loopback = InetAddress.getByAddress(new byte[] {127, 0, 0, 1});
                server.bind(new InetSocketAddress(loopback, USB_PORT));
                server.setSoTimeout(TIMEOUT_MS);
                while (!closed) {
                    try (Socket peer = server.accept()) {
                        usbPeer = peer;
                        if (closed) return;
                        peer.setSoTimeout(3500);
                        readUsb(peer);
                    } catch (IOException | GeneralSecurityException | JSONException ignored) {
                        if (!closed) listener.onResult(null);
                    } finally {
                        usbPeer = null;
                    }
                }
            } catch (IOException ignored) {
                if (!closed) listener.onResult(null);
            } finally {
                usbListener = null;
            }
            if (!closed) waitForNext(INTERVAL_MS);
        }
    }

    private void readUsb(Socket peer) throws IOException, GeneralSecurityException, JSONException {
        DataInputStream input = new DataInputStream(peer.getInputStream());
        byte[] challenge = new byte[17];
        new SecureRandom().nextBytes(challenge);
        challenge[16] = 0;
        peer.getOutputStream().write(challenge, 0, 16);

        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(pin.getBytes(StandardCharsets.US_ASCII), "HmacSHA256"));
        byte[] proof = new byte[32];
        input.readFully(proof);
        if (!MessageDigest.isEqual(proof, mac.doFinal(challenge))) throw new IOException("USB pairing failed");
        challenge[16] = 1;
        peer.getOutputStream().write(mac.doFinal(challenge));

        byte[] buffer = new byte[MAX_PAYLOAD];
        while (!closed) {
            int length = input.readInt();
            if (length < 1 || length > buffer.length) throw new IOException("Invalid telemetry packet");
            input.readFully(buffer, 0, length);
            if (!closed) listener.onResult(decode(buffer, length));
        }
    }

    private static JSONObject decode(byte[] buffer, int length) throws JSONException {
        JSONObject decoded = new JSONObject(new String(buffer, 0, length, StandardCharsets.UTF_8));
        return decoded.optInt("version") == 1 && decoded.optJSONObject("gpu") != null ? decoded : null;
    }

    private static void waitForNext(long milliseconds) {
        try {
            Thread.sleep(milliseconds);
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        }
    }

    @Override public void close() {
        closed = true;
        DatagramSocket active = socket;
        if (active != null) active.close();
        closeSocket(usbPeer);
        closeSocket(usbListener);
        worker.interrupt();
    }

    private static void closeSocket(Closeable socket) {
        if (socket == null) return;
        try {
            socket.close();
        } catch (IOException ignored) { }
    }
}
