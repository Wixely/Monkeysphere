# Accessibility review

Last reviewed: 2026-08-29

Monkeysphere uses native links, buttons, labels, form controls, headings, tables, and validation messages wherever those elements fit. The application also provides a keyboard-visible skip link to the main content.

## Non-text views

- The relationship graph canvas has an accessible name and description. Every displayed node is available in a standard select control, which drives the same record-details panel as selecting a visual node.
- The spatial map has an accessible name and description. Every location on the current result page is also available in an expandable HTML list with record, field, context, and approximation-radius details.
- The coordinate map editor supplements labelled latitude, longitude, accuracy, and radius fields. Its click interaction is optional.

The accessible graph and map alternatives deliberately follow the same server-side query bounds as their visual equivalents.

## Review evidence

- Chromium desktop and 390 by 844 mobile layouts were inspected through the accessibility tree on 2026-08-29.
- Keyboard traversal exposes the skip link as the first focusable control and uses visible focus styling.
- The mobile header wraps its brand and account action above a horizontally scrollable navigation row without clipping the account action.
- The setup choices expose exactly one `aria-pressed="true"` state after selection and `aria-pressed="false"` on the other choices.
- Authenticated rendering tests assert that the accessible graph selector, map list, and skip link are present.

This is a focused accessibility review, not a WCAG conformance claim. Testing with dedicated screen readers, Windows high-contrast mode, browser zoom, and automated accessibility scanners remains to be completed in suitable environments.
