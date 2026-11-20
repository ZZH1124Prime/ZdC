#!/usr/bin/python3
"""GTK host for ZdC's packaged application list."""
import json
import mimetypes
import os
from pathlib import Path
import tempfile

import gi
gi.require_version("Gtk", "3.0")
try:
    gi.require_version("WebKit2", "4.1")
except ValueError:
    gi.require_version("WebKit2", "4.0")
from gi.repository import Gio, GLib, Gtk, WebKit2

UI = Path("/usr/share/zdc/ui")
RESOURCES = {"index.html", "style.css", "model.js", "ui.js", "ZdCMark.png"}
START = "zdc://app/index.html"
STATE = Path(GLib.get_user_config_dir()) / "zdc" / "applications.json"


def atomic_write(destination, data):
    with tempfile.NamedTemporaryFile(dir=destination.parent, delete=False) as tmp:
        temporary_path = Path(tmp.name)
        try:
            tmp.write(data.encode("utf-8"))
            tmp.flush()
            os.fsync(tmp.fileno())
        except BaseException:
            tmp.close()
            temporary_path.unlink()
            raise
    try:
        os.replace(temporary_path, destination)
    finally:
        if temporary_path.exists():
            temporary_path.unlink()


def executable(filename):
    path = Path(filename).resolve(strict=True)
    if not path.is_file() or not os.access(path, os.X_OK):
        raise ValueError("请选择有执行权限的程序文件。")
    with path.open("rb") as stream:
        if stream.read(4) != b"\x7fELF":
            raise ValueError("此适配器按 ELF 进程路径匹配，请选择实际程序文件。")
    return path


class ZdC(Gtk.Application):
    def __init__(self):
        super().__init__(application_id="org.zdc.App",
                         flags=Gio.ApplicationFlags.FLAGS_NONE)

    def do_activate(self):
        if self.get_active_window():
            self.get_active_window().present()
            return
        window = Gtk.ApplicationWindow(application=self, title="ZdC")
        window.set_default_size(680, 500)
        context = WebKit2.WebContext.new_ephemeral()
        context.register_uri_scheme("zdc", self.resource, None)
        context.get_security_manager().register_uri_scheme_as_secure("zdc")
        manager = WebKit2.UserContentManager()
        manager.connect("script-message-received::zdc", self.message)
        manager.register_script_message_handler("zdc")
        self.view = WebKit2.WebView(web_context=context, user_content_manager=manager)
        self.view.connect("decide-policy", self.policy)
        self.view.load_uri(START)
        window.add(self.view)
        window.show_all()

    def resource(self, request, unused):
        name = request.get_path().lstrip("/")
        if name not in RESOURCES or request.get_uri() != "zdc://app/" + name:
            request.finish_error(GLib.Error.new_literal(Gio.io_error_quark(), "Unknown asset", Gio.IOErrorEnum.NOT_FOUND))
            return
        try:
            data = (UI / name).read_bytes()
            stream = Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data))
            mime = "application/javascript" if name.endswith(".js") else mimetypes.guess_type(name)[0]
            request.finish(stream, len(data), mime)
        except OSError as error:
            request.finish_error(GLib.Error.new_literal(Gio.io_error_quark(), str(error), Gio.IOErrorEnum.FAILED))

    def policy(self, view, decision, kind):
        if kind == WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION or (
            kind == WebKit2.PolicyDecisionType.NAVIGATION_ACTION and
            decision.get_navigation_action().get_request().get_uri() != START
        ):
            decision.ignore()
            return True
        return False

    def emit(self, method, *args):
        self.view.run_javascript("window.ZdC." + method + "(" + ", ".join(map(json.dumps, args)) + ")", None, None, None)

    def choose(self, saving):
        action = Gtk.FileChooserAction.SAVE if saving else Gtk.FileChooserAction.OPEN
        dialog = Gtk.FileChooserDialog(title="导出规则集" if saving else "选择程序文件",
                                       parent=self.get_active_window(), action=action)
        dialog.add_buttons("取消", Gtk.ResponseType.CANCEL, "保存" if saving else "选择", Gtk.ResponseType.OK)
        dialog.set_local_only(True)
        if saving:
            dialog.set_current_name("zdc-rules.yaml")
            dialog.set_do_overwrite_confirmation(True)
        try:
            return dialog.get_filename() if dialog.run() == Gtk.ResponseType.OK else None
        finally:
            dialog.destroy()

    def message(self, manager, result):
        msg = json.loads(result.get_js_value().to_string())
        action, value = msg["action"], msg.get("value")
        try:
            if action == "ready":
                try:
                    saved = STATE.read_text(encoding="utf-8")
                except FileNotFoundError:
                    saved = None
                self.emit("open", "linux", saved)
            elif action == "save":
                STATE.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                atomic_write(STATE, value)
                self.emit("saved", True)
            elif action == "pick":
                selected = self.choose(False)
                if selected:
                    path = executable(selected)
                    self.emit("picked", str(path), path.name)
            elif action == "launch":
                entries = json.loads(STATE.read_text(encoding="utf-8"))["entries"]
                if not any(entry["id"] == value for entry in entries):
                    raise ValueError("此入口已移除。")
                path = executable(value)
                if str(path) != value:
                    raise ValueError("入口路径已变更，请重新登记。")
                Gio.Subprocess.new([str(path)], Gio.SubprocessFlags.NONE)
                self.emit("notice", "已请求启动，分流结果请查看客户端连接日志。")
            elif action == "export":
                selected = self.choose(True)
                if selected:
                    destination = Path(selected)
                    if destination.resolve() == STATE.resolve():
                        raise ValueError("请选择与应用名单不同的文件。")
                    atomic_write(destination, value)
                    self.emit("notice", "规则集已写入所选文件，请在客户端导入或重载。")
        except (OSError, ValueError, RuntimeError, GLib.Error) as error:
            if action == "save":
                self.emit("saved", False)
            else:
                self.emit("notice", "操作未完成：" + str(error))


if __name__ == "__main__":
    raise SystemExit(ZdC().run(None))
