# Particle and ribbon runtime checks

Run the trusted method-body scripts with the Titanfall 2 mount loaded in an
s&box editor or game. They create and destroy separate temporary scenes and
do not save the user's scene.

```powershell
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/ParticleVerification/VerifyParticleGeometry.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/ParticleVerification/VerifyParticleMapData.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/ParticleVerification/VerifyParticleQueue.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/ParticleVerification/VerifyAtmosphereParticles.csx"}'
game/bin/managed/sbox-cli.exe --connect <pid> --call run_csharp '{"path":"<absolute-repo>/engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/ParticleVerification/VerifyParticleOrientation.csx"}'
```

The geometry script returns 23 checks. It uses retail PCFs for the ribbon,
sparks, steam, composite harvester beam, distance-emitted beacon, persistent
light card, directional/distance visibility and overbright material. A separate
native sprite checks the zero-projected-velocity shader guard and HDR colour.
The script manually runs the sprite batching stage after particle simulation:
an isolated Scene does not receive the active game's normal FinishUpdate stage.
Each capture uses a fixed camera and four warmup draws before readback.

The data script checks version 3 control-point serialization and version 2
compatibility. The queue script deliberately supplies six real definitions and
one missing definition. It must retain valid pending systems across the first
spawn budget and ultimately create all six. Its one missing-name warning is
expected test output.

All `passed` fields must be true. Images and JSON go to the MCP connection's
exchange directory. Retain useful results before disconnecting. Build and
deploy the mounting DLL and formally compile both the ribbon shader and the
changed sprite compute shader; use a fresh engine process after shader changes.

`VerifyAtmosphereParticles` emits 100 particles from the actual retail ground
dust definition at each of two translated/rotated poses. The authored warp
must flatten the sphere to a local Z range of 10–50 units while retaining its
horizontal spread. This detects both a missing warp and warping around the
world origin instead of the emitter offset.

The checks establish these focused behaviours, not complete PCF parity. Real
maps still need endpoint, transparent-depth, fog, activation and performance
checks. See `../../PARTICLE_BEAM_FIXES.md` for the implementation boundaries.

`VerifyParticleOrientation` exercises the real factory with a controlled sprite.
Its 14 checks cover modes 0/1/2/3 routing, six native GPU side/top projections,
repeated simulation with roll, and reversal of the horizontal card normal by a
180-degree yaw flip. Mode 2 must stay in the XY plane; mode 1 stays upright.
Coverage is measured over the image rather than one edge pixel. Destroyed
fixtures are disabled and flushed between cases so their sprite groups cannot
contaminate the next projection. These checks do not validate Source's full
normal/control-point orientation modes.
