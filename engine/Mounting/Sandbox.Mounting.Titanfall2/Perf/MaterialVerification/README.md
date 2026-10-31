# Mounted material GPU checks

`VerifyDepthNormals.csx` performs 43 checks against the actual native
`NormalsGBuffer` for all eight depth-capable mount shaders. It checks fog and
unlit independence, rotated world normals, opaque roughness, and cutout / decal
silhouettes. Its fullscreen reader handles MSAA and viewport offsets. Compile
`shaders/tests/titanfall2_gbuffer_read.shader` and the mount shaders, then restart
the editor before running it: resident shader caches can otherwise return old
programs. This checks G-buffer correctness, not retail visual parity or a frame
time improvement.

`VerifyColorCorrection.csx` performs 48 real-pixel checks of the mounted color
lookup shader, including identity and red/blue permutation controls, three retail
32-cubed tables, multiple input colors, partial weights, clone texture restoration
after destroying the source, and post-process volume weighting. It explicitly
builds post-processing for its independent fixture scene and requires the built-in
brightness-zero control to turn the image black. The three retail raw fixtures
currently come from the audit directory referenced in the script; copy or adjust
that path when reproducing on another workstation. Compilation success and an
identity-only pass cannot establish that the custom shader actually ran.

Run these trusted MCP method-body scripts in an editor or game that has the
Titanfall 2 mount loaded. Each script creates a separate temporary Scene and
destroys it in `finally`; the user's scene is not saved or changed.

```powershell
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"code":"await Sandbox.Mounting.Directory.Mount(\"titanfall2\");return true;"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyLayerBlend.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyWaterComposite.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyLightmapUv.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyModelFailure.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyMapCache.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifySurfaceUv.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyLayerOpacity.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyTextureMips.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyOpacityFade.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyAtmosphereSource.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/MaterialVerification/VerifyNativeProbeVolume.csx"}'
```

`VerifyLayerBlend` renders five controlled cases with red/blue surface layers,
authored vertex alpha, blend masks and two ramp values. All five `passed` values
must be true. It checks actual GPU material binding and vertex input, including
the retail `(ramp + 1) * vertexAlpha` term. It does not only compare two CPU
implementations of the formula.

`VerifySurfaceUv` separates UV0 from an 8x material transform. A two-colour
mask must cross once with UV0 and repeatedly with UV1 (13 transitions in the
reference frame). This detects accidental reuse of detail-normal coordinates
for albedo and blend masks.

`VerifyLayerOpacity` checks the retail Lyrtrans inverted coverage with ramp
1.61, including full vertex alpha hiding the overlay. Its five GPU cases also
check that ordinary decals retain their previous alpha behaviour.

`VerifyTextureMips` creates constant-colour BC1 mips through the mount's real
texture upload function, then samples beyond the last retained mip. All four
samples must pass; far samples must clamp to the last valid blue 4x4 mip.
The tiny diagnostic shader in `shaders/tests/` is used only by this check.

`VerifyOpacityFade` checks 48 actual GPU cases across four transparency paths:
retail distance/angular attenuation, byte Alpha 1, front/back-face selection,
local atmosphere opacity and soft depth intersection. It compares linear
radiance after decoding screenshot sRGB. `VerifyAtmosphereSource` separately
checks five installed retail material descriptors and two retail dust PCFs.
Real-map isolation, calibration and remaining limits are in `KODAI_HAZE_FIXES.md`.

`VerifyNativeProbeVolume` checks the native baked-volume interface using six
constant-colour RGBA16F slabs. Its seven GPU samples verify +X,+Y,+Z,-X,-Y,-Z
face order and a diagonal irradiance sample. It does not verify the retail
Titanfall SH coefficient scale or static-prop association; see
`LIGHTING_DATA_AUDIT.md` before using retail data.

`VerifyLightProbeData` checks the retail signed Q13 decoder independently of
rendering: unit fallback, signed directions/clamping, sky separation, short
records, local-light raw bytes, source-index binding after filtering, and invalid
indices. If the three exported retail lump pairs exist in the audit directory,
it also checks all 66,506 static-prop associations. Two additional checks cover
default-cache omission and non-null SH metadata round trips. Its 16 checks do not establish
that the coefficients have reached individual draw calls or instance batches.

`VerifyWaterComposite` draws water over a checkerboard. Its zero-coverage case
must match the background (mean channel difference below 2/255). The remaining
cases report how much partial and full coverage change the background; visual
inspection is still required. Maps also need a partial-viewport/shoreline test:
the full-screen test alone did not expose Traffic's old framebuffer-copy fringe.

`VerifyLightmapUv` holds UV0 constant while mapping a four-colour atlas with
TEXCOORD1. Its `passed` result requires distinct red, green, blue and white
quadrants, testing the full vertex-to-pixel UV path and SKY A RGB/alpha decode.
This does not establish that all original HDR/SH/RTL lighting has been restored.

`VerifyModelFailure` deliberately loads Angel City's empty static mister model.
It checks that valid models are accepted, both the error singleton and an error
resource wrapper are rejected, and two consecutive requests share one retry
deadline. The initial expected resource error is part of this diagnostic; it
must not be mistaken for an unexplained map regression.

After loading Homestead in the default BSP mode, `VerifyMapCache` compares the
persisted map with a fresh parse: geometry bytes, material/page identity, PVS and
prop metadata (including non-finite lighting-origin fields) must survive the
round trip. It reports a miss if no compatible map cache exists. This reads the
source BSP again for validation and is not a loading-speed benchmark.

Screenshots and JSON are written to the MCP session's `exchangeDirectory`.
Retain them before closing the connection if the client cleans up that folder.
Compile shader edits through an editor and use a **fresh process** before running
these checks: the native resident shader cache may keep an older compiled shader.
The rendering probes draw three warmup frames before reading pixels; startup
resource uploads and first-use pipelines can otherwise return the previous draw
for a single frame. A failure after warmup remains a test failure.

These are focused rendering checks, not a substitute for map/LOD/animation and
transparent sorting regressions. Traffic, Angel City and an outdoor/indoor map
still need fixed-camera comparisons after changes to shared shaders.

## War Games visibility and transparent materials

Run `VerifyLegacyGrid.csx`, `VerifyPropVisibilityCoverage.csx` and
`VerifyVtfAtlasMips.csx` through `run_csharp`. Their 31, 5 and 10 checks cover
dual-texture multiplication versus the retained sum variant, authored tint and
Basic screen routing, conservative PVS coverage, actual animated-atlas mip
sampling and particle top-frame extraction. The atlas check gives each source
mip/frame a different colour and checks clamping beyond the last valid BC mip.

With `mp_wargames` running and its props loaded, `VerifyWargamesScene.csx` checks
the actual grid render path, disabled grid shadows and visible Goblin instance.
It temporarily moves the camera and restores its transform, leaving spectator
controls disabled for subsequent fixed captures. See `WARGAMES_VISUAL_FIXES.md`
for remaining screen, grid and HDR appearance differences.

`VerifyVmtProxies.csx` must run with all mounted map tabs closed. Its 15 checks
exercise source-ordered scalar/vector proxies, GPU alpha output, the last
animator stopping while its material remains cached, explicit addlayers and the
real Live Fire cubemap path. `VerifyLiveVmtProxies.csx` adds three actual scene
update checks; run it with a map open and again after reloading that map.

`VerifyGeneratedWorldResources.csx` checks all War Games aggregate/translucent
models and four generated lightmap slots, rather than a sample of ten objects.
Run it on initial load, then stop Play, close the mounted map, let both native
retention windows expire (12 seconds), force GC, reopen and run it again. This
detects cached SceneFiles pointing to reclaimed procedural models. Check both
model validity and `IsError`; an error singleton can still be a valid resource.
See `CODE_AUDIT.md` for the retained-resource lifetime and remaining budget work.

## Per-instance probes, compiled lights and cubemap uploads

The 2026-10-26 material repair adds `VerifyMaterialRouting.csx` (17 real-material
checks), `VerifySurfaceCoverage.csx` (10 GPU cases for non-decal Lyrtrans) and
`VerifyEmissionUv.csx` (16 GPU cases separating image UV0 from scanline UV1).
Run each through `run_csharp`; they return `{ output, rows }`. Routing checks
also require independent main-emission and modulation textures, not merely two
parameter names. These do not prove complete water reflections, exposure or
all material permutations. Real-map evidence and remaining scope are in
`ANGEL_CITY_MATERIAL_FIXES.md`.

Run the additional `VerifyProbeIndex.csx`, `VerifyProbeLighting.csx`,
`VerifyProbeTransport.csx`, `VerifyProbeBatchLod.csx`, `VerifyProbeLightPasses.csx`,
`VerifySpecularWorkflow.csx`, `VerifyVtfCubeMips.csx`, `VerifyWorldLightData.csx` and
`VerifyWorldSunLightmap.csx` through `run_csharp` with the same absolute-path form
used above. They return `{ output, rows }`; inspect every `rows[].passed`.

These cover actual GPU instance-ID transport, source-index preservation across
LOD/culling, signed SH/sky evaluation, direct-sun separation, local lights and
emission, RGB F0 diffuse retention, VTF six/seven-face frame layouts and valid BC
mip clamping. The world-light checks cover versions 1–3 and the retail RTL page
and channel mapping, including integer channel selection with bilinear visibility.
See `LIGHTING_DATA_AUDIT.md` for counts, retail samples and remaining scope.

The three retail world-light checks require the exported lump 54/83/105/122
fixtures in the audit directory. Synthetic cases do not substitute for those
real-data checks. The sun rendering probes keep the native directional-light
path enabled with one cascade; the fixture itself does not cast a shadow.
They explicitly require the full and hidden samples to differ before testing
interpolation, avoiding a vacuous pass when no sun reaches the fixture.
