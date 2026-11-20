"use strict";

const ZdCModel = (() => {
    function checkId(platform, id) {
        if (typeof id !== "string" || !id.length || id.length > 4096 ||
            /[,\u0000-\u001f\u007f\u0085\u2028\u2029]/.test(id) || id.trim() !== id) {
            throw Error("此入口名称无法写成精确的进程规则。");
        }
        if (platform === "android" && !/^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$/.test(id)) {
            throw Error("请选择带启动入口的 Android 应用。");
        }
        if (platform === "linux" && (!id.startsWith("/") || id.split("/").slice(1).some(p => !p || p === "." || p === ".."))) {
            throw Error("请选择可执行文件的绝对路径。");
        }
    }

    function read(platform, raw) {
        if (!["android", "linux"].includes(platform)) throw Error("无法识别当前平台。");
        if (raw === null) return { version: 1, platform, entries: [] };
        const state = JSON.parse(raw);
        if (!state || state.version !== 1 || state.platform !== platform || !Array.isArray(state.entries)) {
            throw Error("无法读取当前名单版本，原文件已保留。");
        }
        const seen = new Set();
        for (const entry of state.entries) {
            if (!entry || typeof entry.name !== "string" || !entry.name.trim() || seen.has(entry.id)) {
                throw Error("名单中有无效或重复项目，原文件已保留。");
            }
            checkId(platform, entry.id);
            seen.add(entry.id);
        }
        return state;
    }

    function add(state, entry) {
        checkId(state.platform, entry.id);
        if (state.entries.some(item => item.id === entry.id)) throw Error("这个入口已在名单中。");
        if (typeof entry.name !== "string" || !entry.name.trim()) throw Error("应用名称为空。");
        return { ...state, entries: [...state.entries, { id: entry.id, name: entry.name }] };
    }

    function remove(state, id) {
        return { ...state, entries: state.entries.filter(entry => entry.id !== id) };
    }

    function provider(state) {
        const match = state.platform === "android" ? "PROCESS-NAME" : "PROCESS-PATH";
        if (!state.entries.length) return "payload: []\n";
        return "payload:\n" + state.entries.map(entry => "  - " + JSON.stringify(match + "," + entry.id) + "\n").join("");
    }

    return { read, add, remove, provider };
})();

if (typeof module !== "undefined") module.exports = ZdCModel;
