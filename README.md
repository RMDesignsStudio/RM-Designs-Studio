<h1 align="center"><img src="Swapper/docs/brand/rm-designs-studio.svg" alt="RM Designs Studio" height="34"></h1>

Modding tools for F1® 25 by RM Designs Studio: a swapper for SERPs templates and Simplified Studio projects, and a
helmet swapper, built on one shared engine (Studio Core) and one shared window look (Studio UI).

## Apps

| Folder | What it does |
|---|---|
| [Swapper](Swapper) | Gives a SERPs template or Simplified Studio project a new chassis, steering wheel, rims, engine sound and/or suits, with a live 3D preview. Every swap is added as a new section; your own sections are never changed. |
| [Helmet Swapper](Helmet%20Swapper) | Puts one driver's helmet (design, visor, wing, attachments; any make) onto another, F1 or F2, 2024-2026, with a live 3D preview and 3D model export. |

| Library | Used by |
|---|---|
| [shared/src/Studio.Core](shared/src/Studio.Core) | Studio Core: ERP and SERPs reading and writing, the game install, car models, `.simplified` projects, the 3D preview scene and its WebGL2 viewer, the texture pipeline. Used by the Swapper. |
| [shared/src/Studio.Ui](shared/src/Studio.Ui) | Studio UI: the borderless RM Designs window (the brand band is the title bar) and its window buttons. Used by every app. |
| [shared/external/EgoEngineLibrary](shared/external/EgoEngineLibrary) | Petar Tasev's EGO engine library: the game's ERP archives and their textures, models and data. |
| [Wheel Swap Engine](Wheel%20Swap%20Engine) | The steering wheel engine: grafting a donor wheel into a car, verifying every build, the build report and the wheel pictures. Used by the Swapper. |

## Licence

GPL-3.0 - see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md). The Swapper contains code adapted from Simplified Suite by
Team Simplified, with their permission, so it carries their notice and credit:

**Based on Simplified Suite by Team Simplified - https://github.com/TeamSimplified/simplified-suite**

If you share a modified version of anything here, share its source under the GPL-3.0 too and keep the credits.

## Credits

- **Petar Tasev** ([github.com/ptasev](https://github.com/ptasev)): author of EgoEngineLibrary, part of
  [Ego Engine Modding](https://github.com/EgoEngineModding/Ego-Engine-Modding), which reads and writes the game's ERP
  archives and their contents. Every app here is built on this work.
- **Team Simplified** ([github.com/TeamSimplified](https://github.com/TeamSimplified)): the `.serps` template and
  `.simplified` project formats, and the chassis, driverwear and engine sound swapping and game folder detection in the
  Swapper, adapted from [Simplified Suite](https://github.com/TeamSimplified/simplified-suite) with their permission.

## Building

Each app is its own solution (`*.slnx`) in its folder, targeting .NET 10 (Windows). The apps and `shared` must stay side
by side, as they are here. How the folders fit together is described in [STUDIO-CONVENTIONS.md](STUDIO-CONVENTIONS.md).

```
dotnet publish "Swapper/src/Swapper.App" -c Release -o dist -p:DebugType=none
dotnet publish "Helmet Swapper/src/HelmetSwap.App" -c Release -o dist -p:DebugType=none
```

Each publish gives a single self-contained exe.

## Game files are not included

This repository distributes no files from the game. The apps read what they need from your own F1® 25 install. Nothing
in the game folder, and nothing in the templates or projects you open, is ever changed: every result is a new file.
The tests read your game install and templates; they skip themselves when those aren't found (set the folders with the
environment variables named in each app's `tests` folder).

F1® 25 is a trademark of its owners. This is an unofficial, fan-made project, not affiliated with or endorsed by
Electronic Arts, Codemasters, Formula 1 or the FIA.
