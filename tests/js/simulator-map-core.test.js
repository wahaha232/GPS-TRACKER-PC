// 只測不依賴畫面的 MapLibre 圖層重建與路線資料。
const test = require("node:test");
const assert = require("node:assert/strict");

const core = require("../../src/GpsTrackerPc.App/Simulator/simulator-map-core.js");

class FakeMap {
  constructor() {
    this.sources = {};
    this.layers = {};
    this.sourceAdds = [];
    this.layerAdds = [];
  }

  getSource(name) {
    return this.sources[name] || null;
  }

  addSource(name, definition) {
    this.sources[name] = definition;
    this.sourceAdds.push(name);
  }

  getLayer(name) {
    return this.layers[name] || null;
  }

  addLayer(definition) {
    this.layers[definition.id] = definition;
    this.layerAdds.push(definition.id);
  }
}

test("style 重置後會重新加回所有自訂 source 與 layer", () => {
  const map = new FakeMap();

  core.ensureCustomLayers(map);

  assert.deepEqual(
    map.sourceAdds.sort(),
    ["playback", "playback-tail", "route"]
  );
  assert.deepEqual(
    map.layerAdds.sort(),
    ["playback-line", "playback-tail-line", "route-casing", "route-line"]
  );

  // 模擬 setStyle：MapLibre 會清空 source/layer，再重新建立。
  const afterStyleReset = new FakeMap();
  core.ensureCustomLayers(afterStyleReset);

  assert.ok(afterStyleReset.getSource("route"));
  assert.ok(afterStyleReset.getSource("playback"));
  assert.ok(afterStyleReset.getSource("playback-tail"));
  assert.ok(afterStyleReset.getLayer("route-line"));
  assert.ok(afterStyleReset.getLayer("playback-line"));
  assert.ok(afterStyleReset.getLayer("playback-tail-line"));

  // 路線線條要隱藏，軌跡只能由 playback 圖層在移動時畫出來。
  assert.equal(afterStyleReset.getLayer("route-line").layout.visibility, "none");
  assert.equal(afterStyleReset.getLayer("route-casing").layout.visibility, "none");
});

test("重複呼叫不會重複加入 source 或 layer", () => {
  const map = new FakeMap();

  core.ensureCustomLayers(map);
  core.ensureCustomLayers(map);

  assert.equal(map.sourceAdds.length, 3);
  assert.equal(map.layerAdds.length, 4);
});

test("routeFeature 支援循環與空路線", () => {
  const loop = core.routeFeature(
    [
      { lon: 121.5, lat: 25.0 },
      { lon: 121.6, lat: 25.1 }
    ],
    true
  );

  assert.equal(loop.features[0].geometry.type, "LineString");
  assert.deepEqual(
    loop.features[0].geometry.coordinates,
    [[121.5, 25.0], [121.6, 25.1], [121.5, 25.0]]
  );

  const empty = core.routeFeature([], false);
  assert.equal(empty.features.length, 0);
});
