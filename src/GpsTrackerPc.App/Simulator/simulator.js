(function () {
  "use strict";

  var mapError = document.getElementById("mapError");
  var mapErrorText = document.getElementById("mapErrorText");
  var routePoints = [];
  var markers = [];
  var selectedId = null;
  var playbackActive = false;
  var playbackMarker = null;
  var playbackSegments = [];
  var lastPlaybackTime = null;
  var trailDirty = false;
  var trailTimer = null;
  var lastTrailSetDataAt = 0;

  function showError(message) {
    mapError.hidden = false;
    mapErrorText.textContent = message || "";
  }

  function post(type, data) {
    if (!window.chrome || !window.chrome.webview) return;
    window.chrome.webview.postMessage(Object.assign({ type: type }, data || {}));
  }

  if (typeof maplibregl === "undefined") {
    showError("找不到 MapLibre 函式庫。");
    return;
  }

  var map;
  try {
    map = new maplibregl.Map({
      container: "map",
      style: {
        version: 8,
        sources: {
          esri: {
            type: "raster",
            tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"],
            tileSize: 256,
            maxzoom: 19,
            attribution: "Esri, Maxar, Earthstar Geographics, GIS User Community"
          }
        },
        layers: [{ id: "esri", type: "raster", source: "esri" }]
      },
      center: [121.564472, 25.033964],
      zoom: 15,
      maxZoom: 20,
      attributionControl: false,
      antialias: false,
      fadeDuration: 0,
      pixelRatio: Math.min(window.devicePixelRatio || 1, 2),
      maxTileCacheSize: 120
    });
  } catch (error) {
    showError(error.message);
    return;
  }

  window.__simMap = map;

  map.addControl(new maplibregl.NavigationControl({ visualizePitch: false }), "top-right");
  map.addControl(new maplibregl.AttributionControl({ compact: false }), "bottom-right");
  map.addControl(new maplibregl.ScaleControl({ unit: "metric" }), "bottom-left");

  function emptyRoute() {
    return { type: "FeatureCollection", features: [] };
  }

  function updateMarkerSelection() {
    markers.forEach(function (item) {
      if (item.id === selectedId) item.element.classList.add("selected");
      else item.element.classList.remove("selected");
    });
  }

  function renderMarkers(points) {
    markers.forEach(function (item) {
      item.marker.remove();
    });
    markers = [];

    points.forEach(function (point, index) {
      var element = document.createElement("button");
      element.type = "button";
      element.className = "wp-marker";
      element.textContent = String(index + 1);
      element.title = (point.name || ("路線點 " + (index + 1))) +
        (point.stop > 0 ? "（停留 " + point.stop + " 秒）" : "");

      element.addEventListener("click", function (event) {
        event.stopPropagation();
        selectedId = point.id;
        updateMarkerSelection();
        post("markerClick", { id: point.id });
      });

      var marker = new maplibregl.Marker({
        element: element,
        draggable: true,
        anchor: "center"
      }).setLngLat([point.lon, point.lat]).addTo(map);

      marker.on("dragend", function () {
        var lngLat = marker.getLngLat();
        post("markerMoved", { id: point.id, lat: lngLat.lat, lon: lngLat.lng });
      });

      markers.push({ id: point.id, marker: marker, element: element });
    });

    updateMarkerSelection();
  }

  function updatePlaybackSource() {
    var source = map.getSource("playback");
    if (!source) return;
    var features = playbackSegments
      .filter(function (segment) { return segment.length >= 2; })
      .map(function (segment) {
        return {
          type: "Feature",
          properties: {},
          geometry: { type: "LineString", coordinates: segment }
        };
      });
    source.setData({ type: "FeatureCollection", features: features });
  }

  // 播放軌跡最多每秒更新一次；其他時間只累積，避免大量 setData 造成 CPU 負擔。
  function scheduleTrailUpdate() {
    trailDirty = true;
    var now = performance.now();
    var wait = Math.max(0, 1000 - (now - lastTrailSetDataAt));
    if (wait === 0) {
      flushTrail();
      return;
    }

    if (!trailTimer) {
      trailTimer = setTimeout(function () {
        trailTimer = null;
        flushTrail();
      }, wait);
    }
  }

  function flushTrail() {
    if (!trailDirty) return;
    trailDirty = false;
    lastTrailSetDataAt = performance.now();
    updatePlaybackSource();
  }

  function showRouteMarkers() {
    markers.forEach(function (item) {
      item.element.style.display = "";
    });
  }

  function clearPlayback() {
    if (playbackMarker) {
      playbackMarker.remove();
      playbackMarker = null;
    }
    playbackSegments = [];
    lastPlaybackTime = null;
    document.getElementById("outageBadge").hidden = true;
    scheduleTrailUpdate();
  }

  function ensurePlaybackMarker(lat, lon) {
    if (!playbackMarker) {
      var element = document.createElement("div");
      element.className = "playback-marker";
      playbackMarker = new maplibregl.Marker({ element: element, anchor: "center" })
        .setLngLat([lon, lat])
        .addTo(map);
    } else if (Number.isFinite(lat) && Number.isFinite(lon)) {
      playbackMarker.setLngLat([lon, lat]);
    }
    return playbackMarker;
  }

  function startPlaybackView(message) {
    clearPlayback();
    playbackActive = true;
    markers.forEach(function (item) {
      item.element.style.display = "none";
    });
    if (Number.isFinite(message.lat) && Number.isFinite(message.lon)) {
      ensurePlaybackMarker(message.lat, message.lon);
    }
  }

  function appendPlaybackSample(message) {
    if (!Number.isFinite(message.lat) || !Number.isFinite(message.lon)) return;
    var point = [message.lon, message.lat];

    if (lastPlaybackTime === null || message.elapsed - lastPlaybackTime > 1.5) {
      playbackSegments.push([point]);
    } else {
      playbackSegments[playbackSegments.length - 1].push(point);
    }

    lastPlaybackTime = message.elapsed;
    scheduleTrailUpdate();
    updatePlaybackPosition(message);
  }

  function replacePlayback(message) {
    playbackSegments = (message.segments || [])
      .map(function (segment) {
        return (segment || [])
          .filter(function (point) {
            return Number.isFinite(point.lat) && Number.isFinite(point.lon);
          })
          .map(function (point) { return [point.lon, point.lat]; });
      })
      .filter(function (segment) { return segment.length > 0; });

    var lastPoint = null;
    var sourceSegments = message.segments || [];
    for (var index = sourceSegments.length - 1; index >= 0; index--) {
      var segment = sourceSegments[index] || [];
      if (segment.length) {
        lastPoint = segment[segment.length - 1];
        break;
      }
    }

    lastPlaybackTime = lastPoint && Number.isFinite(lastPoint.elapsed) ? lastPoint.elapsed : null;
    scheduleTrailUpdate();
    if (lastPoint) {
      updatePlaybackPosition(lastPoint);
    }
  }

  function updatePlaybackPosition(message) {
    if (!Number.isFinite(message.lat) || !Number.isFinite(message.lon)) return;
    var marker = ensurePlaybackMarker(message.lat, message.lon);
    var element = marker.getElement();
    element.classList.toggle("outage", !!message.outage);
    document.getElementById("outageBadge").hidden = !message.outage;
  }

  function endPlaybackView() {
    playbackActive = false;
    showRouteMarkers();
    document.getElementById("outageBadge").hidden = true;
    if (playbackMarker) {
      playbackMarker.getElement().classList.remove("outage");
    }
    scheduleTrailUpdate();
  }

  function setRoute(message) {
    clearPlayback();
    playbackActive = false;
    routePoints = message.points || [];
    if (Object.prototype.hasOwnProperty.call(message, "selectedId")) {
      selectedId = message.selectedId;
    }

    var coordinates = routePoints.map(function (point) {
      return [point.lon, point.lat];
    });
    if (message.loop && coordinates.length > 1) {
      coordinates.push([coordinates[0][0], coordinates[0][1]]);
    }

    var source = map.getSource("route");
    if (source) {
      source.setData(coordinates.length >= 2
        ? {
            type: "FeatureCollection",
            features: [{
              type: "Feature",
              properties: {},
              geometry: { type: "LineString", coordinates: coordinates }
            }]
          }
        : emptyRoute());
    }

    renderMarkers(routePoints);
  }

  function fitBounds() {
    if (!routePoints.length) return;
    if (routePoints.length === 1) {
      map.flyTo({ center: [routePoints[0].lon, routePoints[0].lat], zoom: 16, duration: 600 });
      return;
    }

    var lons = routePoints.map(function (point) { return point.lon; });
    var lats = routePoints.map(function (point) { return point.lat; });
    map.fitBounds(
      [[Math.min.apply(null, lons), Math.min.apply(null, lats)],
       [Math.max.apply(null, lons), Math.max.apply(null, lats)]],
      { padding: 60, maxZoom: 17, duration: 600 }
    );
  }

  map.on("load", function () {
    map.addSource("route", { type: "geojson", data: emptyRoute() });
    map.addLayer({
      id: "route-casing",
      type: "line",
      source: "route",
      layout: { "line-cap": "round", "line-join": "round" },
      paint: { "line-color": "#0b1220", "line-width": 8, "line-opacity": 0.8 }
    });
    map.addLayer({
      id: "route-line",
      type: "line",
      source: "route",
      layout: { "line-cap": "round", "line-join": "round" },
      paint: { "line-color": "#38bdf8", "line-width": 4 }
    });

    map.addSource("playback", { type: "geojson", data: { type: "FeatureCollection", features: [] } });
    map.addLayer({
      id: "playback-line",
      type: "line",
      source: "playback",
      layout: { "line-cap": "round", "line-join": "round" },
      paint: { "line-color": "#22c55e", "line-width": 5, "line-opacity": 0.95 }
    });

    map.on("click", function (event) {
      if (playbackActive) return;
      post("mapClick", { lat: event.lngLat.lat, lon: event.lngLat.lng });
    });

    map.on("moveend", function () {
      var center = map.getCenter();
      post("center", { lat: center.lat, lon: center.lng });
    });

    var center = map.getCenter();
    post("ready", { lat: center.lat, lon: center.lng });
  });

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", function (event) {
      var message = event.data;
      if (typeof message === "string") {
        try {
          message = JSON.parse(message);
        } catch (error) {
          return;
        }
      }
      if (!message || !message.type) return;

      switch (message.type) {
        case "setRoute":
          setRoute(message);
          break;
        case "flyTo":
          map.flyTo({
            center: [message.lon, message.lat],
            zoom: message.zoom || 16,
            duration: 700
          });
          break;
        case "fitBounds":
          fitBounds();
          break;
        case "selectMarker":
          selectedId = message.id;
          updateMarkerSelection();
          break;
        case "playbackReset":
          startPlaybackView(message);
          break;
        case "playbackSample":
          appendPlaybackSample(message);
          break;
        case "playbackBatch":
          (message.samples || []).forEach(appendPlaybackSample);
          break;
        case "playbackReplace":
          replacePlayback(message);
          break;
        case "playbackPosition":
          updatePlaybackPosition(message);
          break;
        case "playbackEnd":
          endPlaybackView();
          break;
        default:
          break;
      }
    });
  }
})();
