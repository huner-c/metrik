package com.metrik.tablet;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Bundle;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.ArrayList;
import java.util.Locale;

public final class DashboardActivity extends Activity {
    private static final int INK = 0xff0d1518, PANEL = 0xff162125, LINE = 0xff2d4244;
    private static final int TEXT = 0xffecf7f4, MUTED = 0xffacc4c2, MINT = 0xff82f5c9;
    private static final int SKY = 0xffa3d9ff, AMBER = 0xfffdc371;
    private static final String EMPTY = "—";
    private static final int WRAP = ViewGroup.LayoutParams.WRAP_CONTENT;
    private static final int MATCH = ViewGroup.LayoutParams.MATCH_PARENT;

    private final ArrayList<TextView> readings = new ArrayList<TextView>();
    private SharedPreferences preferences;
    private String host, pin;
    private boolean usb;
    private TelemetryClient client;
    private int generation, missed;
    private float dashboardScale = 1f;
    private TextView connection, endpoint, game, fps, frameTime;
    private TextView gpuName, gpuLoad, gpuTemp, gpuHotspot, gpuClock, gpuPower, gpuFan, vram, vramClock, vramTemp;
    private TextView cpuName, cpuLoad, cpuClock, cpuTemp, cpuPower, cpuThreads, ram;
    private View gpuBar, cpuBar;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        preferences = getSharedPreferences("connection", MODE_PRIVATE);
        host = preferences.getString("host", "");
        pin = preferences.getString("pin", "");
        usb = preferences.getBoolean("usb", false);
        buildDashboard();
        if ((pin.isEmpty() || (!usb && host.isEmpty())) && state == null) editConnection();
    }

    @Override protected void onResume() {
        super.onResume();
        connect();
    }

    @Override protected void onPause() {
        stopConnection();
        super.onPause();
    }

    private void buildDashboard() {
        Configuration configuration = getResources().getConfiguration();
        boolean wide = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE;
        if (wide && configuration.screenHeightDp >= 520)
            dashboardScale = configuration.screenHeightDp >= 650 ? 1.25f : 1.12f;
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(INK);
        LinearLayout root = stack();
        root.setPadding(dp(20), dp(14), dp(20), dp(20));
        scroll.addView(root, new ScrollView.LayoutParams(MATCH, WRAP));
        setContentView(scroll);

        LinearLayout header = row();
        header.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = text("Metrik", 29, TEXT, true);
        header.addView(title, new LinearLayout.LayoutParams(0, WRAP, 1));
        Button settings = new Button(this);
        settings.setText("Connection");
        settings.setTextSize(14 * dashboardScale);
        settings.setTextColor(TEXT);
        settings.setTypeface(Typeface.DEFAULT_BOLD);
        settings.setMinWidth(0);
        settings.setMinimumWidth(0);
        settings.setMinHeight(dp(46));
        settings.setGravity(Gravity.CENTER);
        settings.setPadding(dp(16), 0, dp(16), 0);
        StateListDrawable button = new StateListDrawable();
        button.addState(new int[] {android.R.attr.state_pressed}, background(0xff284441, LINE, 11));
        button.addState(new int[] {android.R.attr.state_focused}, background(0xff284441, MINT, 11));
        button.addState(new int[0], background(PANEL, LINE, 11));
        settings.setBackground(button);
        settings.setFocusable(true);
        settings.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View view) {
                editConnection();
            }
        });
        header.addView(settings, new LinearLayout.LayoutParams(WRAP, WRAP));
        add(root, header, 0);

        connection = text("", 13, MINT, true);
        endpoint = text("", 13, MUTED, false);
        LinearLayout info = row();
        info.addView(connection, new LinearLayout.LayoutParams(0, WRAP, 1));
        info.addView(endpoint, new LinearLayout.LayoutParams(WRAP, WRAP));
        add(root, info, 7);

        LinearLayout frameCard = card();
        LinearLayout frames = row();
        fps = metric(frames, "FPS", MINT, 31);
        frameTime = metric(frames, "Frame time", TEXT, 31);
        frameCard.addView(frames);
        game = text("Waiting for a connection", 13, MUTED, false);
        game.setMaxLines(2);
        game.setEllipsize(TextUtils.TruncateAt.END);
        add(frameCard, game, 7);
        add(root, frameCard, 12);

        LinearLayout gpu = card();
        gpuName = device(gpu, "GPU", MINT);
        gpuLoad = usage(gpu, MINT);
        gpuBar = addTrack(gpu, MINT);
        LinearLayout pair = metricRow(gpu);
        gpuTemp = metric(pair, "GPU temperature", TEXT, 18);
        gpuHotspot = metric(pair, "Hotspot", TEXT, 18);
        pair = metricRow(gpu);
        gpuClock = metric(pair, "GPU clock", TEXT, 18);
        gpuPower = metric(pair, "GPU power", TEXT, 18);
        pair = metricRow(gpu);
        vram = metric(pair, "VRAM usage", TEXT, 18);
        vramClock = metric(pair, "VRAM clock", TEXT, 18);
        pair = metricRow(gpu);
        vramTemp = metric(pair, "VRAM temperature", TEXT, 18);
        gpuFan = metric(pair, "Fan speed", TEXT, 18);

        LinearLayout cpu = card();
        cpuName = device(cpu, "CPU", SKY);
        cpuLoad = usage(cpu, SKY);
        cpuBar = addTrack(cpu, SKY);
        pair = metricRow(cpu);
        cpuTemp = metric(pair, "CPU temperature", TEXT, 18);
        cpuClock = metric(pair, "CPU clock", TEXT, 18);
        pair = metricRow(cpu);
        cpuPower = metric(pair, "Package power", TEXT, 18);
        cpuThreads = metric(pair, "Logical processors", TEXT, 18);
        pair = metricRow(cpu);
        ram = metric(pair, "System memory", TEXT, 18);

        if (wide) {
            LinearLayout hardware = row();
            hardware.addView(gpu, new LinearLayout.LayoutParams(0, MATCH, 1));
            LinearLayout.LayoutParams right = new LinearLayout.LayoutParams(0, MATCH, 1);
            right.leftMargin = dp(12);
            hardware.addView(cpu, right);
            add(root, hardware, 12);
        } else {
            add(root, gpu, 12);
            add(root, cpu, 12);
        }
        applySample(null);
    }

    private void editConnection() {
        LinearLayout form = stack();
        form.setPadding(dp(20), dp(4), dp(20), dp(4));
        RadioGroup transport = new RadioGroup(this);
        transport.setOrientation(LinearLayout.HORIZONTAL);
        RadioButton wifiMode = new RadioButton(this);
        wifiMode.setId(View.generateViewId());
        wifiMode.setText("Local network");
        transport.addView(wifiMode);
        RadioButton usbMode = new RadioButton(this);
        final int usbId = View.generateViewId();
        usbMode.setId(usbId);
        usbMode.setText("USB cable");
        transport.addView(usbMode);
        add(form, transport, 0);
        final TextView instructions = text("", 15, TEXT, false);
        add(form, instructions, 8);
        final EditText address = new EditText(this);
        address.setHint("PC IP address");
        address.setContentDescription("PC IP address");
        address.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        address.setFilters(new InputFilter[] {new InputFilter.LengthFilter(15)});
        address.setSingleLine();
        address.setText(host);
        add(form, address, 12);
        final EditText code = new EditText(this);
        code.setHint("6-digit pairing code");
        code.setContentDescription("6-digit pairing code");
        code.setInputType(InputType.TYPE_CLASS_NUMBER);
        code.setFilters(new InputFilter[] {new InputFilter.LengthFilter(6)});
        code.setSingleLine();
        code.setText(pin);
        add(form, code, 8);

        transport.setOnCheckedChangeListener(new RadioGroup.OnCheckedChangeListener() {
            @Override public void onCheckedChanged(RadioGroup group, int checkedId) {
                boolean useUsb = checkedId == usbId;
                address.setVisibility(useUsb ? View.GONE : View.VISIBLE);
                update(instructions, useUsb ? "Connect the tablet over USB. Select Start USB stream on Windows."
                    : "Select Start LAN stream on Windows. Keep the PC and tablet on the same network.");
            }
        });
        transport.check(usb ? usbId : wifiMode.getId());
        final AlertDialog dialog = new AlertDialog.Builder(this).setTitle("Connect to a PC")
            .setView(form).setNegativeButton("Cancel", null).setPositiveButton("Connect", null).create();
        dialog.show();
        dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View view) {
                String nextHost = address.getText().toString().trim();
                String nextPin = code.getText().toString().trim();
                boolean nextUsb = transport.getCheckedRadioButtonId() == usbId;
                if (!nextUsb && localAddress(nextHost) == null) {
                    address.setError("Enter the local IPv4 address shown in Metrik on Windows.");
                    return;
                }
                if (!nextPin.matches("[0-9]{6}")) {
                    code.setError("Enter the 6-digit code shown in Metrik on Windows.");
                    return;
                }
                if (!nextUsb) host = nextHost;
                usb = nextUsb;
                pin = nextPin;
                preferences.edit().putString("host", host).putString("pin", pin).putBoolean("usb", usb).apply();
                dialog.dismiss();
                connect();
            }
        });
    }

    private static InetAddress localAddress(String host) {
        if (!host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}")) return null;
        String[] parts = host.split("\\.");
        byte[] bytes = new byte[4];
        for (int i = 0; i < bytes.length; i++) {
            int part = Integer.parseInt(parts[i]);
            if (part > 255) return null;
            bytes[i] = (byte)part;
        }
        int a = bytes[0] & 255, b = bytes[1] & 255;
        if (!(a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168))) return null;
        try {
            return InetAddress.getByAddress(bytes);
        } catch (UnknownHostException ignored) {
            return null;
        }
    }

    private void connect() {
        stopConnection();
        InetAddress address = usb ? null : localAddress(host);
        update(endpoint, usb ? "USB cable" : address == null ? "" : host);
        applySample(null);
        if ((!usb && address == null) || !pin.matches("[0-9]{6}")) {
            setState("Disconnected", AMBER);
            update(game, "Connect to a PC to get started");
            return;
        }
        setState("Connecting…", MUTED);
        final int current = generation;
        client = new TelemetryClient(address, pin, new TelemetryClient.Listener() {
            @Override public void onResult(final JSONObject sample) {
                runOnUiThread(new Runnable() {
                    @Override public void run() {
                        if (current == generation) onResultOnMain(sample);
                    }
                });
            }
        });
        client.start();
    }

    private void stopConnection() {
        generation++;
        missed = 0;
        if (client != null) client.close();
        client = null;
    }

    private void onResultOnMain(JSONObject sample) {
        if (sample != null) {
            missed = 0;
            setState("Live", MINT);
            applySample(sample);
        } else if (++missed == 1) {
            applySample(null);
            setState("Waiting for a response…", AMBER);
        } else {
            missed = 2;
            setState(usb ? "Waiting for a USB connection" : "Cannot reach the PC", AMBER);
            update(game, usb ? "Check the USB cable, the Windows stream, and the pairing code."
                : "Check the network, PC IP address, and pairing code.");
        }
    }

    private void setState(String value, int color) {
        update(connection, value);
        connection.setTextColor(color);
    }

    private void applySample(JSONObject sample) {
        if (sample == null) {
            for (TextView reading : readings) update(reading, EMPTY);
            update(gpuName, EMPTY);
            update(cpuName, EMPTY);
            setBar(gpuBar, 0);
            setBar(cpuBar, 0);
            update(game, "Waiting for metrics from the PC");
            return;
        }
        JSONObject frames = sample.optJSONObject("frame");
        update(fps, measure(frames, "fps", 0, ""));
        update(frameTime, measure(frames, "frameMilliseconds", 1, " ms"));
        String status = string(sample, "frameStatus", "Waiting for frame data");
        String title = string(sample, "game", "");
        String runtime = string(sample, "runtime", "");
        if (!title.isEmpty()) status = title + (runtime.isEmpty() ? "" : "  ·  " + runtime) + "  ·  " + status;
        update(game, status);

        JSONObject gpu = sample.optJSONObject("gpu");
        update(gpuName, string(gpu, "name", EMPTY));
        update(gpuLoad, percentage(gpu));
        update(gpuTemp, measure(gpu, "temperature", 0, " °C"));
        update(gpuHotspot, measure(gpu, "hotspot", 0, " °C"));
        update(gpuClock, measure(gpu, "clock", 0, " MHz"));
        String boardPower = measure(gpu, "boardPower", 0, " W · Board");
        update(gpuPower, boardPower.equals(EMPTY) ? measure(gpu, "asicPower", 0, " W · ASIC") : boardPower);
        update(vram, memory(gpu, "usedGiB", "totalGiB"));
        update(vramClock, measure(gpu, "memoryClock", 0, " MHz"));
        update(vramTemp, measure(gpu, "memoryTemperature", 0, " °C"));
        update(gpuFan, measure(gpu, "fanRpm", 0, " RPM"));
        setBar(gpuBar, number(gpu, "load"));

        JSONObject cpu = sample.optJSONObject("cpu");
        update(cpuName, string(cpu, "name", EMPTY));
        update(cpuLoad, percentage(cpu));
        update(cpuClock, measure(cpu, "clock", 0, " MHz"));
        update(cpuTemp, measure(cpu, "temperature", 0, " °C"));
        update(cpuPower, measure(cpu, "packagePower", 1, " W"));
        update(cpuThreads, measure(cpu, "logicalProcessors", 0, ""));
        update(ram, memory(cpu, "memoryUsedGiB", "memoryTotalGiB"));
        setBar(cpuBar, number(cpu, "load"));
    }

    private static String string(JSONObject values, String key, String fallback) {
        return values == null || values.isNull(key) ? fallback : values.optString(key, fallback);
    }

    private static double number(JSONObject values, String key) {
        if (values == null) return Double.NaN;
        double value = values.optDouble(key, Double.NaN);
        return Double.isInfinite(value) ? Double.NaN : value;
    }

    private static String percentage(JSONObject values) {
        String value = measure(values, "load", 0, "");
        return value.equals(EMPTY) ? EMPTY : value + "%";
    }

    private static String measure(JSONObject values, String key, int decimalPlaces, String unit) {
        double value = number(values, key);
        return Double.isNaN(value) ? EMPTY : String.format(Locale.US, "%." + decimalPlaces + "f", value) + unit;
    }

    private static String memory(JSONObject values, String used, String total) {
        String usage = measure(values, used, 1, "");
        String capacity = measure(values, total, 1, "");
        if (usage.equals(EMPTY) && capacity.equals(EMPTY)) return EMPTY;
        return usage + " / " + capacity + " GiB";
    }

    private static void update(TextView view, String content) {
        if (!TextUtils.equals(view.getText(), content)) view.setText(content);
    }

    private TextView device(LinearLayout panel, String label, int accent) {
        TextView kind = text(label, 12, accent, true);
        add(panel, kind, 0);
        TextView name = text(EMPTY, 19, TEXT, true);
        name.setMaxLines(2);
        name.setEllipsize(TextUtils.TruncateAt.END);
        add(panel, name, 3);
        return name;
    }

    private TextView usage(LinearLayout panel, int accent) {
        LinearLayout section = row();
        section.setGravity(Gravity.CENTER_VERTICAL);
        section.addView(text("Usage", 13, MUTED, false), new LinearLayout.LayoutParams(0, WRAP, 1));
        TextView value = text(EMPTY, 24, accent, true);
        readings.add(value);
        section.addView(value, new LinearLayout.LayoutParams(WRAP, WRAP));
        add(panel, section, 6);
        return value;
    }

    private View addTrack(LinearLayout panel, int accent) {
        android.widget.ProgressBar track = new android.widget.ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        android.graphics.drawable.ClipDrawable fill = new android.graphics.drawable.ClipDrawable(
            background(accent, Color.TRANSPARENT, 4), Gravity.LEFT, android.graphics.drawable.ClipDrawable.HORIZONTAL);
        android.graphics.drawable.LayerDrawable layers = new android.graphics.drawable.LayerDrawable(
            new android.graphics.drawable.Drawable[] {background(LINE, Color.TRANSPARENT, 4), fill});
        layers.setId(0, android.R.id.background);
        layers.setId(1, android.R.id.progress);
        track.setProgressDrawable(layers);
        track.setMax(100);
        track.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        panel.addView(track, new LinearLayout.LayoutParams(MATCH, dp(5)));
        return track;
    }

    private static void setBar(View view, double value) {
        ((android.widget.ProgressBar)view).setProgress((int)Math.round(Math.max(0, Math.min(100, value))));
    }

    private LinearLayout metricRow(LinearLayout parent) {
        LinearLayout pair = row();
        add(parent, pair, Math.round(8 * dashboardScale));
        return pair;
    }

    private TextView metric(LinearLayout row, String label, int color, int size) {
        LinearLayout tile = stack();
        tile.setPadding(0, 0, dp(4), 0);
        tile.addView(text(label, 13, MUTED, false));
        TextView reading = text(EMPTY, size, color, true);
        tile.addView(reading);
        readings.add(reading);
        row.addView(tile, new LinearLayout.LayoutParams(0, WRAP, 1));
        return reading;
    }

    private LinearLayout card() {
        LinearLayout layout = stack();
        layout.setBackground(background(PANEL, LINE, 13));
        int side = dp(Math.round(13 * dashboardScale));
        int vertical = dp(Math.round(10 * dashboardScale));
        layout.setPadding(side, vertical, side, vertical);
        return layout;
    }

    private TextView text(String value, int size, int color, boolean bold) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size * dashboardScale);
        view.setTextColor(color);
        view.setTypeface(Typeface.create("sans-serif", bold ? Typeface.BOLD : Typeface.NORMAL));
        view.setIncludeFontPadding(false);
        return view;
    }

    private GradientDrawable background(int color, int stroke, int radius) {
        GradientDrawable background = new GradientDrawable();
        background.setColor(color);
        background.setCornerRadius(dp(radius));
        if (stroke != Color.TRANSPARENT) background.setStroke(dp(1), stroke);
        return background;
    }

    private LinearLayout stack() {
        LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.VERTICAL);
        return view;
    }

    private LinearLayout row() {
        return new LinearLayout(this);
    }

    private void add(LinearLayout parent, View child, int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(MATCH, WRAP);
        params.topMargin = dp(top);
        parent.addView(child, params);
    }

    private int dp(int value) {
        return Math.round(getResources().getDisplayMetrics().density * value);
    }
}
