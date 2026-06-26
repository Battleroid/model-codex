# Model Codex

[![CI](https://github.com/Battleroid/model-codex/actions/workflows/ci.yml/badge.svg)](https://github.com/Battleroid/model-codex/actions/workflows/ci.yml)

A desktop tool to **browse, preview, and export 3D models** (with their mapped textures) from Bungie's
*Marathon* (2025) — the third in a series alongside
[texture-codex](https://github.com/Battleroid/texture-codex) and
[audio-codex](https://github.com/Battleroid/audio-codex), in the spirit of
[Deimos](https://github.com/cohaereo/Deimos-Public), [MIDA](https://github.com/DeltaDesigns/MIDA), and
[Charm](https://github.com/MontagueM/Charm).

It reads the game's Tiger Engine `.pkg` packages directly (AES‑128‑GCM + Oodle), decodes the static
render‑model geometry, couples each mesh part to its material's textures, and lets you preview models in
an interactive 3D viewport and **export to glTF / OBJ / FBX / STL** — for Blender, CAD, and other DCC tools.

> **Own, clean‑room implementation.** The Marathon model format was reverse‑engineered with the bundled
> `Probe` harness and validated against retail data and external tools. No code from Deimos / MIDA / Charm
> is vendored or copied — they were used only as documentation of the file format.

## Features

- **Browse the static‑model catalogue** (~54,000 models) by package (`outpost`, `marathon_deck_001`,
  `perimeter`, `sr_gear`, …), each grid cell showing the actual model in an isometric view that **orbits
  as you hover**.
- **Live 3D preview** of the selected model with its in‑game materials/textures, plus a channels list of
  the resolved texture maps.
- **Open in a tab** for a full interactive inspector — orbit camera (perspective by default, **isometric
  toggle**), textured / wireframe / grid toggles, FOV, and a view cube.
- **Export** a model — or a whole package in bulk — to **glTF 2.0 (`.glb`, textures embedded)**,
  **OBJ + MTL**, **FBX**, or **STL**, namespaced by category. Set the default format and export folder in
  Settings.

## Requirements

- **Windows x64** and a **.NET 8 desktop runtime**, or a self‑contained build (below).
- A local **Marathon install** — the tool uses the game's own `bin\x64\oo2core_9_win64.dll` (Oodle) to
  decompress packages. Nothing from the game is redistributed.

## Running

```
Run.bat            # builds + launches the Release build
```

or from source:

```
dotnet run --project src/App/ModelCodex.App.csproj -c Release
```

On first launch the game folder is auto‑detected at `A:\Steam\steamapps\common\Marathon`. If yours is
elsewhere, open **Settings → Browse…** and pick the Marathon install folder (the one containing
`packages\` and `bin\`).

## Setup / packaging

C#/.NET 8 + **WPF** with **[HelixToolkit.SharpDX](https://github.com/helix-toolkit/helix-toolkit)** for the
DirectX‑11 viewport — export via **[SharpGLTF](https://github.com/vpenades/SharpGLTF)** (glTF) and
**Assimp** (FBX). The Tiger package layer is shared with texture‑codex.

- **Fresh machine, from source:** `powershell -ExecutionPolicy Bypass -File scripts\setup.ps1`
- **Standalone, no .NET install needed:** `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`
  produces a self‑contained build in `publish\`.

## How it works

```
.pkg (Tiger Engine packages, header v53)
  ↓ TigerPackage  — AES-128-GCM block decrypt + Oodle (oo2core_9) decompress
static model tag (type 8, class 0x80808635 = SStaticMesh)
  ↓ StaticMesh.Parse — SStaticMesh → SStaticMeshData → parts / buffer groups
vertex buffer (32/4 header {DataSize, Stride, Type}) + index buffer (32/6 header)
  ↓ decode packed int16 positions → × ModelTransform; UVs → × TexcoordScale
material (type 8) → texture refs (scan + hash64) → TigerTexture decode (BCn / RGBA)
  ↓
HelixToolkit Viewport3DX preview · isometric grid thumbnails · glTF/OBJ/FBX/STL export
```

The package reader (`src/Tiger/TigerPackage.cs`, `Oodle.cs`) and texture decode (`TigerTexture.cs`) are
shared with texture‑codex; the model layer (`src/Tiger/Model/`) is new. The format offsets were
reverse‑engineered against the retail packages — see `src/Probe` for the discovery harness
(`hist`, `model`, `mesh`, `stats`, `thumb`, `texmodel`).

## Fonts

The in‑game typefaces (PP Fraktion Mono, MarathonShapiro Wide 65) are extracted from Marathon and **not
redistributed**; they're git‑ignored. The app falls back to a system font if absent. To restore the
in‑game look, drop the `.otf` files into `src/App/Assets/Fonts/`.
