const graphs = new globalThis.Map();

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

function elements(graph) {
    return [
        ...graph.nodes.map(node => ({
            group: 'nodes',
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

function runLayout(cy, badgeState) {
    cy.elements().layout({
        name: 'cose',
        animate: false,
        fit: false,
        padding: 36,
        nodeRepulsion: () => 7000,
        idealEdgeLength: () => 100,
        randomize: true
    }).run();
    cy.fit(cy.elements(), 36);
    queueBadgePositions(cy, badgeState);
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

export function create(element, callback, graph) {
    if (!globalThis.cytoscape || graphs.has(element)) {
        return;
    }

    const cy = cytoscape({
        container: element,
        elements: elements(graph),
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
    runLayout(cy, badgeState);
}

export function update(element, graph) {
    const instance = graphs.get(element);
    if (!instance) {
        return;
    }

    const { cy, badgeLayer, badgeState } = instance;
    cy.elements().remove();
    cy.add(elements(graph));
    rebuildBadges(badgeLayer, graph, badgeState);
    runLayout(cy, badgeState);
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
