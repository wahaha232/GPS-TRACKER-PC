(function () {
  "use strict";

  var mapError = document.getElementById("mapError");
  var mapErrorText = document.getElementById("mapErrorText");
  var routePoints = [];
  var markers = [];
  var selectedId = null;

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
      attributionControl: false
    });
  } catch (error) {
    showError(error.message);
    return;
  }

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

  function setRoute(message) {
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

    map.on("click", function (event) {
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
        default:
          break;
      }
    });
  }
})();
