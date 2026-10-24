(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.FriendsMapCore = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  function number(value) {
    return typeof value === "number" && isFinite(value) ? value : null;
  }

  function pointOf(item, isSelf) {
    if (!item) return null;
    var lat = number(item.lat);
    var lng = number(item.lng);
    if (lat === null || lng === null) return null;
    if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;
    return { lat: lat, lng: lng, self: isSelf };
  }

  function kindOf(item, isSelf) {
    if (isSelf) return "self";
    if ((item && item.sharing === false) || (item && item.stopped)) return "stopped";
    if (item && item.online) return "online";
    return "offline";
  }

  function featureOf(item, isSelf) {
    var point = pointOf(item, isSelf);
    if (!point) return null;
    return {
      type: "Feature",
      geometry: { type: "Point", coordinates: [point.lng, point.lat] },
      properties: {
        id: String(isSelf ? "self" : item.id || item.deviceId || ""),
        name: String((item && item.name) || (isSelf ? "我" : "未命名")),
        kind: kindOf(item, isSelf),
        self: isSelf,
        distance: item && item.distanceText ? String(item.distanceText) : "",
        updated: item && item.updatedText ? String(item.updatedText) : "",
      },
    };
  }

  function buildFeatures(presence) {
    var features = [];
    var self = presence && presence.self ? featureOf(presence.self, true) : null;
    if (self) features.push(self);

    var peers = (presence && presence.peers) || [];
    for (var i = 0; i < peers.length; i++) {
      var feature = featureOf(peers[i], false);
      if (feature) features.push(feature);
    }

    return { type: "FeatureCollection", features: features };
  }

  function boundsOf(presence) {
    var features = buildFeatures(presence).features;
    if (!features.length) return null;

    var minLng = Infinity;
    var minLat = Infinity;
    var maxLng = -Infinity;
    var maxLat = -Infinity;
    for (var i = 0; i < features.length; i++) {
      var coords = features[i].geometry.coordinates;
      minLng = Math.min(minLng, coords[0]);
      maxLng = Math.max(maxLng, coords[0]);
      minLat = Math.min(minLat, coords[1]);
      maxLat = Math.max(maxLat, coords[1]);
    }

    // 單一點或彼此重疊時給一個小範圍，避免縮放到最大層級。
    if (Math.abs(maxLng - minLng) < 1e-6) {
      minLng -= 0.01;
      maxLng += 0.01;
    }
    if (Math.abs(maxLat - minLat) < 1e-6) {
      minLat -= 0.01;
      maxLat += 0.01;
    }

    return [[minLng, minLat], [maxLng, maxLat]];
  }

  function summary(presence) {
    var peers = (presence && presence.peers) || [];
    var online = 0;
    var stopped = 0;
    var offline = 0;
    for (var i = 0; i < peers.length; i++) {
      if (peers[i].sharing === false || peers[i].stopped) stopped++;
      else if (peers[i].online) online++;
      else offline++;
    }
    return {
      total: peers.length,
      online: online,
      offline: offline,
      stopped: stopped,
    };
  }

  return {
    buildFeatures: buildFeatures,
    boundsOf: boundsOf,
    summary: summary,
    kindOf: kindOf,
  };
});
