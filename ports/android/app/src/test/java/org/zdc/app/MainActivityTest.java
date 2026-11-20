package org.zdc.app;

import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.content.pm.ApplicationInfo;
import android.content.pm.ResolveInfo;
import android.net.Uri;
import android.os.Bundle;
import android.view.ViewGroup;
import android.webkit.WebView;
import android.widget.ListView;

import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowActivity;
import org.robolectric.shadows.ShadowAlertDialog;
import org.robolectric.shadows.ShadowPackageManager;
import org.robolectric.shadows.ShadowWebView;

import java.io.ByteArrayOutputStream;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = {23, 35})
public class MainActivityTest {
    private ShadowWebView view(MainActivity activity) {
        ViewGroup content = activity.findViewById(android.R.id.content);
        return shadowOf((WebView) content.getChildAt(0));
    }

    private void send(MainActivity activity, String action, String value) throws Exception {
        MainActivity.Host host = (MainActivity.Host) view(activity).getJavascriptInterface("ZdCHost");
        host.postMessage(new JSONObject().put("action", action).put("value", value).toString());
    }

    @Test public void listSurvivesRecreationAndMissingPackageIsReported() throws Exception {
        ActivityController<MainActivity> screen = Robolectric.buildActivity(MainActivity.class).setup();
        String list = "{\"version\":1,\"platform\":\"android\",\"entries\":[{\"id\":\"org.example.absent\",\"name\":\"Demo\"}]}";
        send(screen.get(), "save", list);
        assertTrue(view(screen.get()).getLastEvaluatedJavascript().contains("saved.apply(null,[true])"));
        screen.recreate();
        MainActivity activity = screen.get();
        send(activity, "ready", null);
        assertEquals(list, activity.getPreferences(0).getString("list", null));
        assertTrue(view(activity).getLastEvaluatedJavascript().contains("org.example.absent"));
        send(activity, "launch", "org.example.absent");
        assertTrue(view(activity).getLastEvaluatedJavascript().contains("已卸载"));
        assertNull(shadowOf(activity).getNextStartedActivity());
    }

    @Test public void launcherPickerReturnsPackageAndStartsItsRegisteredActivity() throws Exception {
        MainActivity activity = Robolectric.buildActivity(MainActivity.class).setup().get();
        String id = "org.example.notes";
        Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        ResolveInfo notes = new ResolveInfo();
        notes.nonLocalizedLabel = "Notes";
        notes.activityInfo = new ActivityInfo();
        notes.activityInfo.packageName = id;
        notes.activityInfo.name = id + ".Notes";
        notes.activityInfo.applicationInfo = new ApplicationInfo();
        notes.activityInfo.applicationInfo.packageName = id;
        ShadowPackageManager packages = shadowOf(activity.getPackageManager());
        packages.addResolveInfoForIntent(launcher, notes);
        packages.addResolveInfoForIntent(new Intent(launcher).setPackage(id), notes);
        send(activity, "pick", null);
        AlertDialog dialog = ShadowAlertDialog.getLatestAlertDialog();
        ListView choices = dialog.getListView();
        int index = 0;
        while (index < choices.getCount() && !choices.getItemAtPosition(index).toString().endsWith(id)) index++;
        assertTrue(index < choices.getCount());
        choices.performItemClick(choices.getChildAt(index), index, choices.getItemIdAtPosition(index));
        assertTrue(view(activity).getLastEvaluatedJavascript().contains("picked.apply(null,[\"org.example.notes\",\"Notes\"])"));
        send(activity, "save", "{\"version\":1,\"platform\":\"android\",\"entries\":[{\"id\":\"org.example.notes\",\"name\":\"Notes\"}]}");
        send(activity, "launch", id);
        assertEquals(id, shadowOf(activity).getNextStartedActivity().getComponent().getPackageName());
    }

    @Test public void documentPickerRestoresExactSnapshotAndCancelWritesNothing() throws Exception {
        MainActivity activity = Robolectric.buildActivity(MainActivity.class).setup().get();
        String rules = "payload:\n  - \"PROCESS-NAME,org.example.notes\"\n";
        send(activity, "export", rules);
        ShadowActivity.IntentForResult request = shadowOf(activity).getNextStartedActivityForResult();
        assertEquals(Intent.ACTION_CREATE_DOCUMENT, request.intent.getAction());
        Bundle bundle = new Bundle();
        activity.onSaveInstanceState(bundle);
        MainActivity restored = Robolectric.buildActivity(MainActivity.class).create(bundle).start().resume().get();
        Uri file = Uri.parse("content://org.example.documents/zdc.yaml");
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        shadowOf(restored.getContentResolver()).registerOutputStream(file, output);
        Intent result = new Intent().setData(file);
        restored.onActivityResult(request.requestCode, Activity.RESULT_OK, result);
        assertEquals(rules, output.toString("UTF-8"));

        send(restored, "export", "payload: []\n");
        restored.onActivityResult(request.requestCode, Activity.RESULT_CANCELED, null);
        restored.onActivityResult(request.requestCode, Activity.RESULT_OK, result);
        assertEquals(rules, output.toString("UTF-8"));
    }
}
