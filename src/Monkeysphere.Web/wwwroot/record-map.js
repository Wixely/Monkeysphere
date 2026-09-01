const maps = new globalThis.Map();

function themeColor(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function graticule() {
    const labelStyle = new ol.style.Text({
        font: '12px system-ui',
        fill: new ol.style.Fill({ color: themeColor('--ink-soft', '#4c3d2a') }),
        stroke: new ol.style.Stroke({ color: themeColor('--panel', '#fffaf0'), width: 3 })
    });
    return new ol.layer.Graticule({
        strokeStyle: new ol.style.Stroke({ color: themeColor('--map-grid', 'rgba(113, 55, 3, .35)'), width: 1 }),
        lonLabelStyle: labelStyle,
        latLabelStyle: labelStyle,
        showLabels: true,
        wrapX: true
    });
}

function radiusStyle() {
    return new ol.style.Style({
        fill: new ol.style.Fill({ color: themeColor('--map-radius-fill', 'rgba(221, 132, 20, .13)') }),
        stroke: new ol.style.Stroke({
            color: themeColor('--map-radius-stroke', 'rgba(169, 85, 8, .65)'),
            width: 2,
            lineDash: [6, 5]
        })
    });
}

function markerStyle(feature) {
    const count = feature.get('features').length;
    return new ol.style.Style({
        image: new ol.style.Circle({
            radius: count > 1 ? 13 : 8,
            fill: new ol.style.Fill({
                color: count > 1
                    ? themeColor('--accent-dark', '#713703')
                    : themeColor('--accent', '#a95508')
            }),
            stroke: new ol.style.Stroke({ color: themeColor('--panel', '#fffaf0'), width: 3 })
        }),
        text: count > 1 ? new ol.style.Text({
            text: String(count),
            fill: new ol.style.Fill({ color: themeColor('--panel', '#fffaf0') }),
            font: '600 12px system-ui'
        }) : undefined
    });
}

function replaceEntries(state, entries, fit) {
    const features = entries.map(entry => new ol.Feature({
        geometry: new ol.geom.Point(ol.proj.fromLonLat([entry.longitude, entry.latitude])),
        fieldValueId: entry.fieldValueId
    }));
    state.source.clear();
    state.source.addFeatures(features);
    state.radiusSource.clear();
    state.radiusSource.addFeatures(entries
        .filter(entry => Number.isFinite(entry.approximationRadiusKilometres) && entry.approximationRadiusKilometres > 0)
        .map(entry => new ol.Feature({
            geometry: ol.geom.Polygon
                .circular([entry.longitude, entry.latitude], entry.approximationRadiusKilometres * 1000, 64)
                .transform('EPSG:4326', 'EPSG:3857')
        })));
    if (fit && features.length > 0) {
        state.map.getView().fit(state.source.getExtent(), { padding: [45, 45, 45, 45], maxZoom: 10, duration: 180 });
    }
}

export function create(element, callback, entries) {
    if (!globalThis.ol || maps.has(element)) {
        return;
    }

    const source = new ol.source.Vector();
    const radiusSource = new ol.source.Vector();
    const clusters = new ol.source.Cluster({ distance: 48, minDistance: 12, source });
    const map = new ol.Map({
        target: element,
        layers: [
            graticule(),
            new ol.layer.Vector({
                source: radiusSource,
                style: radiusStyle()
            }),
            new ol.layer.Vector({ source: clusters, style: markerStyle })
        ],
        view: new ol.View({ center: ol.proj.fromLonLat([0, 20]), zoom: 2, minZoom: 1, maxZoom: 19 })
    });
    const state = { map, source, radiusSource };
    map.on('singleclick', event => {
        const cluster = map.forEachFeatureAtPixel(event.pixel, feature => feature);
        if (!cluster) {
            callback.invokeMethodAsync('ClusterSelected', []);
            return;
        }

        const ids = cluster.get('features').map(feature => feature.get('fieldValueId'));
        callback.invokeMethodAsync('ClusterSelected', ids);
    });
    maps.set(element, state);
    replaceEntries(state, entries, true);
}

export function update(element, entries) {
    const state = maps.get(element);
    if (state) {
        replaceEntries(state, entries, true);
    }
}

export function dispose(element) {
    const state = maps.get(element);
    if (!state) {
        return;
    }

    state.map.setTarget(undefined);
    maps.delete(element);
}

globalThis.addEventListener('monkeysphere:themechanged', () => {
    for (const state of maps.values()) {
        state.map.getLayers().setAt(0, graticule());
        state.map.getLayers().item(1).setStyle(radiusStyle());
        state.map.getLayers().item(2).setStyle(markerStyle);
    }
});
