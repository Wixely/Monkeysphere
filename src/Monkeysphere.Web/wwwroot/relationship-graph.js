const graphs = new globalThis.Map();

function elements(graph) {
    return [
        ...graph.nodes.map(node => ({
            group: 'nodes',
            data: {
                id: node.recordId,
                recordId: node.recordId,
                label: node.displayName,
                type: node.recordTypeName,
                distance: node.distance
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

function runLayout(cy) {
    cy.layout({
        name: 'cose',
        animate: false,
        fit: true,
        padding: 36,
        nodeRepulsion: () => 7000,
        idealEdgeLength: () => 100,
        randomize: true
    }).run();
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
                    'width': 30,
                    'height': 30
                }
            },
            {
                selector: 'node[distance = 0]',
                style: { 'background-color': '#713703', 'width': 42, 'height': 42 }
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
    cy.on('tap', 'node', event => callback.invokeMethodAsync('NodeSelected', event.target.data('recordId')));
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
