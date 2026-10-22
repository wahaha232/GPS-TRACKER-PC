(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.SimulatorMapCore = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  function emptyFeatureCollection() {
    return { type: "FeatureCollection", features: [] };
  }

  function routeFeature(routePoints, loop) {
    var coordinates = (routePoints || []).map(function (point) {
      return [point.lon, point.lat];
    });

    if (coordinates.length > 1 && loop) {
      coordinates.push([coordinates[0][0], coordinates[0][1]]);
    }

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
  function ensureCustomLayers(map) {
    if (!map.getSource("route")) {
      map.addSource("route", { type: "geojson", data: emptyFeatureCollection() });
    }

    if (!map.getLayer("route-casing")) {
      map.addLayer({
        id: "route-casing",
        type: "line",
        source: "route",
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#0b1220", "line-width": 8, "line-opacity": 0.8 }
      });
    }

    if (!map.getLayer("route-line")) {
      map.addLayer({
        id: "route-line",
        type: "line",
        source: "route",
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#38bdf8", "line-width": 4 }
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
    emptyFeatureCollection: emptyFeatureCollection,
    routeFeature: routeFeature,
    ensureCustomLayers: ensureCustomLayers
  };
});
