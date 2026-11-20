"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const model = require("../shared/model.js");

for (const [platform, id, kind] of [
    ["linux", "/opt/工具 #1/示例", "PROCESS-PATH"],
    ["android", "org.example.updatable", "PROCESS-NAME"]
]) {
    test(platform + " register, reopen, export and remove", () => {
        const initial = model.read(platform, null);
        const state = model.add(initial, { id, name: "示例" });
        assert.deepEqual(model.read(platform, JSON.stringify(state)), state);
        assert.equal(model.provider(state), "payload:\n  - " + JSON.stringify(kind + "," + id) + "\n");
        assert.throws(() => model.add(state, { id, name: "升级后的名字" }));
        assert.equal(initial.entries.length, 0);
        assert.equal(model.provider(model.remove(state, id)), "payload: []\n");
    });
}
test("malformed, future and cross-platform lists fail closed", () => {
    for (const raw of ["", "null", "{", '{"version":2,"platform":"linux","entries":[]}',
        JSON.stringify(model.read("android", null))]) {
        assert.throws(() => model.read("linux", raw));
    }
});
test("rejects rule separators, control characters and unresolved paths", () => {
    const state = model.read("linux", null);
    for (const id of ["/a,b", "/a\nMATCH", "/a/../b", "relative", "/a/./b", "/bin/", "/a//b", "/a "]) {
        assert.throws(() => model.add(state, { id, name: "x" }));
    }
    assert.throws(() => model.add(model.read("android", null), { id: "org.*", name: "x" }));
});
test("persisted duplicate IDs are rejected", () => {
    const entry = { id: "org.example.app", name: "示例" };
    const state = { version: 1, platform: "android", entries: [entry, entry] };
    assert.throws(() => model.read("android", JSON.stringify(state)));
});
