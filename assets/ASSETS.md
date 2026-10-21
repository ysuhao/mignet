# 视觉素材交接 / Visual asset handoff

Generated with an image model on 2026-10-21 (Asia/Tokyo). These project copies are the integration sources; the original image alpha is preserved.

| File | Pixels | Purpose | Alpha inspection |
|---|---|---|---|
| stamp-idle-v1.png | 1429 × 1100 | Upright matte black stamp body with crimson bottom seam | ARGB; corner alpha 0 |
| stamp-drag-v1.png | 1430 × 1100 | Same stamp lifted and tilted, underside partially visible | ARGB; corner alpha 0 |
| stamp-reticle-v1.png | 1254 × 1254 | Orthographic precision metal ring, off-white and red ticks | ARGB; corner AND center alpha 0 |

## Rendering guidance

- Keep original alpha. The black shown behind the generated previews is the image viewer backdrop, not an intended background layer. Never composite these against black before rendering.
- Idle and drag bodies share a recognizable product design. Their framing/bounds differ slightly: use normalized visual anchors or occupied-alpha bounds in YOUR code to prevent pose changes from shifting the base. Do not stretch independently on X/Y. Cap highlights are intentionally restrained and light comes from upper left.
- These are high-resolution raster poses, not a 3D mesh or a full rotation atlas. A blend/transform transition is an approximation; report it honestly. Dynamic velocity tilt, spring damping, depth, drop shadows, hover glow, name label and impact ripple should be rendered as separate layers in code.
- The ring has a transparent aperture. Preserve that aperture when adding hit testing: a transparent visual hole must not become an opaque mouse-blocking rectangle. Its existing tick marks are visual texture, not a guarantee of metrologically exact subdivisions. Add code-calculated marks if a physical angular scale is required.
- Additional alpha QA: 2,821 sampled pixels within radius 450px of center (627,627) have maximum alpha 1/255; exact center alpha is 0. Image-generation antialiasing leaves negligible residual alpha in a few aperture samples. In YOUR renderer, explicitly mask the central aperture to alpha zero and use geometric hit tests; do not infer interactive area from alpha > 0. No image pixels have been edited by Codex.
- Reticle should be understated in use: reduce whole-ring opacity as appropriate, with a code-rendered semitransparent seal ghost inside the open center. No tinted glass, white disc or full-window backdrop.
- Seal width is distinct from ring outer width. Physical 20/40mm sizing must derive from the ink imprint and Word points/DPI; image canvas or ring diameter is not the seal diameter.
- Contact glow, impact shadows, and hover breathing are dynamic; do not scale the red seam to full brightness on every state. Name text stays separate so imported seal names remain legible and localizable.
- No final impression artwork is baked into the metal. User-imported seal art is separate. Any demonstration stamp must clearly identify itself as SAMPLE/样章.

## Status

All three assets are final v1 and available now. They have been visually inspected and metadata-checked; original image alpha is preserved without postprocessing. Integrate these files, build, verify state transitions,.
