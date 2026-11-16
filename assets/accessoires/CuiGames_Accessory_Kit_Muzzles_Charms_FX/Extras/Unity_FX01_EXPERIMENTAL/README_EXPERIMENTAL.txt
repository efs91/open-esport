Unity FX01 – EXPERIMENTAL (optional bonus)

Status: experimental. Scripts compile against the Unity API, but import, shader compile, prefab
generation and Play Mode have NOT been verified inside the Unity Editor. Use at your own risk.

What it adds: scope picture-in-picture + reticle, holo reticle brightness, 1-6x demo zoom (L01),
flashlight spotlight (FL01), red/green beam with hit dot (LM01), night-vision-only IR look (IM01).

Install
1. Copy Assets/TacticalAccessoryFX into your project's Assets folder.
2. Menu: Tools > Tactical Accessory FX > Build Prefabs and Demo.
3. Open the generated demo scene and press Play.

Render pipeline compatibility (Built-in / URP / HDRP) is untested. Models here are FBX copies of the 29 rail variants with their own simple
material setup (catalog.json); the main GLB/blend files in Scopes/ and Tactical/ have the updated PBR look.
Layer 31 is used locally by the scope camera to exclude effect objects (not renamed project-wide). Effects are visuals only (no shooting/ballistics code).
