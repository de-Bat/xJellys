package com.xjellys.tvoverlay;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.DialogInterface;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputType;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import com.xjellys.tvoverlay.model.ServerUrl;

import java.util.ArrayList;
import java.util.List;

/** Sign-in and settings. The overlay service arrives in a later task. */
public class MainActivity extends Activity {
    private final Handler ui = new Handler(Looper.getMainLooper());
    private Prefs prefs;
    private TextView status, code;
    private LinearLayout signInBox, signedInBox;
    private EditText server, user, password;
    private Button overlayBtn, watchBtn, bootBtn, quickBtn;
    private volatile String quickSecret;

    private final Runnable refresher = new Runnable() {
        @Override public void run() {
            refresh();
            ui.postDelayed(this, 1000);
        }
    };

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = new Prefs(this);
        int pad = dp(40);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(pad, dp(28), pad, pad);

        TextView title = text("JellyTvOverlay", 30);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        root.addView(title);
        root.addView(text("On-screen overlays for Jellyfin. Version " + BuildInfo.VERSION, 15));

        status = text("", 17);
        status.setPadding(0, dp(16), 0, dp(8));
        root.addView(status);

        code = text("", 22);
        code.setTextColor(Color.rgb(255, 214, 102));
        code.setVisibility(View.GONE);
        root.addView(code);

        // signed out
        signInBox = column();
        server = field("Server address, e.g. 192.168.1.10:8096", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        server.setText(prefs.server());
        signInBox.addView(server);
        LinearLayout r1 = row();
        r1.addView(button("Find server", new View.OnClickListener() {
            @Override public void onClick(View v) { findServer(); }
        }));
        quickBtn = button("Sign in with Quick Connect", new View.OnClickListener() {
            @Override public void onClick(View v) { quickConnect(); }
        });
        r1.addView(quickBtn);
        signInBox.addView(r1);
        signInBox.addView(text("Or with a user name and password:", 15));
        user = field("User name", InputType.TYPE_CLASS_TEXT);
        password = field("Password", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        password.setImeOptions(EditorInfo.IME_ACTION_DONE);
        signInBox.addView(user);
        signInBox.addView(password);
        signInBox.addView(button("Sign in", new View.OnClickListener() {
            @Override public void onClick(View v) { signIn(); }
        }));
        root.addView(signInBox);

        // signed in
        signedInBox = column();
        LinearLayout r2 = row();
        overlayBtn = button("Allow overlay", new View.OnClickListener() {
            @Override public void onClick(View v) { openOverlaySettings(); }
        });
        r2.addView(overlayBtn);
        r2.addView(button("Test panel", new View.OnClickListener() {
            @Override public void onClick(View v) { testPanel(); }
        }));
        signedInBox.addView(r2);
        LinearLayout r3 = row();
        watchBtn = button("Watch: automatic", new View.OnClickListener() {
            @Override public void onClick(View v) { pickSession(); }
        });
        bootBtn = button("Start on boot: on", new View.OnClickListener() {
            @Override public void onClick(View v) {
                prefs.setStartOnBoot(!prefs.startOnBoot());
                refresh();
            }
        });
        r3.addView(watchBtn);
        r3.addView(bootBtn);
        r3.addView(button("Sign out", new View.OnClickListener() {
            @Override public void onClick(View v) { signOut(); }
        }));
        signedInBox.addView(r3);
        TextView help = text(
"How it works: leave this app and play something in Jellyfin. Overlays from the JellyTvOverlay plugin on"
                + " your server show over the player. The overlay renderer arrives in a later version.\n"
                + "Needs the JellyTvOverlay plugin on the server.", 15);
        help.setPadding(0, dp(20), 0, 0);
        signedInBox.addView(help);
        root.addView(signedInBox);

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(Color.rgb(24, 28, 36));
        scroll.addView(root);
        setContentView(scroll);

        refresh();
        (prefs.signedIn() ? watchBtn : (prefs.server().isEmpty() ? server : quickBtn)).requestFocus();
        if (!prefs.signedIn() && prefs.server().isEmpty()) findServer();
    }

    @Override
    protected void onResume() {
        super.onResume();
        ui.post(refresher);
    }

    @Override
    protected void onPause() {
        super.onPause();
        ui.removeCallbacks(refresher);
    }

    @Override
    protected void onDestroy() {
        quickSecret = null;
        super.onDestroy();
    }

    private void refresh() {
        boolean in = prefs.signedIn();
        boolean overlay = Settings.canDrawOverlays(this);
        signInBox.setVisibility(in ? View.GONE : View.VISIBLE);
        signedInBox.setVisibility(in ? View.VISIBLE : View.GONE);
        if (in) code.setVisibility(View.GONE);
        StringBuilder b = new StringBuilder();
        if (in) {
            b.append("Server: ").append(prefs.serverName()).append("  (").append(prefs.userName()).append(")\n");
            b.append(prefs.server().toLowerCase(java.util.Locale.ROOT).startsWith("https://")
                ? prefs.server() : "Not secured (local network)").append('\n');
            b.append("Overlay permission: ").append(overlay ? "allowed" : "NOT ALLOWED - press Allow overlay").append('\n');
            if (!overlay) {
                b.append("  If this box has no such setting, run on a PC:\n  adb shell appops set ")
                    .append(getPackageName()).append(" SYSTEM_ALERT_WINDOW allow\n");
            }
            b.append("Overlay: the renderer arrives in a later version");
        } else {
            b.append("Sign in to your Jellyfin server (same user as the Jellyfin app on this box).");
        }
        status.setText(b.toString());
        status.setTextColor(in && overlay ? Color.rgb(170, 235, 185) : Color.rgb(255, 190, 140));
        overlayBtn.setVisibility(overlay ? View.GONE : View.VISIBLE);
        watchBtn.setText("Watch: " + (prefs.watch().isEmpty() ? "automatic" : prefs.watchLabel()));
        bootBtn.setText("Start on boot: " + (prefs.startOnBoot() ? "on" : "off"));
    }

    // ---- sign in ----

    /**
     * Talks to the typed address (https first, port 8920 for a bare host). If that fails to connect and the host is
     * on the local network, retries plain http on 8096. Blocks: run off the main thread. Saves the address that worked.
     */
    private Api connect(String typed) throws Exception {
        String base = ServerUrl.normalize(typed);
        Api api = new Api(base, prefs.deviceName(), prefs.deviceId(), null);
        String name;
        try {
            name = api.serverName();
        } catch (Api.HttpError e) {
            throw e;
        } catch (Exception e) {
            String fb = ServerUrl.httpFallback(base);
            if (fb == null) throw e;
            api = new Api(fb, prefs.deviceName(), prefs.deviceId(), null);
            name = api.serverName();
        }
        prefs.setServer(api.base());
        prefs.setServerName(name);
        final String shown = api.base();
        ui.post(new Runnable() {
            @Override public void run() { server.setText(shown); }
        });
        return api;
    }

    private void findServer() {
        say("Looking for Jellyfin servers on the network...");
        new Thread(new Runnable() {
            @Override public void run() {
                final String found = Api.discover();
                ui.post(new Runnable() {
                    @Override public void run() {
                        if (found == null) {
                            say("No server answered. Type its address (e.g. 192.168.1.10:8096).");
                            server.requestFocus();
                        } else {
                            server.setText(found);
                            say("Found " + found);
                            quickBtn.requestFocus();
                        }
                    }
                });
            }
        }).start();
    }

    private void quickConnect() {
        final String typed = server.getText().toString();
        if (ServerUrl.normalize(typed).isEmpty()) {
            say("Enter the server address first.");
            return;
        }
        say("Contacting " + ServerUrl.normalize(typed) + "...");
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    Api api = connect(typed);
                    String name = prefs.serverName();
                    if (!api.quickConnectEnabled()) {
                        sayLater("Quick Connect is off on " + name + " (Dashboard > General > Quick Connect). Use user name and password.");
                        return;
                    }
                    String[] sc = api.quickConnectStart();
                    final String secret = sc[0];
                    quickSecret = secret;
                    final String c = sc[1];
                    ui.post(new Runnable() {
                        @Override public void run() {
                            code.setText("Quick Connect code: " + c + "\nOn your phone or computer: Jellyfin > your profile > Quick Connect, enter the code.");
                            code.setVisibility(View.VISIBLE);
                            say("Waiting for approval...");
                        }
                    });
                    long until = System.currentTimeMillis() + 5 * 60 * 1000;
                    while (secret.equals(quickSecret) && System.currentTimeMillis() < until) {
                        Thread.sleep(2000);
                        if (api.quickConnectApproved(secret)) {
                            String[] s = api.quickConnectFinish(secret);
                            signedIn(s);
                            return;
                        }
                    }
                    if (secret.equals(quickSecret)) sayLater("The code expired. Press Quick Connect again.");
                } catch (Exception e) {
                    sayLater("Could not reach the server: " + e.getMessage());
                }
            }
        }).start();
    }

    private void signIn() {
        final String typed = server.getText().toString();
        final String u = user.getText().toString().trim();
        final String pw = password.getText().toString();
        if (ServerUrl.normalize(typed).isEmpty() || u.isEmpty()) {
            say("Enter the server address and user name.");
            return;
        }
        say("Signing in...");
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    signedIn(connect(typed).signIn(u, pw));
                } catch (Api.HttpError e) {
                    sayLater(e.code == 401 ? "Wrong user name or password." : "Sign-in failed: " + e.getMessage());
                } catch (Exception e) {
                    sayLater("Could not reach the server: " + e.getMessage());
                }
            }
        }).start();
    }

    /** {token, userId, userName} from the server. */
    private void signedIn(final String[] s) {
        quickSecret = null;
        prefs.setSession(s[0], s[2]);
        prefs.setRunning(true);
        ui.post(new Runnable() {
            @Override public void run() {
                password.setText("");
                code.setVisibility(View.GONE);
                refresh();
                (Settings.canDrawOverlays(MainActivity.this) ? watchBtn : overlayBtn).requestFocus();
            }
        });
    }

    private void signOut() {
        final Api api = Api.of(prefs);
        new Thread(new Runnable() {
            @Override public void run() { api.signOut(); }
        }).start();
        prefs.signOut();
        refresh();
        quickBtn.requestFocus();
    }

    // ---- signed in ----

    private void testPanel() {
        Toast.makeText(this, "The overlay renderer arrives in a later version", Toast.LENGTH_LONG).show();
    }

    private void pickSession() {
        final Api api = Api.of(prefs);
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    JSONArray list = api.sessions();
                    final List<String> ids = new ArrayList<>();
                    final List<String> labels = new ArrayList<>();
                    ids.add("");
                    labels.add("Automatic (Jellyfin on this box)");
                    for (int i = 0; i < list.length(); i++) {
                        JSONObject s = list.getJSONObject(i);
                        String id = s.optString("DeviceId");
                        if (id.equals(prefs.deviceId()) || ids.contains(id)) continue;
                        ids.add(id);
                        JSONObject np = s.optJSONObject("NowPlayingItem");
                        labels.add(s.optString("DeviceName") + " - " + s.optString("Client")
                            + (np != null ? "  (playing " + np.optString("Name") + ")" : ""));
                    }
                    ui.post(new Runnable() {
                        @Override public void run() {
                            new AlertDialog.Builder(MainActivity.this)
                                .setTitle("Which Jellyfin player should the overlay follow?")
                                .setItems(labels.toArray(new String[0]), new DialogInterface.OnClickListener() {
                                    @Override public void onClick(DialogInterface d, int which) {
                                        String label = labels.get(which);
                                        int cut = label.indexOf("  (playing");
                                        prefs.setWatch(ids.get(which), cut > 0 ? label.substring(0, cut) : label);
                                        refresh();
                                    }
                                })
                                .show();
                        }
                    });
                } catch (Exception e) {
                    sayLater("Could not list players: " + e.getMessage());
                }
            }
        }).start();
    }

    private void openOverlaySettings() {
        try {
            startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName())));
        } catch (ActivityNotFoundException e) {
            try {
                startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION));
            } catch (ActivityNotFoundException e2) {
                say("This box has no overlay settings screen. Use the adb command shown above.");
            }
        }
    }

    // ---- views ----

    private void say(String s) {
        status.setText(s);
        status.setTextColor(Color.WHITE);
        ui.removeCallbacks(refresher);
        ui.postDelayed(refresher, 6000);
    }

    private void sayLater(final String s) {
        ui.post(new Runnable() {
            @Override public void run() { say(s); }
        });
    }

    private TextView text(String s, int sp) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(Color.WHITE);
        return t;
    }

    private EditText field(String hint, int type) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setInputType(type);
        e.setSingleLine(true);
        e.setTextColor(Color.WHITE);
        e.setHintTextColor(Color.argb(140, 255, 255, 255));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(520), LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(6);
        e.setLayoutParams(lp);
        return e;
    }

    private Button button(String label, View.OnClickListener click) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setOnClickListener(click);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMarginEnd(dp(12));
        lp.topMargin = dp(8);
        b.setLayoutParams(lp);
        return b;
    }

    private LinearLayout row() {
        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.HORIZONTAL);
        return l;
    }

    private LinearLayout column() {
        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.VERTICAL);
        return l;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }
}
