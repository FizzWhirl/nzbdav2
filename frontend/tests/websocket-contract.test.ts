import assert from "node:assert/strict";
import test from "node:test";
import { isWebsocketTopicMessage } from "../app/types/websocket";

test("accepts valid topic messages", () => {
    assert.equal(isWebsocketTopicMessage({ Topic: "Queue", Message: "{}" }), true);
    assert.equal(isWebsocketTopicMessage({ Topic: "Stats", Message: { count: 1, ok: true, rows: ["a", null] } }), true);
});

test("rejects malformed topic messages", () => {
    assert.equal(isWebsocketTopicMessage(null), false);
    assert.equal(isWebsocketTopicMessage({ Topic: 42, Message: "{}" }), false);
    assert.equal(isWebsocketTopicMessage({ Topic: "Queue" }), false);
    assert.equal(isWebsocketTopicMessage({ Topic: "Queue", Message: undefined }), false);
    assert.equal(isWebsocketTopicMessage({ Topic: "Queue", Message: { nested: undefined } }), false);
});