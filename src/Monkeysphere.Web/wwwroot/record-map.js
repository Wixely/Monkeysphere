const maps = new globalThis.Map();

function markerStyle(feature) {
    const count = feature.get('features').length;
    return new ol.style.Style({
        image: new ol.style.Circle({
            radius: count > 1 ? 13 : 8,
            fill: new ol.style.Fill({ color: count > 1 ? '#713703' : '#a95508' }),
            stroke: new ol.style.Stroke({ color: '#fffaf0', width: 3 })
        }),
        text: count > 1 ? new ol.style.Text({
            text: String(count),
            fill: new ol.style.Fill({ color: '#fffaf0' }),
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
    if (fit && features.length > 0) {
        state.map.getView().fit(state.source.getExtent(), { padding: [45, 45, 45, 45], maxZoom: 10, duration: 180 });
    }
}

export function create(element, callback, entries) {
    if (!globalThis.ol || maps.has(element)) {
        return;
    }

    const source = new ol.source.Vector();
    const clusters = new ol.source.Cluster({ distance: 48, minDistance: 12, source });
    const map = new ol.Map({
        target: element,
        layers: [
            new ol.layer.Graticule({
                strokeStyle: new ol.style.Stroke({ color: 'rgba(113, 55, 3, .35)', width: 1 }),
                showLabels: true,
                wrapX: true
            }),
            new ol.layer.Vector({ source: clusters, style: markerStyle })
        ],
        view: new ol.View({ center: ol.proj.fromLonLat([0, 20]), zoom: 2, minZoom: 1, maxZoom: 19 })
    });
    const state = { map, source };
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
