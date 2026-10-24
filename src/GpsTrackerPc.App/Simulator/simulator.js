(function () {
  "use strict";

  var mapError = document.getElementById("mapError");
  var mapErrorText = document.getElementById("mapErrorText");
  var attributionElement = document.getElementById("basemapAttribution");
  var routePoints = [];
  var routeLoop = false;
  var markers = [];
  var selectedId = null;
  var pointDrag = null;
  var pointDragFrame = null;
  var suppressMapClickUntil = 0;
  var ignoreNextMapClick = false;

  var playbackActive = false;
  var playbackMarker = null;
  var playbackSegments = [];
  var lastPlaybackTime = null;
  var lastPlaybackPosition = null;
  var lastPlaybackOutage = false;
  var lastConfirmedPoint = null;
  var currentLocationMarker = null;
  var currentLocationPosition = null;

  var trailDirty = false;
  var trailTimer = null;
  var lastTrailSetDataAt = 0;

  var currentBasemap = "simple";
  var styleGeneration = 0;

  var BASEMAP_ATTRIBUTION = {
    simple: "© OpenFreeMap · © OpenMapTiles · © OpenStreetMap contributors",
    satellite: "Esri, Maxar, Earthstar Geographics, GIS User Community",
    nlsc: "內政部國土測繪中心（NLSC）通用版電子地圖",
    none: "無底圖"
  };

  function showError(message) {
    mapError.hidden = false;
    mapErrorText.textContent = message || "";
  }

  function post(type, data) {
    if (!window.chrome || !window.chrome.webview) return;
    window.chrome.webview.postMessage(Object.assign({ type: type }, data || {}));
  }

  function rasterStyle(tiles, attribution) {
    return {
      version: 8,
      sources: {
        basemap: {
          type: "raster",
          tiles: [tiles],
          tileSize: 256,
          maxzoom: 19,
          attribution: attribution
        }
      },
      layers: [{ id: "basemap", type: "raster", source: "basemap" }]
    };
  }

  function styleFor(kind) {
    if (kind === "satellite") {
      return rasterStyle(
        "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
        BASEMAP_ATTRIBUTION.satellite
      );
    }

    if (kind === "nlsc") {
      return rasterStyle(
        "https://wmts.nlsc.gov.tw/wmts/EMAP/default/GoogleMapsCompatible/{z}/{y}/{x}",
        BASEMAP_ATTRIBUTION.nlsc
      );
    }

    if (kind === "none") {
      return {
        version: 8,
        sources: {},
        layers: [{
          id: "background",
          type: "background",
          paint: { "background-color": "#0b1220" }
        }]
      };
    }

    return "https://tiles.openfreemap.org/styles/positron";
  }

  function updateAttribution(kind) {
    if (!attributionElement) return;
    attributionElement.textContent = BASEMAP_ATTRIBUTION[kind] || BASEMAP_ATTRIBUTION.simple;
  }

  if (typeof maplibregl === "undefined") {
    showError("找不到 MapLibre 函式庫。");
    return;
  }

  var MapCore = window.SimulatorMapCore;
  if (!MapCore) {
    showError("找不到模擬地圖核心。");
    return;
  }

  var map;
  try {
    map = new maplibregl.Map({
      container: "map",
      style: styleFor("simple"),
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
  updateAttribution(currentBasemap);

  map.addControl(new maplibregl.NavigationControl({ visualizePitch: false }), "top-right");
  map.addControl(new maplibregl.ScaleControl({ unit: "metric" }), "bottom-left");

  function emptyRoute() {
    return MapCore.emptyFeatureCollection();
  }

  function routeFeature() {
    return MapCore.routeFeature(routePoints, routeLoop);
  }

  function updateRouteSource() {
    var source = map.getSource("route");
    if (source) source.setData(routeFeature());
  }

  function ensureCustomLayers() {
    MapCore.ensureCustomLayers(map);
  }

  function updateRoutePointsSource() {
    var source = map.getSource("route-points");
    if (source) source.setData(MapCore.routePointsFeature(routePoints, selectedId));
  }

  function setRoutePointsVisible(visible) {
    var value = visible ? "visible" : "none";
    if (map.getLayer("route-points-circles")) {
      map.setLayoutProperty("route-points-circles", "visibility", value);
    }
  }

  function updateMarkerSelection() {
    markers.forEach(function (item) {
      if (item.id === selectedId) item.element.classList.add("selected");
      else item.element.classList.remove("selected");
    });

    if (MapCore.isDenseRoute(routePoints)) {
      updateRoutePointsSource();
    }
  }

  function clearMarkers() {
    markers.forEach(function (item) {
      item.marker.remove();
    });
    markers = [];
  }

  function renderMarkers(points) {
    clearMarkers();
    updateRoutePointsSource();

    // 點數太多時不建立 DOM 標記：改成 GeoJSON 圓點圖層，只保留點選與拖曳目前點。
    if (MapCore.isDenseRoute(points)) {
      setRoutePointsVisible(!playbackActive);
      return;
    }

    setRoutePointsVisible(false);
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

  // 大量點位時用滑鼠拖曳選中的點：拖曳期間只更新圖層資料，放開才通知 C#。
  function schedulePointDragUpdate() {
    if (pointDragFrame !== null) return;
    pointDragFrame = window.requestAnimationFrame(function () {
      pointDragFrame = null;
      updateRoutePointsSource();
    });
  }

  function finishPointDrag() {
    if (!pointDrag) return;

    map.dragPan.enable();
    map.getCanvas().style.cursor = "";
    var drag = pointDrag;
    pointDrag = null;

    if (pointDragFrame !== null) {
      window.cancelAnimationFrame(pointDragFrame);
      pointDragFrame = null;
    }

    updateRoutePointsSource();
    if (!drag.moved) return;

    // 拖曳結束後的 click 不應該被當成「在地圖上新增路線點」。
    suppressMapClickUntil = performance.now() + 400;
    var point = routePoints[drag.index];
    if (point) {
      post("markerMoved", { id: point.id, lat: point.lat, lon: point.lon });
    }
  }

  window.addEventListener("mouseup", finishPointDrag);
  window.addEventListener("blur", finishPointDrag);

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

  function updatePlaybackTail() {
    var source = map.getSource("playback-tail");
    if (!source) return;

    if (!playbackActive ||
        !lastPlaybackPosition ||
        lastPlaybackOutage ||
        !lastConfirmedPoint) {
      source.setData(emptyRoute());
      return;
    }

    var current = [lastPlaybackPosition.lon, lastPlaybackPosition.lat];
    source.setData({
      type: "FeatureCollection",
      features: [{
        type: "Feature",
        properties: {},
        geometry: {
          type: "LineString",
          coordinates: [lastConfirmedPoint, current]
        }
      }]
    });
  }

  // 播放軌跡最多每秒更新一次；其他時間只累積，避免大量 setData。
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

    if (MapCore.isDenseRoute(routePoints)) {
      setRoutePointsVisible(true);
    }
  }

  function hideRouteMarkers() {
    markers.forEach(function (item) {
      item.element.style.display = "none";
    });

    if (MapCore.isDenseRoute(routePoints)) {
      setRoutePointsVisible(false);
    }
  }

  function clearPlayback() {
    if (playbackMarker) {
      playbackMarker.remove();
      playbackMarker = null;
    }
    playbackSegments = [];
    lastPlaybackTime = null;
    lastPlaybackPosition = null;
    lastPlaybackOutage = false;
    lastConfirmedPoint = null;
    document.getElementById("outageBadge").hidden = true;
    scheduleTrailUpdate();
    updatePlaybackTail();
  }

  function ensurePlaybackMarker(lat, lon) {
    if (!playbackMarker) {
      var element = document.createElement("div");
      element.className = "playback-marker";
      var pin = document.createElement("div");
      pin.className = "playback-pin";
      element.appendChild(pin);
      playbackMarker = new maplibregl.Marker({ element: element, anchor: "bottom" })
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
    hideRouteMarkers();
    if (Number.isFinite(message.lat) && Number.isFinite(message.lon)) {
      lastPlaybackPosition = { lat: message.lat, lon: message.lon };
      ensurePlaybackMarker(message.lat, message.lon);
    }
    updatePlaybackTail();
  }

  function appendPlaybackSample(message) {
    if (!Number.isFinite(message.lat) || !Number.isFinite(message.lon)) return;
    var point = [message.lon, message.lat];
    var lastSegment = playbackSegments[playbackSegments.length - 1];
    var lastPoint = lastSegment && lastSegment.length
      ? lastSegment[lastSegment.length - 1]
      : null;

    if (lastPoint && lastPoint[0] === point[0] && lastPoint[1] === point[1]) {
      lastPlaybackTime = message.elapsed;
      updatePlaybackPosition(message);
      return;
    }

    if (lastPlaybackTime === null || message.elapsed - lastPlaybackTime > 1.5) {
      playbackSegments.push([point]);
    } else {
      playbackSegments[playbackSegments.length - 1].push(point);
    }

    lastPlaybackTime = message.elapsed;
    lastConfirmedPoint = point;
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

    var sourceSegments = message.segments || [];
    var lastPoint = null;
    for (var index = sourceSegments.length - 1; index >= 0; index--) {
      var segment = sourceSegments[index] || [];
      if (segment.length) {
        lastPoint = segment[segment.length - 1];
        break;
      }
    }

    if (lastPoint) {
      lastPlaybackTime = Number.isFinite(lastPoint.elapsed) ? lastPoint.elapsed : null;
      lastConfirmedPoint = [lastPoint.lon, lastPoint.lat];
      updatePlaybackPosition({
        lat: lastPoint.lat,
        lon: lastPoint.lon,
        outage: false
      });
    }

    scheduleTrailUpdate();
    updatePlaybackTail();
  }

  function updatePlaybackPosition(message) {
    if (!Number.isFinite(message.lat) || !Number.isFinite(message.lon)) return;
    lastPlaybackPosition = { lat: message.lat, lon: message.lon };
    lastPlaybackOutage = !!message.outage;

    var marker = ensurePlaybackMarker(message.lat, message.lon);
    marker.getElement().classList.toggle("outage", lastPlaybackOutage);
    document.getElementById("outageBadge").hidden = !lastPlaybackOutage;
    updatePlaybackTail();
  }

  function endPlaybackView() {
    playbackActive = false;
    showRouteMarkers();
    document.getElementById("outageBadge").hidden = true;
    if (playbackMarker) {
      playbackMarker.getElement().classList.remove("outage");
    }
    scheduleTrailUpdate();
    updatePlaybackTail();
  }

  function showCurrentLocation(message) {
    if (!Number.isFinite(message.lat) || !Number.isFinite(message.lon)) return;

    currentLocationPosition = { lat: message.lat, lon: message.lon };
    if (currentLocationMarker) {
      currentLocationMarker.remove();
      currentLocationMarker = null;
    }

    var element = document.createElement("div");
    element.className = "current-location-marker";
    element.title = "目前位置（Windows 定位）";
    var pin = document.createElement("div");
    pin.className = "current-location-pin";
    element.appendChild(pin);
    currentLocationMarker = new maplibregl.Marker({ element: element, anchor: "bottom" })
      .setLngLat([message.lon, message.lat])
      .addTo(map);

    if (message.center) {
      map.panTo([message.lon, message.lat], { duration: 400 });
    }
  }

  function restoreCustomLayers() {
    ensureCustomLayers();
    updateRouteSource();
    updatePlaybackSource();
    renderMarkers(routePoints);

    if (playbackActive) {
      hideRouteMarkers();
      if (playbackMarker) {
        playbackMarker.remove();
        playbackMarker = null;
      }
      if (lastPlaybackPosition) {
        ensurePlaybackMarker(lastPlaybackPosition.lat, lastPlaybackPosition.lon);
      }
    } else {
      showRouteMarkers();
    }

    if (currentLocationPosition) {
      showCurrentLocation({
        lat: currentLocationPosition.lat,
        lon: currentLocationPosition.lon,
        center: false
      });
    }

    updatePlaybackTail();
  }

  function setRoute(message) {
    clearPlayback();
    playbackActive = false;
    routePoints = message.points || [];
    routeLoop = !!message.loop;

    if (Object.prototype.hasOwnProperty.call(message, "selectedId")) {
      selectedId = message.selectedId;
    }

    updateRouteSource();
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

  function sendViewport() {
    var bounds = map.getBounds();
    if (!bounds) return;
    post("viewport", {
      west: bounds.getWest(),
      south: bounds.getSouth(),
      east: bounds.getEast(),
      north: bounds.getNorth()
    });
  }

  function applyBasemap(kind) {
    if (kind === currentBasemap && map.loaded()) {
      updateAttribution(kind);
      return;
    }

    currentBasemap = kind;
    updateAttribution(kind);
    var generation = ++styleGeneration;
    map.setStyle(styleFor(kind), { diff: false });
    map.once("style.load", function () {
      if (generation !== styleGeneration) return;
      restoreCustomLayers();
      sendViewport();
    });
  }

  function initializeMap() {
    ensureCustomLayers();
    updateAttribution(currentBasemap);

    // 大量點位的圓點圖層要自己處理點選與拖曳；註冊順序要在一般地圖 click 之前。
    map.on("click", "route-points-circles", function (event) {
      if (playbackActive || performance.now() < suppressMapClickUntil) return;
      var feature = event.features && event.features[0];
      if (!feature) return;

      ignoreNextMapClick = true;
      selectedId = feature.properties.id;
      updateMarkerSelection();
      post("markerClick", { id: selectedId });
    });

    map.on("mousedown", "route-points-circles", function (event) {
      if (playbackActive) return;
      var feature = event.features && event.features[0];
      if (!feature) return;

      var index = Number(feature.properties.index);
      if (!Number.isInteger(index) || !routePoints[index]) return;

      pointDrag = {
        index: index,
        startX: event.point.x,
        startY: event.point.y,
        moved: false
      };
      map.dragPan.disable();
      map.getCanvas().style.cursor = "grabbing";
      event.preventDefault();
    });

    map.on("mousemove", "route-points-circles", function () {
      if (!pointDrag) map.getCanvas().style.cursor = "grab";
    });

    map.on("mouseleave", "route-points-circles", function () {
      if (!pointDrag) map.getCanvas().style.cursor = "";
    });

    map.on("mousemove", function (event) {
      if (!pointDrag) return;

      if (!pointDrag.moved) {
        if (Math.abs(event.point.x - pointDrag.startX) +
            Math.abs(event.point.y - pointDrag.startY) < 4) {
          return;
        }

        pointDrag.moved = true;
      }

      var point = routePoints[pointDrag.index];
      if (!point) return;
      point.lon = event.lngLat.lng;
      point.lat = event.lngLat.lat;
      schedulePointDragUpdate();
    });

    map.on("click", function (event) {
      if (playbackActive) return;

      if (ignoreNextMapClick) {
        ignoreNextMapClick = false;
        return;
      }

      if (performance.now() < suppressMapClickUntil) return;
      post("mapClick", { lat: event.lngLat.lat, lon: event.lngLat.lng });
    });

    map.on("dragstart", function () {
      // 使用者自己拖曳地圖就暫停跟隨；按「跟隨」按鈕才會回到大頭針。
      post("followPaused");
    });

    map.on("moveend", function () {
      var center = map.getCenter();
      post("center", { lat: center.lat, lon: center.lng });
      sendViewport();
    });

    map.on("resize", sendViewport);

    var center = map.getCenter();
    sendViewport();
    post("ready", { lat: center.lat, lon: center.lng });
  }

  map.on("load", initializeMap);

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
        case "setBasemap":
          applyBasemap(message.basemap || "simple");
          break;
        case "followPan":
          if (Number.isFinite(message.lat) && Number.isFinite(message.lon)) {
            map.panTo([message.lon, message.lat], { duration: 250 });
          }
          break;
        case "locatePosition":
          if (Number.isFinite(message.lat) && Number.isFinite(message.lon)) {
            map.panTo([message.lon, message.lat], { duration: 300 });
          }
          break;
        case "showCurrentLocation":
          showCurrentLocation(message);
          break;
        default:
          break;
      }
    });
  }
})();
