const graphs = new globalThis.Map();

function elements(graph) {
    return [
        ...graph.nodes.flatMap(node => {
            const result = [{
                group: 'nodes',
                data: {
                    id: node.recordId,
                    recordId: node.recordId,
                    label: node.displayName,
                    type: node.recordTypeName,
                    distance: node.distance,
                    imageUrl: node.imageId
                        ? `/records/${encodeURIComponent(node.recordId)}/images/${encodeURIComponent(node.imageId)}/thumbnail`
                        : null
                }
            }];
            if (node.recordTypeSymbol) {
                result.push({
                    group: 'nodes',
                    data: {
                        id: `type-badge-${node.recordId}`,
                        badgeFor: node.recordId,
                        label: node.recordTypeSymbol,
                        type: node.recordTypeName
                    }
                });
            }
            return result;
        }),
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

function positionBadge(cy, node) {
    const badge = cy.getElementById(`type-badge-${node.id()}`);
    if (!badge.length) {
        return;
    }

    const position = node.position();
    const offset = Math.max(node.width(), node.height()) * 0.32;
    badge.unlock();
    badge.position({ x: position.x + offset, y: position.y - offset });
    badge.lock();
}

function positionBadges(cy) {
    cy.nodes().forEach(node => {
        if (!node.data('badgeFor')) {
            positionBadge(cy, node);
        }
    });
}

function runLayout(cy) {
    const layoutElements = cy.elements().filter(element => element.isEdge() || !element.data('badgeFor'));
    layoutElements.layout({
        name: 'cose',
        animate: false,
        fit: false,
        padding: 36,
        nodeRepulsion: () => 7000,
        idealEdgeLength: () => 100,
        randomize: true
    }).run();
    positionBadges(cy);
    cy.fit(cy.elements(), 36);
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
        wheelSensitivity: 0.25,
        style: [
            {
                selector: 'node',
                style: {
                    'background-color': '#a95508',
                    'border-color': '#fffaf0',
                    'border-width': 3,
                    'label': 'data(label)',
                    'color': '#3d2108',
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
                style: { 'background-color': '#713703', 'width': 42, 'height': 42 }
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
                selector: 'node[badgeFor]',
                style: {
                    'background-color': '#fffaf0',
                    'border-color': '#2d2418',
                    'border-width': 2,
                    'label': 'data(label)',
                    'color': '#2d2418',
                    'font-family': '"Segoe UI Emoji", "Segoe UI Symbol", sans-serif',
                    'font-size': 12,
                    'font-weight': 700,
                    'text-wrap': 'none',
                    'text-halign': 'center',
                    'text-valign': 'center',
                    'text-margin-x': 0,
                    'text-margin-y': 0,
                    'width': 18,
                    'height': 18,
                    'events': 'no',
                    'z-index': 20
                }
            },
            {
                selector: 'node:selected',
                style: { 'border-color': '#e9a23b', 'border-width': 6 }
            },
            {
                selector: 'edge',
                style: {
                    'curve-style': 'bezier',
                    'line-color': '#c78a45',
                    'width': 2,
                    'label': 'data(label)',
                    'font-size': 9,
                    'color': '#713703',
                    'text-background-color': '#fffaf0',
                    'text-background-opacity': 0.85,
                    'text-background-padding': 2,
                    'target-arrow-color': '#a95508',
                    'target-arrow-shape': 'none'
                }
            },
            {
                selector: 'edge[directional = true]',
                style: { 'target-arrow-shape': 'triangle' }
            }
        ]
    });
    cy.on('position', 'node', event => {
        if (!event.target.data('badgeFor')) {
            positionBadge(cy, event.target);
        }
    });
    cy.on('tap', 'node', event => {
        const recordId = event.target.data('recordId');
        if (recordId) {
            callback.invokeMethodAsync('NodeSelected', recordId);
        }
    });
    graphs.set(element, cy);
    runLayout(cy);
}

export function update(element, graph) {
    const cy = graphs.get(element);
    if (!cy) {
        return;
    }

    cy.elements().remove();
    cy.add(elements(graph));
    runLayout(cy);
}

export function dispose(element) {
    const cy = graphs.get(element);
    if (cy) {
        cy.destroy();
        graphs.delete(element);
    }
}
