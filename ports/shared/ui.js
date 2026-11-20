"use strict";

(() => {
    const element = id => document.getElementById(id);
    let state = null;
    let pending = null;

    function send(action, value = null) {
        const message = JSON.stringify({ action, value });
        if (window.ZdCHost) window.ZdCHost.postMessage(message);
        else if (window.webkit && window.webkit.messageHandlers) window.webkit.messageHandlers.zdc.postMessage(message);
        else throw Error("平台适配器未连接。");
    }

    function notice(message) { element("feedback").textContent = message; }

    function render() {
        const list = element("entries");
        list.textContent = "";
        const disabled = !state || pending !== null;
        element("pick").disabled = element("export").disabled = disabled;
        element("empty").hidden = !state || state.entries.length !== 0;
        if (!state) return;
        element("platform").textContent = (state.platform === "linux" ? "Linux" : "Android") + " · " + state.entries.length + " 个入口";
        for (const entry of state.entries) {
            const row = document.createElement("li");
            const identity = document.createElement("div");
            identity.className = "identity";
            const label = document.createElement("strong");
            label.textContent = entry.name;
            const id = document.createElement("small");
            id.textContent = entry.id;
            identity.append(label, id);
            const actions = document.createElement("div");
            actions.className = "row-actions";
            for (const caption of ["启动", "移除"]) {
                const button = document.createElement("button");
                button.textContent = caption;
                button.disabled = disabled;
                button.setAttribute("aria-label", caption + " " + entry.name);
                button.onclick = () => {
                    if (caption === "启动") send("launch", entry.id);
                    else save(ZdCModel.remove(state, entry.id));
                };
                actions.append(button);
            }
            row.append(identity, actions);
            list.append(row);
        }
    }

    function save(next) {
        if (pending !== null) return;
        pending = next;
        render();
        try { send("save", JSON.stringify(next)); }
        catch (error) { pending = null; render(); notice(error.message); }
    }

    window.ZdC = {
        open(platform, raw) {
            try { state = ZdCModel.read(platform, raw); }
            catch (error) { state = null; notice(error.message); }
            render();
        },
        picked(id, name) {
            if (!state || pending) return;
            try { save(ZdCModel.add(state, { id, name })); }
            catch (error) { notice(error.message); }
        },
        saved(ok) {
            if (!pending) return;
            if (ok) state = pending;
            pending = null;
            render();
            notice(ok ? "名单已保存，更新规则集后请重载客户端。" : "名单保存失败，请检查存储空间和权限。");
        },
        notice
    };
    element("pick").onclick = () => send("pick");
    element("export").onclick = () => send("export", ZdCModel.provider(state));
    try { send("ready"); } catch (error) { notice(error.message); }
})();
