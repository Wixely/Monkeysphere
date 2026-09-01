const editors = new globalThis.Map();

function themeColor(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function pinStyle() {
    return new ol.style.Style({
        image: new ol.style.Circle({
            radius: 8,
            fill: new ol.style.Fill({ color: themeColor('--accent', '#a95508') }),
            stroke: new ol.style.Stroke({ color: themeColor('--panel', '#fffaf0'), width: 3 })
        })
    });
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

export function create(element, callback, latitude, longitude) {
    if (!globalThis.ol || editors.has(element)) {
        return;
    }

    const hasPin = Number.isFinite(latitude) && Number.isFinite(longitude);
    const feature = new ol.Feature();
    feature.setStyle(pinStyle());
    if (hasPin) {
        feature.setGeometry(new ol.geom.Point(ol.proj.fromLonLat([longitude, latitude])));
    }

    const source = new ol.source.Vector({ features: [feature] });
    const map = new ol.Map({
        target: element,
        layers: [
            graticule(),
            new ol.layer.Vector({ source })
        ],
        view: new ol.View({
            center: hasPin ? ol.proj.fromLonLat([longitude, latitude]) : ol.proj.fromLonLat([0, 20]),
            zoom: hasPin ? 7 : 2,
            minZoom: 1,
            maxZoom: 19
        })
    });
    map.on('singleclick', event => {
        const coordinates = ol.proj.toLonLat(event.coordinate);
        const pickedLongitude = Math.max(-180, Math.min(180, coordinates[0]));
        const pickedLatitude = Math.max(-90, Math.min(90, coordinates[1]));
        feature.setGeometry(new ol.geom.Point(event.coordinate));
        callback.invokeMethodAsync('PinChanged', pickedLatitude, pickedLongitude);
    });
    editors.set(element, { map, feature });
}

export function update(element, latitude, longitude) {
    const editor = editors.get(element);
    if (!editor) {
        return;
    }

    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) {
        editor.feature.setGeometry(undefined);
        return;
    }

    const coordinate = ol.proj.fromLonLat([longitude, latitude]);
    editor.feature.setGeometry(new ol.geom.Point(coordinate));
}

export function dispose(element) {
    const editor = editors.get(element);
    if (!editor) {
        return;
    }

    editor.map.setTarget(undefined);
    editors.delete(element);
}

globalThis.addEventListener('monkeysphere:themechanged', () => {
    for (const editor of editors.values()) {
        editor.feature.setStyle(pinStyle());
        editor.map.getLayers().setAt(0, graticule());
    }
});
