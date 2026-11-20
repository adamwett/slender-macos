# slender-macos

Build a native Apple silicon version of _Slender: The Eight Pages_ (v0.9.6, 2012) from your
own copy of the original Windows game.

The 2012 release is a 32-bit Windows Unity 3.5 build. On a modern Mac it only runs through
CrossOver or Wine, which means Rosetta for the x86 code and Direct3D 9 translated to Metal.
This repo turns the original game files into an arm64 `Slender.app` built with Unity 2022.3
that renders with Metal. The finished bundle contains no x86 code.

**This repository contains no game files.** It holds build scripts, source patches and Unity
editor scripts. All game content comes from the copy you supply, at build time, on your
machine.

## Requirements

- A Mac with Apple silicon.
- **Unity 2022.3.62f3**, installed through [Unity Hub](https://unity.com/download)
  (`unityhub://2022.3.62f3/96770f904ca7`). The macOS editor already includes Mac build support.
  Sign in to Hub with a Unity account and activate a license (the free Personal license works).
  Batch mode will not run without one.
- The original **Slender v0.9.6 for Windows**: the folder holding `Slender.exe` and
  `Slender_Data/`. The build checks file hashes and stops if they don't match. Other versions,
  including the 2012 Mac build, have not been tested.
- About 2 GB of free disk space for the Unity project and its import cache.

[AssetRipper](https://github.com/AssetRipper/AssetRipper) 1.3.14 is downloaded the first
time you build and checked against a pinned SHA-256.

## Build

```sh
git clone https://github.com/adamwett/slender-macos.git
cd slender-macos
./build.sh /path/to/Slender
```

The output is `dist/Slender.app`. Copy it wherever you like and double-click it.

If Unity isn't installed in Hub's default location, point the script at it:

```sh
UNITY=/path/to/Unity.app/Contents/MacOS/Unity ./build.sh /path/to/Slender
```

The first build takes a few minutes, mostly Unity importing the project. Every run starts
over from a fresh extraction, so the output never depends on leftovers from an earlier run.
Logs go to `work/logs/`.

The reconstructed Unity project is left at `work/Slender`. You can open it in Unity
2022.3.62f3 to look at the scene, scripts and terrain. The editor has a **Port** menu with
**Run Fixup** and **Build Apple Silicon**.

## What the build does

1. Checks the game files against `game.sha256`.
2. Runs AssetRipper headless to turn the game into a Unity project: the scene, terrain,
   meshes, textures, audio, and C# decompiled from the game's assemblies.
3. Deletes scripts the scene never uses, mostly Unity 3.5 Standard Assets that no longer
   compile. Also deletes the shader stubs AssetRipper writes in place of D3D9 bytecode it
   can't decompile.
4. Patches the decompiled C# for Unity 2022.3 (`scripts/patch-sources.pl`):
   - `.renderer`, `.camera` and `.rigidbody` shortcuts become `GetComponent<T>()`.
   - `Screen.lockCursor` becomes `Cursor.lockState` / `Cursor.visible`.
   - `Application.LoadLevel` becomes `SceneManager.LoadScene`.
   - A coroutine compiled by the UnityScript compiler as a `Boo.Lang` generator is
     rewritten as a C# iterator.
   - Flashlight and ending-light intensities set from script are doubled. Unity 5 removed
     the old 2x light multiplier and only compensated for lights stored in the scene, so
     without this the flashlight is half as bright as it should be.
   - The probe Slender Man uses to test teleport spots is kept awake. PhysX 3 stops collision
     callbacks on sleeping bodies, which would let him teleport inside trees.

   Each game-specific patch must match exactly once, so a build from different game files
   fails instead of producing a quietly broken port.
5. Opens the project in Unity in batch mode and runs `PortFixup` (`unity/Editor/`). It
   recreates the Terrain component, which was a script in Unity 3.5 and is dropped on load,
   with the original tree, detail and LOD distances. It points the tree and Slender Man
   materials at Unity's built-in shaders and removes leftover missing-script components.
   Then it checks the scene for missing scripts, null materials and unsupported shaders,
   and fails the build if it finds any.
6. Builds an arm64, Mono, gamma color space macOS player with `BuildMac`.

[docs/PORT.md](docs/PORT.md) explains the decisions behind this: why Unity 2022.3, why not
AssetRipper's version conversion, and what was checked.

## Known differences from the original

- **Shaders are Unity built-ins.** The originals only exist as D3D9 bytecode, which no
  available tool decompiles on macOS. The game mostly used stock Unity shaders anyway.
- **Slender Man's material** used a custom `Skin/Diffuse` shader with rim lighting. It now
  uses `Legacy Shaders/Diffuse`, so his face looks a little flatter. This is the most
  visible difference.
- **Shadows are on and vSync is on.** The 2012 launcher let you pick a quality level and
  defaulted to Fastest. Modern Unity has no launcher, so the build uses the project's top
  quality level. vSync matters: the ambient scare-sound timer counts frames, so without it
  the sounds would play too often.
- **Retina rendering is on.** The menus are laid out in raw pixels, so they look smaller on
  a HiDPI display than they did in 2012. Set `macRetinaSupport = false` in
  `unity/Editor/BuildMac.cs` if you'd rather have the original proportions.

## Legal

_Slender: The Eight Pages_ is © 2012 Mark J. Hadley / Parsec Productions. This project is
not affiliated with or endorsed by them. No game assets, binaries or decompiled game code are
distributed here. You need your own copy of the game. Don't redistribute the `Slender.app`
you build; it contains the game's assets.

The scripts in this repository are MIT licensed (see [LICENSE](LICENSE)). AssetRipper is
GPL-3.0 and is downloaded from its official GitHub release, not bundled. Unity is used
under your own Unity license.
