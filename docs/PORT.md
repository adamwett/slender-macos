# Port notes

How the 2012 Windows build of _Slender: The Eight Pages_ becomes a native Apple silicon app,
and why each step works the way it does.

|          | Original                               | Port                                |
| -------- | -------------------------------------- | ----------------------------------- |
| Binary   | PE32 x86 (32-bit Windows)              | Mach-O arm64                        |
| Engine   | Unity 3.5.4f1                          | Unity 2022.3.62f3                   |
| Graphics | Direct3D 9                             | Metal                               |
| On a Mac | CrossOver (Rosetta + D3D9 translation) | Runs directly                       |

## Why Unity 2022.3

**CrossOver** is slow for structural reasons. 32-bit x86 runs through Rosetta and every D3D9
call is translated to Metal, in a game that already made a lot of draw calls in 2012. No
setting gets around that.

**A Godot rebuild** would be a remake, not a port. The gameplay code is small (about 2,500
lines), but Unity Terrain with 4,899 instanced trees, terrain splatting, legacy particles, the
flashlight cookie and the scene graph have no Godot equivalent. AssetRipper rebuilds all of
that as a Unity project.

**Unity 2022.3 LTS, not Unity 6.** Every legacy feature this game uses was removed in Unity
2018.x or earlier:

| Removed feature                                                    | Removed in |
| ------------------------------------------------------------------ | ---------- |
| Legacy `ParticleEmitter` / `ParticleAnimator` / `ParticleRenderer` | 2018.3     |
| `GUIText` / `GUITexture` / `GUILayer`                              | ~2018.x    |
| `component.rigidbody` / `.camera` / `.renderer` shortcuts          | 2018.4.36  |
| UnityScript compiler                                               | 2018.3     |
| `SplatPrototype` replaced by `TerrainLayer`                        | 2018.3     |

So 2022.3 and Unity 6 reject exactly the same things, and Unity 6 brings extra graphics
changes on top. 2021.2 is the oldest editor that runs natively on arm64, and 2022.3 is the
most mature LTS after it.

## Extraction

AssetRipper 1.3.14's minimum supported Unity version is 3.5.0, so 3.5.4f1 is right at the
bottom of its range. It has no ripping CLI. `build.sh` runs its web server headless and
drives it over HTTP with `ScriptExportMode=Hybrid`, which decompiles the game assemblies to
C#.

AssetRipper also has an experimental `TargetVersion` setting that rewrites assets for a
modern Unity version directly. It was tried and dropped: it silently cleared
`m_CastShadows` and `m_ReceiveShadows` on 264 of 265 renderers. The build exports faithfully
as 3.5.4f1 and lets Unity's own upgrader convert the assets.

## Code

- **Unused code.** 32 of the 53 decompiled scripts are Unity Standard Assets the game
  shipped but never used (`ThirdPersonController`, `RollABall`, the iPhone `Joystick`). They
  were removed by following dependencies out from the 17 scripts the scene references. That
  also removed the only users of `GUITexture`.
- **API renames.** Component shortcuts, `Screen.lockCursor`, `Application.LoadLevel` and
  `StartCoroutine_Auto` are rewritten to their current equivalents.
- **The UnityScript coroutine.** `CharacterMotor` was compiled by the UnityScript/Boo
  compiler, which turned its coroutine into a `Boo.Lang.GenericGenerator` state machine.
  Those types no longer exist, so the coroutine is rewritten as a C# iterator with the same
  behaviour.

## Behaviour changes that throw no errors

Code can compile and run and still behave differently from 2012.

**Flashlight brightness.** Unity 5 removed the legacy 2x light intensity multiplier. When it
upgrades a project it doubles every light stored in the scene: the flashlight spot goes from
1 to 2 and the title light from 2 to 4. It can't change intensities set from script, and
`PlayerScript` sets the flashlight from `battery`, which tops out at 1.0. Left alone, the
flashlight shines at half the brightness of everything else in the scene. The two ending
lights in `LoseScript` have the same problem. All three are doubled in code.

**Slender Man teleporting into trees.** He checks a teleport spot with a probe object whose
`OnCollisionStay` rejects spots that overlap geometry. PhysX 2.8 kept calling that callback
for as long as the overlap lasted. PhysX 3 stops calling it once a body sleeps, and this
probe sleeps almost at once because it is non-kinematic with gravity off. A sleeping probe
never reports an overlap, so every spot passes. The probe's `sleepThreshold` is set to 0.

**Frame-rate coupling.** `AmbientScript` counts down its scare-sound timer once per frame in
`Update`, not in `FixedUpdate`. Uncapped on an M-series Mac, the sounds would play several
times too often. No code change was needed: the default Standalone quality level has vSync
on.

## Scene

**Terrain.** In Unity 3.5, `Terrain` was a MonoBehaviour backed by a built-in script
(fileID 12000). Unity 5 made it native class 218, so the old component is dropped on load
and the forest floor disappears with it. `PortFixup` recreates it with the settings from the
original scene: tree distance 2000, billboard distance 80, 160 full-LOD trees, detail
distance 80, heightmap pixel error 1, basemap distance 40. Unity's upgrader converts the
`SplatPrototype`s to `TerrainLayer`s by itself (`Assets/_TerrainAutoUpgrade/`), so the
fixup's own layer path does nothing in practice.

**Shaders.** The free edition of AssetRipper can't decompile D3D9 shader bytecode, and the
premium one only does it on Windows. All 8 shipped shaders come out as stubs. Seven of them
have the same names as Unity's built-in tree shaders and would hide the real ones, turning
the leaves into opaque quads. `Skin/Diffuse` loses its SubShader and comes out with a broken
fallback name. The build deletes the stubs and `PortFixup` points the 12 affected materials
at the built-in shaders by name. `Skin/Diffuse` has no modern equivalent and falls back to
`Legacy Shaders/Diffuse`.

The other materials need nothing. Unity maps every legacy built-in shader ID to its current
version on import.

## Project settings

The 2012 `ProjectSettings` files are missing keys that didn't exist yet. A static reading
suggested those keys would load as 0 and cause problems: `autoSyncTransforms` off,
`shadowNearPlaneOffset` 0, particle raycast budget 0, `maximumParticleDeltaTime` 0, and
missing input axes. Probing the running engine showed all five were fine: Unity's upgrade
path fills in modern defaults (`True`, `3`, `4` to `4096` per tier, `0.03`, and all 11 input
axes). `Time.fixedDeltaTime` is 0.02 and the color space is Gamma, both matching 2012. That
matters because every gameplay timer runs in `FixedUpdate` and every material was authored
for gamma.

## Build output

`BuildMac` sets the player up for the Apple silicon architecture, the Mono scripting backend
and the Gamma color space, at 1280×720 in a fullscreen window. The original port was checked
for:

- **Compile:** 0 errors.
- **Scene audit:** 0 unsupported shaders, 0 null materials, 0 missing scripts. `PortFixup`
  checks this on every build and fails if it isn't true.
- **Architecture:** `lipo -archs` reports `arm64` for every executable and dylib in the
  bundle.
- **Runtime:** runs on Metal with no exceptions through a full play session.
