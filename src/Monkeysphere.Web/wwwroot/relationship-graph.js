const graphs = new globalThis.Map();
const minimumNodeDistance = 130;

function themeColor(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function graphStyles() {
    const ink = themeColor('--ink', '#2d2418');
    const panel = themeColor('--panel', '#fffaf0');
    const node = themeColor('--graph-node', '#a95508');
    const focusedNode = themeColor('--graph-node-focus', '#713703');
    const edge = themeColor('--graph-edge', '#c78a45');
    const edgeLabel = themeColor('--graph-label', '#713703');
    const selected = themeColor('--accent-bright', '#e9a23b');

    return [
        {
            selector: 'node',
            style: {
                'background-color': node,
                'border-color': panel,
                'border-width': 3,
                'label': 'data(label)',
                'color': ink,
                'font-size': 12,
                'font-weight': 600,
                'text-wrap': 'wrap',
                'text-max-width': 110,
                'text-valign': 'bottom',
                'text-margin-y': 8,
                'active-bg-opacity': 0,
                'overlay-opacity': 0,
                'width': 30,
                'height': 30
            }
        },
        {
            selector: 'node[distance = 0]',
            style: { 'background-color': focusedNode, 'width': 42, 'height': 42 }
        },
        {
            selector: 'node[imageUrl]',
            style: {
                'background-image': 'data(imageUrl)',
                'background-fit': 'cover',
                'background-clip': 'node',
                'width': 46,
                'height': 46
            }
        },
        {
            selector: 'node[imageUrl][distance = 0]',
            style: { 'width': 54, 'height': 54 }
        },
        {
            selector: 'node:selected',
            style: { 'border-color': selected, 'border-width': 6 }
        },
        {
            selector: 'edge',
            style: {
                'curve-style': 'bezier',
                'line-color': edge,
                'width': 2,
                'label': 'data(label)',
                'font-size': 9,
                'color': edgeLabel,
                'text-background-color': panel,
                'text-background-opacity': 0.85,
                'text-background-padding': 2,
                'target-arrow-color': node,
                'target-arrow-shape': 'none'
            }
        },
        {
            selector: 'edge[?directional]',
            style: { 'target-arrow-shape': 'triangle' }
        }
    ];
}

function positionMap(positions) {
    const result = new globalThis.Map();
    (positions || []).forEach(position => {
        if (position?.recordId && Number.isFinite(position.x) && Number.isFinite(position.y)) {
            result.set(position.recordId, { x: position.x, y: position.y });
        }
    });
    return result;
}

function elements(graph, positions) {
    const saved = positionMap(positions);
    return [
        ...graph.nodes.map(node => ({
            group: 'nodes',
            ...(saved.has(node.recordId) ? { position: saved.get(node.recordId) } : {}),
            data: {
                id: node.recordId,
                recordId: node.recordId,
                label: node.displayName,
                type: node.recordTypeName,
                distance: node.distance,
                ...(node.imageId
                    ? { imageUrl: `/records/${encodeURIComponent(node.recordId)}/images/${encodeURIComponent(node.imageId)}/thumbnail` }
                    : {})
            }
        })),
        ...graph.edges.map(edge => ({
            group: 'edges',
            data: {
                id: edge.relationshipId,
                source: edge.sourceRecordId,
                target: edge.targetRecordId,
                label: edge.label,
                directional: edge.directionality === 0
            }
        }))
    ];
}

function rebuildBadges(layer, graph, badgeState) {
    layer.replaceChildren();
    badgeState.elements.clear();
    graph.nodes.forEach(node => {
        if (!node.recordTypeSymbol) {
            return;
        }

        const badge = document.createElement('span');
        badge.className = 'graph-type-badge';
        badge.textContent = node.recordTypeSymbol;
        badge.title = node.recordTypeName;
        layer.appendChild(badge);
        badgeState.elements.set(node.recordId, badge);
    });
}

function positionBadges(cy, badgeState) {
    badgeState.elements.forEach((badge, recordId) => {
        const node = cy.getElementById(recordId);
        if (!node.length || !node.visible()) {
            badge.hidden = true;
            return;
        }

        const position = node.renderedPosition();
        const nodeSize = Math.max(node.renderedWidth(), node.renderedHeight());
        const offset = nodeSize * 0.32;
        const badgeSize = Math.min(38, Math.max(22, nodeSize * 0.42));
        badge.hidden = false;
        badge.style.width = `${badgeSize}px`;
        badge.style.height = `${badgeSize}px`;
        badge.style.fontSize = `${badgeSize * 0.55}px`;
        badge.style.left = `${position.x + offset}px`;
        badge.style.top = `${position.y - offset}px`;
    });
}

function queueBadgePositions(cy, badgeState) {
    cancelAnimationFrame(badgeState.frame);
    badgeState.frame = requestAnimationFrame(() => positionBadges(cy, badgeState));
}

function capturePositionMap(cy) {
    const result = new globalThis.Map();
    cy.nodes().forEach(node => {
        const position = node.position();
        if (Number.isFinite(position.x) && Number.isFinite(position.y)) {
            result.set(node.id(), { x: position.x, y: position.y });
        }
    });
    return result;
}

function isPositionFree(position, occupied) {
    const minimumSquared = minimumNodeDistance * minimumNodeDistance;
    return occupied.every(other => {
        const dx = position.x - other.x;
        const dy = position.y - other.y;
        return (dx * dx) + (dy * dy) >= minimumSquared;
    });
}

function angleOffset(id) {
    let hash = 2166136261;
    for (let index = 0; index < id.length; index++) {
        hash ^= id.charCodeAt(index);
        hash = Math.imul(hash, 16777619);
    }
    return ((hash >>> 0) % 360) * Math.PI / 180;
}

function findFreePosition(origin, occupied, id) {
    if (isPositionFree(origin, occupied)) {
        return origin;
    }

    const offset = angleOffset(id);
    for (let ring = 1; ring <= 80; ring++) {
        const candidates = Math.max(12, ring * 12);
        const radius = minimumNodeDistance * ring;
        for (let index = 0; index < candidates; index++) {
            const angle = offset + (index * Math.PI * 2 / candidates);
            const candidate = {
                x: origin.x + Math.cos(angle) * radius,
                y: origin.y + Math.sin(angle) * radius
            };
            if (isPositionFree(candidate, occupied)) {
                return candidate;
            }
        }
    }

    return { x: origin.x + occupied.length * minimumNodeDistance, y: origin.y };
}

function averagePosition(positions) {
    if (!positions.length) {
        return { x: 0, y: 0 };
    }

    return {
        x: positions.reduce((sum, position) => sum + position.x, 0) / positions.length,
        y: positions.reduce((sum, position) => sum + position.y, 0) / positions.length
    };
}

function normalizePositions(nodes, fixedPositions) {
    const occupied = [];
    const placed = new globalThis.Map();
    const pending = [];

    nodes.forEach(node => {
        const preferred = fixedPositions.get(node.id());
        if (!preferred) {
            pending.push(node);
            return;
        }

        const position = findFreePosition(preferred, occupied, node.id());
        node.position(position);
        occupied.push(position);
        placed.set(node.id(), position);
    });

    pending.forEach(node => {
        const neighbours = node.neighborhood('node').toArray()
            .map(neighbour => placed.get(neighbour.id()))
            .filter(Boolean);
        const origin = neighbours.length ? averagePosition(neighbours) : averagePosition(occupied);
        const position = findFreePosition(origin, occupied, node.id());
        node.position(position);
        occupied.push(position);
        placed.set(node.id(), position);
    });
}

function runLayout(cy, savedPositions, preservedPositions, badgeState) {
    const fixedPositions = positionMap(savedPositions);
    preservedPositions.forEach((position, id) => fixedPositions.set(id, position));
    const nodes = cy.nodes().toArray().sort((left, right) => left.id().localeCompare(right.id()));
    const hasDisplayedFixedPosition = nodes.some(node => fixedPositions.has(node.id()));

    if (!hasDisplayedFixedPosition) {
        cy.elements().layout({
            name: 'cose',
            animate: false,
            fit: false,
            padding: 36,
            nodeRepulsion: () => 12000,
            idealEdgeLength: () => 150,
            randomize: true
        }).run();
        normalizePositions(nodes, capturePositionMap(cy));
    } else {
        normalizePositions(nodes, fixedPositions);
    }

    cy.fit(cy.elements(), 36);
    queueBadgePositions(cy, badgeState);
}

function separateDraggedNode(cy, node) {
    const occupied = cy.nodes().toArray()
        .filter(other => other.id() !== node.id())
        .map(other => other.position());
    const current = node.position();
    const position = findFreePosition(current, occupied, node.id());
    if (position.x !== current.x || position.y !== current.y) {
        node.animate({ position }, { duration: 180, easing: 'ease-out' });
    }
}

function hideRecordMenu(menu) {
    menu.hidden = true;
}

function showRecordMenu(element, menu, node, renderedPosition) {
    const recordId = node.data('recordId');
    if (!recordId) {
        return;
    }

    const link = menu.querySelector('a');
    link.href = `/records/${encodeURIComponent(recordId)}`;
    link.textContent = `View ${node.data('label') || 'record'}`;
    menu.hidden = false;

    const menuWidth = menu.offsetWidth;
    const menuHeight = menu.offsetHeight;
    const x = Math.min(Math.max(8, renderedPosition.x), Math.max(8, element.clientWidth - menuWidth - 8));
    const y = Math.min(Math.max(8, renderedPosition.y), Math.max(8, element.clientHeight - menuHeight - 8));
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;
    link.focus({ preventScroll: true });
}

export function create(element, callback, graph, savedPositions) {
    if (!globalThis.cytoscape || graphs.has(element)) {
        return;
    }

    const cy = cytoscape({
        container: element,
        elements: elements(graph, savedPositions),
        layout: { name: 'preset', fit: false },
        minZoom: 0.15,
        maxZoom: 3,
        style: graphStyles()
    });
    const shell = element.parentElement;
    const menu = shell.querySelector('.graph-record-menu');
    const badgeLayer = shell.querySelector('.graph-type-badges');
    const badgeState = { elements: new globalThis.Map(), frame: undefined };
    rebuildBadges(badgeLayer, graph, badgeState);
    cy.on('render', () => queueBadgePositions(cy, badgeState));
    cy.on('tap', 'node', event => {
        const recordId = event.target.data('recordId');
        if (recordId) {
            callback.invokeMethodAsync('NodeSelected', recordId);
        }
    });
    cy.on('cxttap', 'node', event => {
        const node = event.target;
        if (!node.data('recordId')) {
            return;
        }

        event.originalEvent?.preventDefault();
        cy.nodes().unselect();
        node.select();
        showRecordMenu(element, menu, node, event.renderedPosition);
    });
    cy.on('tap pan zoom drag', () => hideRecordMenu(menu));
    cy.on('dragfree', 'node', event => separateDraggedNode(cy, event.target));

    const suppressContextMenu = event => event.preventDefault();
    const dismissMenu = event => {
        if (!shell.contains(event.target)) {
            hideRecordMenu(menu);
        }
    };
    const handleKeyDown = event => {
        if (event.key === 'Escape') {
            hideRecordMenu(menu);
            element.focus();
            return;
        }
        if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) {
            return;
        }

        const selected = cy.nodes(':selected').filter(item => item.data('recordId'));
        const node = selected.length
            ? selected.first()
            : cy.nodes().filter(item => item.data('recordId')).first();
        if (!node?.length) {
            return;
        }

        event.preventDefault();
        showRecordMenu(element, menu, node, node.renderedPosition());
    };
    element.addEventListener('contextmenu', suppressContextMenu);
    element.addEventListener('keydown', handleKeyDown);
    document.addEventListener('pointerdown', dismissMenu);
    let resizeFrame;
    const observer = new ResizeObserver(() => {
        cancelAnimationFrame(resizeFrame);
        resizeFrame = requestAnimationFrame(() => {
            cy.resize();
            cy.fit(cy.elements(), 36);
        });
    });
    observer.observe(element);
    graphs.set(element, { cy, observer, suppressContextMenu, handleKeyDown, dismissMenu, badgeLayer, badgeState });
    runLayout(cy, savedPositions, new globalThis.Map(), badgeState);
}

export function update(element, graph, savedPositions) {
    const instance = graphs.get(element);
    if (!instance) {
        return;
    }

    const { cy, badgeLayer, badgeState } = instance;
    const preservedPositions = capturePositionMap(cy);
    cy.elements().remove();
    cy.add(elements(graph, savedPositions));
    rebuildBadges(badgeLayer, graph, badgeState);
    runLayout(cy, savedPositions, preservedPositions, badgeState);
}

export function getPositions(element) {
    const graph = graphs.get(element);
    if (!graph) {
        return [];
    }

    const nodes = graph.cy.nodes().toArray().sort((left, right) => left.id().localeCompare(right.id()));
    normalizePositions(nodes, capturePositionMap(graph.cy));
    queueBadgePositions(graph.cy, graph.badgeState);
    return nodes
        .map(node => {
            const position = node.position();
            return {
                recordId: node.id(),
                x: Math.round(position.x * 1000) / 1000,
                y: Math.round(position.y * 1000) / 1000
            };
        });
}

export function centerOn(element, recordId) {
    const cy = graphs.get(element)?.cy;
    const node = cy?.getElementById(recordId);
    if (!cy || !node?.length) {
        return;
    }

    node.select();
    cy.animate({
        center: { eles: node },
        zoom: Math.max(cy.zoom(), 1.15)
    }, {
        duration: 280,
        easing: 'ease-out'
    });
}

export function dispose(element) {
    const graph = graphs.get(element);
    if (graph) {
        cancelAnimationFrame(graph.badgeState.frame);
        graph.observer.disconnect();
        element.removeEventListener('contextmenu', graph.suppressContextMenu);
        element.removeEventListener('keydown', graph.handleKeyDown);
        document.removeEventListener('pointerdown', graph.dismissMenu);
        graph.cy.destroy();
        graphs.delete(element);
    }
}

globalThis.addEventListener('monkeysphere:themechanged', () => {
    graphs.forEach(graph => graph.cy.style().fromJson(graphStyles()).update());
});
