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
            selector: 'core',
            style: {
                'selection-box-color': selected,
                'selection-box-border-color': selected,
                'selection-box-opacity': 0.16,
                'selection-box-border-width': 2
            }
        },
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
            selectable: false,
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

function applyViewport(cy, viewport) {
    if (!viewport ||
        !Number.isFinite(viewport.panX) ||
        !Number.isFinite(viewport.panY) ||
        !Number.isFinite(viewport.zoom)) {
        return false;
    }

    cy.viewport({
        pan: { x: viewport.panX, y: viewport.panY },
        zoom: Math.min(cy.maxZoom(), Math.max(cy.minZoom(), viewport.zoom))
    });
    return true;
}

function currentViewport(cy) {
    const pan = cy.pan();
    return {
        panX: Math.round(pan.x * 1000) / 1000,
        panY: Math.round(pan.y * 1000) / 1000,
        zoom: Math.round(cy.zoom() * 1000000) / 1000000
    };
}

function runLayout(cy, savedPositions, preservedPositions, badgeState, viewport) {
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

    if (!applyViewport(cy, viewport)) {
        cy.fit(cy.elements(), 36);
    }
    queueBadgePositions(cy, badgeState);
}

function isGroupPositionFree(nodes, occupied, offset) {
    return nodes.every(node => {
        const position = node.position();
        return isPositionFree({ x: position.x + offset.x, y: position.y + offset.y }, occupied);
    });
}

function findFreeGroupOffset(nodes, occupied, id) {
    const origin = { x: 0, y: 0 };
    if (isGroupPositionFree(nodes, occupied, origin)) {
        return origin;
    }

    const angle = angleOffset(id);
    for (let ring = 1; ring <= 80; ring++) {
        const candidates = Math.max(12, ring * 12);
        const radius = minimumNodeDistance * ring;
        for (let index = 0; index < candidates; index++) {
            const candidateAngle = angle + (index * Math.PI * 2 / candidates);
            const candidate = {
                x: Math.cos(candidateAngle) * radius,
                y: Math.sin(candidateAngle) * radius
            };
            if (isGroupPositionFree(nodes, occupied, candidate)) {
                return candidate;
            }
        }
    }

    return { x: occupied.length * minimumNodeDistance, y: 0 };
}

function separateDraggedNodes(cy, draggedNode) {
    const selected = cy.nodes(':selected').toArray();
    const moved = draggedNode.selected() && selected.length > 1 ? selected : [draggedNode];
    const movedIds = new Set(moved.map(node => node.id()));
    const occupied = cy.nodes().toArray()
        .filter(node => !movedIds.has(node.id()))
        .map(node => node.position());
    const offset = findFreeGroupOffset(moved, occupied, draggedNode.id());
    if (offset.x !== 0 || offset.y !== 0) {
        moved.forEach(node => {
            const position = node.position();
            node.animate({
                position: { x: position.x + offset.x, y: position.y + offset.y }
            }, { duration: 180, easing: 'ease-out' });
        });
    }
}

function updateSelectionSummary(cy, summary) {
    const count = cy.nodes(':selected').length;
    summary.hidden = count < 2;
    summary.textContent = count < 2
        ? ''
        : `${count} records selected · Drag any selected record to move the group.`;
}

function isAdditiveSelection(event) {
    const original = event.originalEvent;
    return Boolean(original?.ctrlKey || original?.metaKey || original?.shiftKey);
}

// The menu itself is rendered by the component rather than built here. It carries record types,
// tags, buttons and a dropdown, which is a user interface rather than a bit of canvas decoration,
// and reimplementing focus order and labelling for it in this file would be building a second one.
// What this file still owns is where and when: it decides that a menu was asked for, on what, and
// at which point of the canvas, and hands that over.
function selectedRecordIds(cy) {
    return cy.nodes(':selected').map(node => node.data('recordId')).filter(Boolean);
}

// Clamped here rather than in the component, because this is where the canvas's own size is
// known. The figures are the menu's rough extent: a little too generous is a menu that opens
// slightly inside the edge, which is better than one that opens past it.
const menuExtent = { width: 320, height: 380 };

function requestMenu(element, callback, recordId, recordIds, renderedPosition) {
    const x = Math.min(
        Math.max(8, renderedPosition?.x ?? 0),
        Math.max(8, element.clientWidth - menuExtent.width - 8));
    const y = Math.min(
        Math.max(8, renderedPosition?.y ?? 0),
        Math.max(8, element.clientHeight - menuExtent.height - 8));
    callback.invokeMethodAsync('ContextMenuRequested', recordId ?? null, recordIds, Math.round(x), Math.round(y));
}

function dismissMenuIfOpen(callback, state) {
    if (!state.open) {
        return;
    }

    state.open = false;
    callback.invokeMethodAsync('ContextMenuDismissed');
}

export function create(element, callback, graph, savedPositions, savedViewport) {
    if (!globalThis.cytoscape || !element?.isConnected || graphs.has(element)) {
        return;
    }

    const cy = cytoscape({
        container: element,
        elements: elements(graph, savedPositions),
        layout: { name: 'preset', fit: false },
        minZoom: 0.15,
        maxZoom: 3,
        boxSelectionEnabled: true,
        selectionType: 'additive',
        style: graphStyles()
    });
    const shell = element.parentElement;
    const menuState = { open: false };
    const badgeLayer = shell.querySelector('.graph-type-badges');
    const selectionSummary = shell.querySelector('.graph-multi-selection');
    const badgeState = { elements: new globalThis.Map(), frame: undefined };
    const changeState = { frame: undefined };
    const notifyGraphChanged = () => {
        if (changeState.frame) {
            return;
        }
        changeState.frame = requestAnimationFrame(() => {
            changeState.frame = undefined;
            callback.invokeMethodAsync('GraphChanged');
        });
    };
    rebuildBadges(badgeLayer, graph, badgeState);
    cy.on('render', () => queueBadgePositions(cy, badgeState));
    cy.on('tap', 'node', event => {
        if (isAdditiveSelection(event)) {
            return;
        }

        const node = event.target;
        // Selecting here does not survive: cytoscape finishes its own tap bookkeeping after user
        // handlers have run and clears the selection on its way through, so a plain click used to
        // leave the node unhighlighted while a modified one, which returns above and lets
        // cytoscape do the selecting, did highlight. Doing it on the next frame is what makes the
        // two agree.
        requestAnimationFrame(() => {
            if (node.removed()) {
                return;
            }

            cy.nodes().unselect();
            node.select();
            updateSelectionSummary(cy, selectionSummary);
        });

        const recordId = node.data('recordId');
        if (recordId) {
            callback.invokeMethodAsync('NodeSelected', recordId);
        }
    });
    cy.on('select unselect', 'node', () => updateSelectionSummary(cy, selectionSummary));
    // One handler for both menus. Cytoscape reports a right-click on empty canvas with the core
    // as its target, so telling them apart here is what decides whether the component is being
    // asked about a record or about the space between them.
    cy.on('cxttap', event => {
        event.originalEvent?.preventDefault();
        const node = event.target !== cy && event.target.isNode?.() ? event.target : null;
        const recordId = node?.data('recordId');
        if (!recordId) {
            menuState.open = true;
            requestMenu(element, callback, null, [], event.renderedPosition);
            return;
        }

        // Right-clicking outside the selection acts on that record alone, which is what the
        // pointer was pointing at; right-clicking within it keeps the whole selection.
        const alreadySelected = node.selected();
        const ids = alreadySelected ? selectedRecordIds(cy) : [recordId];
        if (!alreadySelected) {
            requestAnimationFrame(() => {
                if (node.removed()) {
                    return;
                }

                cy.nodes().unselect();
                node.select();
                updateSelectionSummary(cy, selectionSummary);
            });
        }

        menuState.open = true;
        requestMenu(element, callback, recordId, ids, event.renderedPosition);
    });
    cy.on('tap drag', () => dismissMenuIfOpen(callback, menuState));
    cy.on('pan zoom', event => {
        // Only when the operator moved the graph. Creating a record relayouts it, and a menu that
        // closed on its own relayout would take its own "created that" message with it.
        if (event.originalEvent) {
            dismissMenuIfOpen(callback, menuState);
            notifyGraphChanged();
        }
    });
    cy.on('dragfree', 'node', event => {
        separateDraggedNodes(cy, event.target);
        notifyGraphChanged();
    });

    const suppressContextMenu = event => event.preventDefault();
    const dismissMenu = event => {
        // The menu is the component's element now, so it is not inside the shell; a pointer landing
        // in it must not be read as a click away from it.
        if (!shell.contains(event.target) && !event.target?.closest?.('.graph-context-menu')) {
            dismissMenuIfOpen(callback, menuState);
        }
    };
    const handleKeyDown = event => {
        if (event.key === 'Escape') {
            dismissMenuIfOpen(callback, menuState);
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
        event.preventDefault();
        menuState.open = true;
        if (!node?.length) {
            // No record to act on, so this is the canvas menu, opened from the keyboard at a
            // sensible place rather than wherever a pointer last happened to be.
            requestMenu(element, callback, null, [], { x: element.clientWidth / 2, y: element.clientHeight / 2 });
            return;
        }

        requestMenu(element, callback, node.data('recordId'), selectedRecordIds(cy), node.renderedPosition());
    };
    element.addEventListener('contextmenu', suppressContextMenu);
    element.addEventListener('keydown', handleKeyDown);
    document.addEventListener('pointerdown', dismissMenu);
    let resizeFrame;
    const observer = new ResizeObserver(() => {
        cancelAnimationFrame(resizeFrame);
        resizeFrame = requestAnimationFrame(() => {
            cy.resize();
            queueBadgePositions(cy, badgeState);
        });
    });
    observer.observe(element);
    graphs.set(element, { cy, observer, suppressContextMenu, handleKeyDown, dismissMenu, badgeLayer, badgeState, changeState, selectionSummary, menuState });
    runLayout(cy, savedPositions, new globalThis.Map(), badgeState, savedViewport);
}

export function update(element, graph, savedPositions) {
    const instance = graphs.get(element);
    if (!instance) {
        return;
    }

    const { cy, badgeLayer, badgeState } = instance;
    const preservedPositions = capturePositionMap(cy);
    const preservedViewport = currentViewport(cy);
    cy.elements().remove();
    cy.add(elements(graph, savedPositions));
    rebuildBadges(badgeLayer, graph, badgeState);
    updateSelectionSummary(cy, instance.selectionSummary);
    runLayout(cy, savedPositions, preservedPositions, badgeState, preservedViewport);
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

export function getViewport(element) {
    const cy = graphs.get(element)?.cy;
    return cy ? currentViewport(cy) : null;
}

export function resetViewport(element) {
    const graph = graphs.get(element);
    if (!graph) {
        return;
    }

    graph.cy.fit(graph.cy.elements(), 36);
    queueBadgePositions(graph.cy, graph.badgeState);
}

// Both of these wait for layout before measuring. A fullscreen transition resizes the element
// after its event has already fired, so acting immediately would frame the size the graph had a
// moment ago rather than the one it now has.
function afterLayout(element, act) {
    const graph = graphs.get(element);
    if (!graph) {
        return;
    }

    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (!graphs.has(element)) {
            return;
        }

        graph.cy.resize();
        act(graph);
        queueBadgePositions(graph.cy, graph.badgeState);
    }));
}

export function fitToElement(element) {
    afterLayout(element, graph => graph.cy.fit(graph.cy.elements(), 36));
}

export function restoreViewport(element, viewport) {
    afterLayout(element, graph => applyViewport(graph.cy, viewport));
}

export function centerOn(element, recordId) {
    const cy = graphs.get(element)?.cy;
    const node = cy?.getElementById(recordId);
    if (!cy || !node?.length) {
        return;
    }

    cy.nodes().unselect();
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
        cancelAnimationFrame(graph.changeState.frame);
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
