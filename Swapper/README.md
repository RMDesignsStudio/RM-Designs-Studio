<h1 align="center"><img src="docs/brand/swapper.svg" alt="Swapper" height="44"></h1>
<p align="center"><img src="docs/brand/rm-designs-studio.svg" alt="RM Designs Studio" height="18"></p>

An RM Designs Studio app for F1 25 SERPs templates: give one a new **chassis**, **steering wheel**, **rims**,
**engine sound** and/or **suits** (any of the five) and save it as a new .serps. The file you open is never changed. It
has a live 3D preview beside the picks.

Swaps never overwrite your work: each chassis option that gets a new wheel is copied, with everything in its folder
(livery, numbers), into a new section named after the wheel ("Livery - Default Chassis - 26 Ferrari Wheel", same
order and group, so it sits right after the original as an alternative). The original is kept byte for byte. A section
this app made is changed where it is when it gets another wheel; the same option given the same wheel again replaces
its earlier section. Templates without option folders have no sections, so there the wheel goes onto the chassis where
it is, in the new file.

Opens and saves `.serps` templates only. Simplified Studio projects (`.simplified`) are neither opened nor written, as
Team Simplified asked: projects stay Simplified Studio's, which exports them as a .serps that Swapper can then open
(`TemplateOutput.Problem` refuses both, in the app and on the command line).

Successor to the Steering Wheel Swapper, on the same engine. The Steering Wheel Swapper stays the stable app until
this one does everything it does, better.

## Status: v1.2.2

| Pick | State |
|---|---|
| Steering wheel | any 2026 or 2025 car's wheel, both seasons' FOM chassis included (the 2025 FOM car's model is in that season's `common_fom.erp`), onto 2026 or 2025 cars (a wheel of the other season carries its driver clips in the entry; `anySeason`, Swapper only: the Steering Wheel Swapper app keeps 2026 cars only); the Steering Wheel Swapper's engine: built onto the new chassis when one is picked, else onto the ticked chassis options (each a new section, `... - 26 X Wheel`), verified, optional report; the wheel, driver position and gear stick keep the car's own placement (`carPlacement`), so the wheel sits as far from the driver as the car's own did |
| Chassis | Team Simplified's chassis swap, ported: any car of the template's season (2026 cars and special liveries; 2025 cars for a 2025 template) as a chassis option of its own, `NN--01--Livery - <car> Chassis`; then drawn at its own height: the team's handling files come along with the donor's four drawing values (`CarLook`; without it an Aston Martin in the Cadillac slot sat 33 mm low, and the garage monitors floated over its nose). My Team too: the car goes into the FOM car's slot, the engine slots show it and its binding answers every My Team livery, number and sticker name (`MyTeamChassis`; proved in game, Career included); it shows its own livery |
| Rims | the road wheels' rims (and hub centres) of any car of the same season, special liveries too, on the new chassis or the ticked options, FOM options and My Team liveries included (`... - X Rims`); the car keeps its axles, uprights and tyres, and each rim keeps the car's own whole-wheel bounding sphere (a tight one sat the car too low on track) (`RimSwap`) |
| Engine sound | any power unit's sound of the 2026 and 2025 cars (named by maker, with the car it comes from: each car's sounds a little different), the FOM car's and the My Team suppliers', on the new chassis or the ticked options (`... - Ferrari Engine (Haas 26)`): only the car-model layer's Wwise switch changes, as in Team Simplified's chassis swapper (`EngineSwap`); not for My Team liveries, whose sound comes with the engine supplier picked in game |
| Suits | Team Simplified's driverwear swap, ported: any set (2024-26, F1 and F2, specials) for the template team's drivers, as a driverwear section of its own, `NN--02--Driverwear - <set>`; My Team too (the set's suit geometry in the template's My Team driverwear, `MyTeamSuitSwap`); each set's maker shown; standing-figure preview |

FOM options (the team's slot on the game's FOM car) and My Team liveries: how they are built and what each swap does
with them is in [docs/FOM.md](docs/FOM.md).

Every list has hover previews (drawn once by the 3D viewer from the game files, smallest game file first, each scene swapped into one open viewer page: about a minute for all of them, cached in `%LOCALAPPDATA%\RM Designs Studio\Swapper`; the "i" next to the checkbox explains this in the app).
Any mix of the four builds into one new file (`SwapBuild`), checked before it is written and read back after. Each
swap matches the swaps made with Team Simplified's own app resource for resource (tests: ChassisTests, SuitTests).
Stages 0-4 were each tested in game. Sharing: `py -3 tools/make_share.py` after publishing (zips in `share/`).

## Layout

- `src/Swapper.Core`: what the game offers (`Catalog`: cars with their special liveries, wheels, suit sets), the
  template's team (`TeamSimplified/TemplateTeams`), the chassis and suit swaps (`TeamSimplified/ChassisSwap`, `SuitSwap`), one build
  for any mix of the three (`SwapBuild`), where
  a swap goes in the template (`Sections`), saving and checking (`TemplateOutput`), the preview's cars (`CarView`). The wheel is built by
  `../Steering Wheel Swapper/src/WheelSwap.Core`, used as is.
- `src/Swapper.App`: the WPF app (borderless Studio window from `../shared/src/Studio.Ui`, preview column with a
  divider and pop-out, the shared 3D viewer in WebView2).
- `src/Swapper.Cli`: `swapper inspect|cars|suits|wheels|suit|build|preview`.
- `tests/Swapper.Tests` (run the built exe), `tests/gui_smoke.ps1` (UI Automation; the window opens behind your
  others and never takes the focus).

The app needs its siblings in the `RM Designs Studio` folder: `../shared` and `../Steering Wheel Swapper`.

## Credits

- **RM Designs Studio**: design and development of Swapper, including steering wheel and rim swapping, the 3D preview,
  and build verification and reporting.
- **Team Simplified**: chassis, driverwear and engine sound swapping and game folder detection, adapted from
  Simplified Suite with their permission (the ported files are in `src/Swapper.Core/TeamSimplified/` and state their
  origin), and the `.serps` template and `.simplified` project formats.
- **Petar Tasev** ([github.com/ptasev](https://github.com/ptasev)): author of EgoEngineLibrary, part of
  [Ego Engine Modding](https://github.com/EgoEngineModding/Ego-Engine-Modding), which reads and writes the game's ERP
  archives and their contents (textures, models and PKG data). Every swap in Swapper is built on this work.
- **Google**: the information icon, from [Material Symbols](https://github.com/google/material-design-icons) (Apache
  License 2.0).

Based on Simplified Suite by Team Simplified - https://github.com/TeamSimplified/simplified-suite

## Licence

Copyright (C) 2026 RM Designs Studio and Team Simplified. Licensed under the GNU General Public License, version 3.
Simplified Suite's notice, including its attribution term (GPL-3.0 section 7(b)), is kept in
[NOTICE-Simplified-Suite.md](NOTICE-Simplified-Suite.md). If you share a modified version, share its source under
the same licence and keep that credit.

Third-party parts: EgoEngineLibrary by Petar Tasev (MIT, in `../shared/external`; see Credits), Zstandard by Meta (BSD), Roboto Mono
by Google (Open Font License). F1® 25 is a trademark of its owners; this is an unofficial fan-made tool.
