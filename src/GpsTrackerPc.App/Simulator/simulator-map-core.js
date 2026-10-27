(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.SimulatorMapCore = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  // 超過這個點數就不再為每個路線點建立可拖曳的 DOM 標記，改用 GeoJSON 圖層畫圓點。
  // 1000 點以上時，MapLibre 每格畫面都要更新所有 Marker 的位置，會造成明顯卡頓。
  var ROUTE_POINT_MARKER_LIMIT = 200;

  function emptyFeatureCollection() {
    return { type: "FeatureCollection", features: [] };
  }

  function isDenseRoute(routePoints) {
    return (routePoints || []).length > ROUTE_POINT_MARKER_LIMIT;
  }

  function routePointsFeature(routePoints, selectedId) {
    var features = (routePoints || []).map(function (point, index) {
      return {
        type: "Feature",
        properties: {
          id: point.id,
          index: index,
          selected: point.id === selectedId
        },
        geometry: { type: "Point", coordinates: [point.lon, point.lat] }
      };
    });

    return { type: "FeatureCollection", features: features };
  }

  // 循環路線是沿原路折返，不會從終點直線連回起點，所以線條一律不閉合。
  function routeFeature(routePoints) {
    var coordinates = (routePoints || []).map(function (point) {
      return [point.lon, point.lat];
    });

    return coordinates.length >= 2
      ? {
          type: "FeatureCollection",
          features: [{
            type: "Feature",
            properties: {},
            geometry: { type: "LineString", coordinates: coordinates }
          }]
        }
      : emptyFeatureCollection();
  }

  // MapLibre setStyle 會清掉自訂 source/layer；style.load 後呼叫這個函式加回去。
  // 路線只保留編輯用的點位；軌跡要等播放開始、大頭針移動時才由 playback 圖層畫出來。
  function ensureCustomLayers(map) {
    if (!map.getSource("route")) {
      map.addSource("route", { type: "geojson", data: emptyFeatureCollection() });
    }

    if (!map.getLayer("route-casing")) {
      map.addLayer({
        id: "route-casing",
        type: "line",
        source: "route",
        layout: {
          "line-cap": "round",
          "line-join": "round",
          visibility: "none"
        },
        paint: { "line-color": "#0b1220", "line-width": 8, "line-opacity": 0.8 }
      });
    }

    if (!map.getLayer("route-line")) {
      map.addLayer({
        id: "route-line",
        type: "line",
        source: "route",
        layout: {
          "line-cap": "round",
          "line-join": "round",
          visibility: "none"
        },
        paint: { "line-color": "#38bdf8", "line-width": 4 }
      });
    }

    // 點數很多時用這個圖層畫路線點；預設隱藏，由 simulator.js 依點數決定要不要顯示。
    // 不使用 symbol 文字（部分點陣底圖沒有 glyphs，會畫不出編號）。
    if (!map.getSource("route-points")) {
      map.addSource("route-points", { type: "geojson", data: emptyFeatureCollection() });
    }

    if (!map.getLayer("route-points-circles")) {
      map.addLayer({
        id: "route-points-circles",
        type: "circle",
        source: "route-points",
        layout: { visibility: "none" },
        paint: {
          "circle-radius": ["case", ["==", ["get", "selected"], true], 8, 5],
          "circle-color": ["case", ["==", ["get", "selected"], true], "#fbbf24", "#38bdf8"],
          "circle-stroke-color": "#f8fafc",
          "circle-stroke-width": 2
        }
      });
    }

    if (!map.getSource("playback")) {
      map.addSource("playback", { type: "geojson", data: emptyFeatureCollection() });
    }

    if (!map.getLayer("playback-line")) {
      map.addLayer({
        id: "playback-line",
        type: "line",
        source: "playback",
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#22c55e", "line-width": 5, "line-opacity": 0.95 }
      });
    }

    if (!map.getSource("playback-tail")) {
      map.addSource("playback-tail", { type: "geojson", data: emptyFeatureCollection() });
    }

    if (!map.getLayer("playback-tail-line")) {
      map.addLayer({
        id: "playback-tail-line",
        type: "line",
        source: "playback-tail",
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#86efac", "line-width": 4, "line-opacity": 0.95 }
      });
    }
  }

  return {
    ROUTE_POINT_MARKER_LIMIT: ROUTE_POINT_MARKER_LIMIT,
    emptyFeatureCollection: emptyFeatureCollection,
    isDenseRoute: isDenseRoute,
    routePointsFeature: routePointsFeature,
    routeFeature: routeFeature,
    ensureCustomLayers: ensureCustomLayers
  };
});
