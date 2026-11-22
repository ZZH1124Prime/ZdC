package org.zdc.app;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ContentResolver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.UriPermission;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Insets;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.view.WindowInsets;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.text.Collator;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

public final class MainActivity extends Activity {
    private static final int CREATE_RULES = 21;
    private static final String LIST_KEY = "list";
    private static final String PICK_EXPORT_KEY = "pickExportDocument";
    private static final String BASE = "https://appassets.androidplatform.net/assets/";
    private WebView web;
    private String pendingExport;

    @Override @SuppressLint("SetJavaScriptEnabled")
    public void onCreate(Bundle state) {
        super.onCreate(state);
        Set<String> webFiles = new HashSet<>(Arrays.asList(getString(R.string.web_asset_names).split("\n")));
        if (state != null) pendingExport = state.getString("pendingExport");
        web = new WebView(this);
        if (Build.VERSION.SDK_INT >= 35) {
            web.setOnApplyWindowInsetsListener((v, insets) -> {
                Insets edges = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                v.setPadding(edges.left, edges.top, edges.right, edges.bottom);
                return insets;
            });
        }
        web.getSettings().setJavaScriptEnabled(true);
        web.getSettings().setAllowFileAccess(false);
        web.getSettings().setAllowContentAccess(false);
        web.getSettings().setBlockNetworkLoads(true);
        web.setWebViewClient(new WebViewClient() {
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                String url = request.getUrl().toString();
                if (request.getMethod().equals("GET") && url.startsWith(BASE)) {
                    String file = url.substring(BASE.length());
                    if (webFiles.contains(file)) {
                        try {
                            InputStream input = getAssets().open(file);
                            if (file.endsWith(".png")) return new WebResourceResponse("image/png", null, input);
                            String mime = file.endsWith(".js") ? "application/javascript" :
                                    (file.endsWith(".css") ? "text/css" : "text/html");
                            return new WebResourceResponse(mime, "utf-8", input);
                        } catch (IOException ignored) { /* Return a local miss. */ }
                    }
                }
                return new WebResourceResponse("text/plain", "utf-8", 404, "Not Found", null,
                        new ByteArrayInputStream(new byte[0]));
            }
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) { return true; }
            @Override public boolean shouldOverrideUrlLoading(WebView view, String url) { return true; }
        });
        web.addJavascriptInterface(new Host(), "ZdCHost");
        setContentView(web);
        web.loadUrl(BASE + "index.html");
    }

    final class Host {
        @JavascriptInterface public void postMessage(String message) {
            runOnUiThread(() -> {
                if (isDestroyed() || isFinishing()) return;
                String action = null;
                try {
                    JSONObject request = new JSONObject(message);
                    String value = request.optString("value", "");
                    action = request.getString("action");
                    switch (action) {
                        case "ready":
                            emit("open", "android", getPreferences(Context.MODE_PRIVATE).getString(LIST_KEY, null));
                            break;
                        case "pick":
                            pick();
                            break;
                        case "save":
                            emit("saved", saveList(getPreferences(Context.MODE_PRIVATE), value));
                            break;
                        case "launch":
                            launch(value);
                            break;
                        case "export":
                        case "exportAs":
                            exportRules(value, "exportAs".equals(action));
                            break;
                        default:
                            emit("notice", "未知操作。");
                    }
                } catch (JSONException | RuntimeException error) {
                    if ("save".equals(action)) emit("saved", false);
                    else emit("notice", "操作未完成：" + error.getLocalizedMessage());
                }
            });
        }
    }

    private static boolean saveList(SharedPreferences preferences, String value) {
        String previous = preferences.getString(LIST_KEY, null);
        boolean saved = false;
        try {
            saved = preferences.edit().putString(LIST_KEY, value).commit();
            return saved;
        } finally {
            // A failed disk write can still publish the new value to readers in this process.
            if (!saved) preferences.edit().putString(LIST_KEY, previous).commit();
        }
    }

    private void pick() {
        PackageManager packages = getPackageManager();
        Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        Map<String, String> names = new LinkedHashMap<>();
        for (ResolveInfo info : packages.queryIntentActivities(launcher, 0)) {
            String id = info.activityInfo.packageName;
            if (!id.equals(getPackageName())) names.put(id, info.loadLabel(packages).toString());
        }
        if (names.isEmpty()) { emit("notice", "当前用户下没有其他可启动的应用。"); return; }
        List<String> ids = new ArrayList<>(names.keySet());
        Collator collator = Collator.getInstance();
        Collections.sort(ids, (a, b) -> collator.compare(names.get(a), names.get(b)));
        String[] labels = new String[ids.size()];
        for (int i = 0; i < labels.length; i++) labels[i] = names.get(ids.get(i)) + "\n" + ids.get(i);
        new AlertDialog.Builder(this).setTitle("添加应用").setItems(labels, (dialog, index) -> {
            String id = ids.get(index);
            emit("picked", id, names.get(id));
        }).setNegativeButton("取消", null).show();
    }

    private void launch(String id) throws JSONException {
        String raw = getPreferences(Context.MODE_PRIVATE).getString(LIST_KEY, null);
        if (raw != null) {
            JSONArray entries = new JSONObject(raw).getJSONArray("entries");
            for (int i = 0; i < entries.length(); i++) {
                if (!id.equals(entries.getJSONObject(i).getString("id"))) continue;
                Intent intent = getPackageManager().getLaunchIntentForPackage(id);
                if (intent != null) { startActivity(intent); return; }
                break;
            }
        }
        emit("notice", "应用已移除、已卸载或启动入口已变更。");
    }

    private void emit(String method, Object... values) {
        JSONArray arguments = new JSONArray();
        for (Object value : values) arguments.put(value == null ? JSONObject.NULL : value);
        // Escape JS line separators on older WebViews.
        String json = arguments.toString().replace("\u2028", "\\u2028").replace("\u2029", "\\u2029");
        web.evaluateJavascript("window.ZdC." + method + ".apply(null," + json + ")", null);
    }

    @Override protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out);
        out.putString("pendingExport", pendingExport);
    }

    private List<Uri> persistedExportTargets() {
        List<Uri> targets = new ArrayList<>();
        for (UriPermission grant : getContentResolver().getPersistedUriPermissions()) {
            if (grant.isWritePermission()) targets.add(grant.getUri());
        }
        return targets;
    }

    private void exportRules(String payload, boolean chooseFile) {
        if (pendingExport != null) return;
        if (!chooseFile && !getPreferences(Context.MODE_PRIVATE).getBoolean(PICK_EXPORT_KEY, false)) {
            List<Uri> targets = persistedExportTargets();
            if (targets.size() == 1) {
                try {
                    writeExport(targets.get(0), payload);
                    return;
                } catch (FileNotFoundException | SecurityException expired) {
                    // A provider may remove the document or withdraw access between exports.
                } catch (IOException | RuntimeException error) {
                    showExportError(error);
                    return;
                }
            }
        }
        Intent picker = new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                .setType("application/yaml").putExtra(Intent.EXTRA_TITLE, "zdc-rules.yaml");
        startActivityForResult(picker, CREATE_RULES);
        pendingExport = payload;
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != CREATE_RULES) return;
        String payload = pendingExport;
        pendingExport = null;
        if (result != RESULT_OK || data == null || data.getData() == null || payload == null) return;
        Uri uri = data.getData();
        if (!ContentResolver.SCHEME_CONTENT.equals(uri.getScheme())) return;
        ContentResolver resolver = getContentResolver();
        SharedPreferences preferences = getPreferences(Context.MODE_PRIVATE);
        boolean alreadyPicking = preferences.getBoolean(PICK_EXPORT_KEY, false);
        List<Uri> selectedOnly = Collections.singletonList(uri);
        List<Uri> previousTargets = persistedExportTargets();
        boolean suspendPrevious = !alreadyPicking && !previousTargets.isEmpty()
                && !previousTargets.equals(selectedOnly);
        // Persist the guard before writing another document, including across process death.
        if (suspendPrevious && !preferences.edit().putBoolean(PICK_EXPORT_KEY, true).commit()) {
            showExportError(new IOException(android.system.Os.strerror(android.system.OsConstants.EIO)));
            return;
        }
        try {
            writeExport(uri, payload);
        } catch (IOException | RuntimeException error) {
            if (suspendPrevious) preferences.edit().remove(PICK_EXPORT_KEY).apply();
            showExportError(error);
            return;
        }
        int write = Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
        int required = write | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION;
        if ((data.getFlags() & required) == required) {
            try {
                resolver.takePersistableUriPermission(uri, write);
            } catch (SecurityException ignored) { /* The provider may allow just this export. */ }
        }
        List<Uri> targets = persistedExportTargets();
        if (!targets.contains(uri)) return;
        for (Uri other : targets) {
            if (uri.equals(other)) continue;
            try {
                resolver.releasePersistableUriPermission(other, write);
            } catch (SecurityException ignored) { /* The grant may already have been revoked. */ }
        }
        if ((suspendPrevious || alreadyPicking) && persistedExportTargets().equals(selectedOnly)) {
            preferences.edit().remove(PICK_EXPORT_KEY).apply();
        }
    }

    private void writeExport(Uri uri, String payload) throws IOException {
        try (OutputStream stream = getContentResolver().openOutputStream(uri, "wt")) {
            if (stream == null) throw new IOException("无法打开文件");
            stream.write(payload.getBytes(StandardCharsets.UTF_8));
        }
        android.widget.Toast.makeText(this, "规则集已写入，请在客户端导入或重载。", android.widget.Toast.LENGTH_LONG).show();
    }

    private void showExportError(Exception error) {
        new AlertDialog.Builder(this).setMessage("写入失败：" + error.getLocalizedMessage()).setPositiveButton("确定", null).show();
    }

    @Override protected void onDestroy() {
        web.removeJavascriptInterface("ZdCHost");
        web.destroy();
        super.onDestroy();
    }
}
