# Tekla Drawing Assistant

Prototype drawing automation tool for **Tekla Structures 2023**. The drawing rules are kept separate from the Tekla session plumbing so the geometry and dimensioning logic can be expanded without tying the rules to one Tekla API call path.

## What this first version does

- Connects to the currently open Tekla Structures 2023 model and drawing.
- Supports assembly drawings and single-part drawings.
- Resolves the drawing main part from the model.
- Reads every drawing view and checks whether the main part is visible.
- Classifies main-part views as **Web**, **Flange**, **End**, or **Unknown** from the view direction relative to the main-part coordinate system.
- Collects visible bolt/hole positions from the actual model bolt groups.
- Reports parts, bolt groups, hole points, dimension sets, marks and welds per view.
- Builds a rule-based proposed dimension plan from the fabrication context.
- Creates rule-based straight dimension sets:
  - **Web view:** longitudinal holes from the left end; vertical holes from the top of the member.
  - **Flange view:** longitudinal holes from the left end; transverse holes from the member centreline.
  - **End view:** horizontal holes from the member centreline; vertical holes from the top of the member.
- Can optionally remove existing straight dimension sets in a processed view before rebuilding.

## Deliberate limitations of the current prototype

This is the geometry/dimensioning foundation, not the finished drawing automation tool.

Still to add:

1. Distinguish top flange / bottom flange and select the best side for dimensions.
2. Group holes by face/plate so unrelated bolt patterns are not chained together.
3. Detect existing dimensions semantically instead of deleting/rebuilding them.
4. Dimension secondary plates and fitting extents only where the drawing rules require it.
5. Detect clashes between dimensions, marks, weld notes and geometry.
6. Reposition marks and weld notes into clear lanes.
7. Handle section/detail views with their own rule sets.
8. Load preferred Tekla dimension attribute files instead of default straight-dimension attributes.
9. Process drawings selected in Document Manager in a batch rather than only the currently open drawing.

## Build

- Visual Studio 2022
- .NET Framework 4.8
- x64
- C# 7.3
- Tekla Structures 2023.0 installed locally

The project references the Tekla 2023 assemblies directly from:

`C:\Program Files\Tekla Structures\2023.0\bin`

The location is held in the `TeklaInstallDir` MSBuild property in the project file, so it can be overridden if Tekla is installed somewhere else.

The referenced assemblies are:

- `Tekla.Structures.dll`
- `Tekla.Structures.Model.dll`
- `Tekla.Structures.Drawing.dll`

This matches the direct-reference approach already used by the Tekla 2023 tools in this account rather than restoring the Tekla 2026 NuGet packages.

## First test

Use a simple assembly drawing with one beam and a couple of bolted plates:

1. Open the model in Tekla Structures 2023 and open the assembly drawing.
2. Run **Analyze Drawing** first and check the detected Web / Flange / End view types and the proposed dimension plan.
3. Leave **Delete existing straight dimensions** OFF for the first test.
4. Run **Rebuild Hole Dimensions**.
5. Inspect which dimension sets are correct and which should be split into separate logical groups.

The next development step should be driven by real assembly drawings so the grouping rules match the drawing-office standards rather than trying to guess every case generically.

## Version strategy

Tekla Structures **2023 is now the primary target** for this repository. Keep new Tekla API usage compatible with the 2023 assemblies unless the project is explicitly retargeted later.
