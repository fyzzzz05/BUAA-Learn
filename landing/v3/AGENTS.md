# Prototype Instructions

Run the local server yourself and open the preview in the browser available to this environment. Do not give the user server-start instructions when you can run it.

Before making substantial visual changes, use the Product Design plugin's `get-context` skill when the visual source is unclear or no longer matches the current goal. When the user gives durable prototype-specific design feedback, preferences, or decisions, record them in `AGENTS.md`.

When implementing from a selected generated mock, treat that image as the source of truth for layout, component anatomy, density, spacing, color, typography, visible content, and hierarchy.

Build app UI in `src/`. Keep `.openai/hosting.json`, `worker/index.js`, `scripts/prepare-sites-build.mjs`, and `tests/sites-worker.test.mjs` intact so the same local prototype can be handed to Sites. Before a Sites handoff, run `npm run build` and `npm run test:sites`; the build must leave `dist/client/index.html`, `dist/server/index.js`, and `dist/.openai/hosting.json`.

## Selected direction
User chose the dark blue horizontal lecture-timeline mock on 2026-11-16. Preserve its headline, product reading panel, three chapter controls, and action-based lower sections. Render UI as interactive HTML, never as a page-sized generated image. Background light artwork may be raster. Demonstrations use polished fictional classroom information. Do not deploy until requested.

The landing header uses the existing desktop blue-book/orange-dot SVG icon (desktop/assets/icon.svg), copied to public/images/buaa-learn-icon.svg. Keep the user’s product icon instead of a generic paper-plane mark.

User approved the first screen on 2026-11-17; preserve it during lower-page updates. Lower sections now demonstrate an orchestrated BUAA Learn review workflow using crisp DOM animation, polished fictional class content, and user-selectable recap, knowledge-point lookup, and homework scenes.
