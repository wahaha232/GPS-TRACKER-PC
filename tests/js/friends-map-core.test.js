const test = require("node:test");
const assert = require("node:assert/strict");

const core = require("../../src/GpsTrackerPc.App/Friends/friends-map-core.js");

test("buildFeatures 會放入自己和好友，並保留線上狀態", () => {
  const presence = {
    self: { name: "我的電腦", lat: 25.0, lng: 121.0 },
    peers: [
      { id: "a", name: "小明", lat: 25.1, lng: 121.1, online: true, sharing: true },
      { id: "b", name: "小華", lat: 25.2, lng: 121.2, online: false, sharing: false },
    ],
  };

  const features = core.buildFeatures(presence).features;

  assert.equal(features.length, 3);
  assert.equal(features[0].properties.kind, "self");
  assert.deepEqual(features[0].geometry.coordinates, [121.0, 25.0]);
  assert.equal(features[1].properties.kind, "online");
  assert.equal(features[2].properties.kind, "stopped");
  assert.equal(features[2].properties.name, "小華");
});

test("buildFeatures 會忽略座標不完整的項目", () => {
  const features = core.buildFeatures({
    peers: [
      { id: "a", name: "壞資料", lat: 999, lng: 121.0 },
      { id: "b", name: "少經度", lat: 25.0 },
      { id: "c", name: "正常", lat: 25.0, lng: 121.0, online: true },
    ],
  }).features;

  assert.equal(features.length, 1);
  assert.equal(features[0].properties.id, "c");
});

test("boundsOf 沒有位置時回傳 null", () => {
  assert.equal(core.boundsOf({ self: null, peers: [] }), null);
});

test("boundsOf 只有一個點時會給出小範圍", () => {
  const bounds = core.boundsOf({
    self: { name: "我", lat: 25.0, lng: 121.0 },
    peers: [],
  });

  assert.ok(bounds);
  assert.ok(bounds[0][0] < 121.0);
  assert.ok(bounds[1][0] > 121.0);
  assert.ok(bounds[0][1] < 25.0);
  assert.ok(bounds[1][1] > 25.0);
});

test("boundsOf 會包住所有成員", () => {
  const bounds = core.boundsOf({
    self: { name: "我", lat: 25.0, lng: 121.0 },
    peers: [
      { id: "a", name: "A", lat: 24.5, lng: 120.5, online: true },
      { id: "b", name: "B", lat: 25.5, lng: 121.5, online: true },
    ],
  });

  assert.deepEqual(bounds, [[120.5, 24.5], [121.5, 25.5]]);
});

test("summary 會算線上、離線與停止分享人數", () => {
  assert.deepEqual(
    core.summary({
      peers: [
        { online: true },
        { online: false },
        { online: true, sharing: false },
        { online: false, sharing: false },
      ],
    }),
    { total: 4, online: 1, offline: 1, stopped: 2 },
  );
});
