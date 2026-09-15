# Tekla Drawing Assistant

Prototype drawing automation tool for Tekla Structures 2023, with the core drawing logic kept version-light so a 2026 adapter can be added later.

## What this first version does

- Connects to the currently open Tekla Structures 2023 model and drawing.
- Supports assembly drawings and single-part drawings.
- Resolves the drawing main part from the model.
- Reads every drawing view and checks whether the main part is visible.
- Classifies main-part views as **Web**, **Flange**, **End**, or **Unknown** from the view direction relative to the main-part coordinate system.
- Collects visible bolt/hole positions from the actual model bolt groups.
- Reports parts, bolt groups, hole points, dimension sets, marks and welds per view.
- Creates rule-based straight dimension sets:
  - **Web view:** longitudinal holes from the left end; vertical holes from the top of the member.
  - **Flange view:** longitudinal holes from the left end; transverse holes from the member centreline.
  - **End view:** horizontal holes from the member centreline; vertical holes from the top of the member.
- Can optionally remove existing straight dimension sets in a processed view before rebuilding.

## Deliberate limitations of v0.1

This is the geometry/dimensioning foundation, not the finished drawing AI.

Still to add:

1. Distinguish top flange / bottom flange and select the best side for dimensions.
2. Group holes by face/plate so unrelated bolt patterns are not chained together.
3. Detect existing dimensions semantically instead of deleting/rebuilding them.
4. Dimension secondary plates and fitting extents only where your drawing rules require it.
5. Detect clashes between dimensions, marks, weld notes and geometry.
6. Reposition marks and weld notes into clear lanes.
7. Handle section/detail views with their own rule sets.
8. Load your preferred Tekla dimension attribute files instead of default straight-dimension attributes.

## Build

- Visual Studio 2022
- .NET Framework 4.8
- x64
- C# 7.3
- Tekla Structures Open API NuGet packages `2023.0.1`

The project mirrors the setup used by the existing GalvVent project: SDK-style WPF, `TSAppConfigPatcherTask`, and a `TeklaVersion` of `2023.0`.

## First test

Use a simple assembly drawing with one beam and a couple of bolted plates:

1. Open the Tekla model and assembly drawing.
2. Run **Analyze Drawing** first and check the detected Web / Flange / End view types.
3. Leave **Delete existing straight dimensions** OFF for the first test.
4. Run **Rebuild Hole Dimensions**.
5. Inspect which dimension sets are correct and which should be split into separate logical groups.

The next development step should be driven by one or two real assembly drawings so the grouping rules match how your drawing office actually dimensions steel rather than trying to guess every case generically.

## Version strategy

Tekla 2023 is the primary production target because that is where the existing model base currently lives. The geometry and drawing-rule code is intentionally kept separate from session/model lookup code so a Tekla 2026 target can be introduced later without duplicating the dimensioning rules.
