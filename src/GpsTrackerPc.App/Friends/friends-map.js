(function () {
  "use strict";

  var core = window.FriendsMapCore;
  var map = null;
  var ready = false;
  var pending = null;
  var markers = {};
  var lastFitKey = "";
  var empty = document.getElementById("empty");

  function fitPresence(presence) {
    var bounds = core.boundsOf(presence);
    if (!bounds) return false;
    map.fitBounds(bounds, { padding: 70, maxZoom: 16, duration: 450 });
    return true;
  }

  function markerElement(feature) {
    var element = document.createElement("div");
    element.className = "friend-marker kind-" + feature.properties.kind;

    var dot = document.createElement("div");
    dot.className = "dot";
    element.appendChild(dot);

    var label = document.createElement("div");
    label.className = "label";
    label.textContent = feature.properties.name;

    var detail = [feature.properties.distance, feature.properties.updated]
      .filter(function (value) { return !!value; })
      .join(" · ");
    if (detail) {
      var small = document.createElement("small");
      small.textContent = detail;
      label.appendChild(small);
    }

    element.appendChild(label);
    return element;
  }

  function render(presence) {
    if (!ready || !map) {
      pending = presence;
      return;
    }

    var features = core.buildFeatures(presence).features;
    var seen = {};

    for (var i = 0; i < features.length; i++) {
      var feature = features[i];
      var id = feature.properties.id;
      seen[id] = true;
      var coords = feature.geometry.coordinates;

      if (markers[id]) {
        markers[id].setLngLat(coords);
        var existing = markers[id].getElement();
        existing.className = "friend-marker kind-" + feature.properties.kind;
        var label = existing.querySelector(".label");
        if (label) {
          label.textContent = feature.properties.name;
          var detail = [feature.properties.distance, feature.properties.updated]
            .filter(function (value) { return !!value; })
            .join(" · ");
          if (detail) {
            var small = document.createElement("small");
            small.textContent = detail;
            label.appendChild(small);
          }
        }
      } else {
        markers[id] = new maplibregl.Marker({
          element: markerElement(feature),
          anchor: "center",
        })
          .setLngLat(coords)
          .addTo(map);
      }
    }

    Object.keys(markers).forEach(function (id) {
      if (!seen[id]) {
        markers[id].remove();
        delete markers[id];
      }
    });

    empty.hidden = features.length > 0;

    var fitKey = features
      .map(function (feature) {
        return feature.properties.id + ":" +
          feature.geometry.coordinates[0].toFixed(4) + "," +
          feature.geometry.coordinates[1].toFixed(4);
      })
      .sort()
      .join("|");
    if (presence && presence.fit === true && fitKey !== lastFitKey) {
      if (fitPresence(presence)) lastFitKey = fitKey;
    } else if (features.length && !lastFitKey) {
      if (fitPresence(presence)) lastFitKey = fitKey;
    }
  }

  function init() {
    map = new maplibregl.Map({
      container: "map",
      style: "https://tiles.openfreemap.org/styles/positron",
      center: [121.0, 23.7],
      zoom: 7,
      attributionControl: false,
      antialias: false,
      fadeDuration: 0,
    });
    map.addControl(new maplibregl.NavigationControl({ showCompass: false }), "top-right");
    map.addControl(new maplibregl.AttributionControl({ compact: true }), "bottom-right");

    map.on("load", function () {
      ready = true;
      if (pending) {
        var presence = pending;
        pending = null;
        render(presence);
      }
    });

    map.on("error", function (event) {
      // 底圖載入失敗時仍保留已收到的大頭針，只把錯誤寫到 console。
      if (event && event.error) console.warn("friends map:", event.error);
    });
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", function (event) {
      var data = event.data;
      if (data && data.type === "presence") {
        render(data.presence);
      } else if (data && data.type === "fit") {
        if (ready && pending === null) fitPresence(data.presence || {});
      }
    });
  }

  init();
})();
