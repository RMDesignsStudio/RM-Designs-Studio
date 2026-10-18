# Helmet Swapper (F1 25, 2026 season)

Step 1 (done): **per-driver helmet SERPs templates** for the 22 2026 F1 and 23 2026 F2 drivers, generated from the game files
(`out/Helmet Templates`, `out/Helmet Templates F2`; F2 files are named exactly like F1 ones).
Step 2 (done, confirmed in-game): **whole-helmet swap**, app `dist/Helmet Swapper.exe`: design, materials and the
driver's own 3D helmet parts (wing, visor attachments, clear parts).
Step 3 (done, confirmed in-game): **manufacturer swap**: a Bell, Schuberth or Stilo helmet onto any driver.

## Helmet swap (app)

Pick the driver to change and whose helmet to give them, then Build.

2025 drivers can be changed too: "Driver to change" has a 2026 / 2025 tab (2025 = the regular F1 and F2 grids, every
special-livery driver, e.g. Leclerc Monza or Ocon Montreal, and the story drivers); file names mark them:
`Charles Leclerc 2025 - Schuberth Swap (Max V)`, `Charles Leclerc Monza 2025 - Bell Swap (Lando N 2024)`.

**Parts from several helmets** (`PartPicks`): the app's Parts row has one slot per part saying whose it is (a helmet, or
"own"); clicking slots aims the next helmet picked at those parts only (none aimed = the whole helmet, the classic swap),
x puts a part back to the driver's own, the preview's part buttons switch a slot on/off. Each part comes from its own
helmet: materials and textures per part, the parts model cut per driver and merged (any number of drivers), the shell
from the Shell part's helmet; the rules (`PartsProblem`) apply per donor; the warning names every part shaped for another
shell than the one the helmet ends up with. Files: `Charles Leclerc - Mixed Swap (Lando N, Nico H).serps`, each part
folder naming its own donor (the 3D Model folder every helmet whose shapes it holds). One helmet for every part builds
byte-identically to before. CLI: `helmetswap swap --driver <t> --mix design:<a>,visor:<b>,wing:<c>` (preview `--page` and `export` take `--mix` too).

Parts to swap: Shell (the paint, enum `Design`; with another make, the shell itself too), Visor (tint, tear-offs), Visor strip (the sponsor strip
above the visor: the parts model's "visor_attachments" surface and material), Wing (the rear wing only: the "wing" surface and
the clear triangles inside the wing zone, y > 1.64 m and z < 0.03 m) and Attachments (everything else in the parts model: the
clear vents and chin strip, and the "wing" surface outside the wing zone - every Schuberth's front/chin vents, painted on
its wing texture; a few Bells have such pieces too). `HelmetParts.ClassOf` sorts each triangle into one of the three; the
parts model is cut per driver and merged (the attachments driver first, then the wing's, then the visor strip's).
Front vents and wing from different drivers: the attachments driver's vents move to the parts model's spare
`helmet_attachments` surface (bound in every helmet binding, unused by the game's meshes), whose material becomes a copy of
that driver's wing material, so each keeps its own texture (`VentsFrom` / `MaterialJobs`; the verifier checks it).
Each chip shows the helmet's make and wing (solid wing, clear wing or no wing). Any combination works across makes; with
another make the app warns that parts shaped for one shell may not sit perfectly on the other, and the Preview shows it.
Partial files are named by the parts: `Charles Leclerc - Wing Swap (Lando N)`, `... - Visor strip + Attachments Swap (...)`.
The verifier also checks that every part not picked is exactly the game's. CLI: `--parts design,visor,visorstrip,wing,attachments`.

Preview (`Core/HelmetPage.cs`): the Preview button writes a self-contained WebGL2 page to `%TEMP%\HelmetSwapper\Preview - <name>.html`
and opens it in the browser (CLI: `preview --driver A --from B [--parts ...] --page FILE.html`). Before / After / the donor's
own / side by side; the four parts switch on the page; studio Purple / Grey / White; orbit, pan, zoom, preset views.
Materials as the game's helmet shaders use them: colour, normal map (RG, Z rebuilt), surface map (R occlusion,
G roughness, B metallic); the visor is tinted glass (me_alpha, metallic visors reflect in their tint colour); the clear
parts (char_helmet_attachment, IOR 1.4, roughness 0.3) refract a resolved copy of the scene, slightly frosted. A dark
head sits inside the shell as in the game. 4x MSAA, a shadow-mapped floor. About 5-7 MB per page.
Hover previews (as Swapper's): each chip shows its helmet in the preview's 3/4 view on a see-through background, drawn
once by the preview's own renderer (`App/HelmetPictures.cs`: a hidden WebView2 with `HelmetPage.SnapPage`, loaded once; each
helmet's data, `HelmetPage.SnapData`, is fetched by it and the picture sent back), cached in
`%LOCALAPPDATA%\HelmetSwapper\helmets`. All of them are made in the background after the game loads, smallest helmet file
first (about a minute for ~180), with a "making previews · n% · m:ss left" line; the "Hover previews" box turns them off
(and the "i" next to it explains them). Tests: `HELMETSWAP_PICTURES` = another picture folder (+ times.log),
`HELMETSWAP_TEST_TIP=<folder>` opens that chip's preview without the mouse. Switching helmets dims the preview under a
loading layer ("building the preview…", `PreviewHost.ShowBusy`) until the next one is drawn. The smoke test sets
`HELMETSWAP_NO_BROWSER=1` so the page is written, not opened.

Credits: the information icon is from Google's [Material Symbols](https://github.com/google/material-design-icons)
(Apache License 2.0).

v1.4: the preview is also built into the app, as a right-hand column (`App/PreviewHost.cs`: WebView2 showing the same page in
embedded mode, served from memory at https://helmet.local/). It follows the picks: both picked = the swap, one picked = that
driver's own helmet; the part boxes switch the parts by message, without rebuilding. The column needs a work area at least
1300 px wide, otherwise it is hidden and "Open in browser" remains. WebView2 adds about 0.4 MB to the exe and runs only
while the window is open (its data in `%LOCALAPPDATA%\HelmetSwapper\WebView2`).
Search and filters above each list: text (name, team, make, year, series, wing), make tabs and a team dropdown. They
combine with the season and series tabs, which each have an "All" tab (all seasons: newest first, the year on each chip).
A divider between the picks and the preview column drags to resize the preview (the picks column keeps 880 px at least; the
width is remembered in settings as FormWidth). The preview's part buttons and the app's tick boxes are kept in step both ways. "Pop out preview" moves the preview into its own
resizable window (`App/PreviewWindow.xaml`); closing it puts it back. The main window can be narrowed to 960 px: below
1250 px the column hides (the preview can still pop out). The dark head inside the shell stands in for the driver behind the
visor; it is sized to stay inside every make's shell (checked: no shell vertex inside it, its bottom above the rear rim).

Older helmets: the "Helmet from" list has a 2026 / 2025 / 2024 tab (2024 helmets can be given, not changed). 2025 and 2024 bring those seasons' F1 and
F2 grids, the Braking Point story drivers (Konnersport, Zenith) and the 2025 special-livery helmets that really differ
(Leclerc and Hamilton Monza, Verstappen Japan); helmets identical to a newer one or to the regular livery are left out
(fingerprint: make, helmet texture pixels, helmet-parts structure). 2024 adds the Stilo ST5. File names say which helmet:
`Charles Leclerc - Bell Swap (Lewis H 2024)`, `... (Max V Japan 2025)`, `... (Liam L Racing Bulls 2025)`. Each list has an F1 / F2 tab, so swaps work within
and across series; every chip shows the driver's helmet make (bell, schuberth, stilo, arai). Any driver can give their
helmet: F1 has Bell HP7 (14), Schuberth SF2 Pro (5), Stilo ST6 (3); F2 adds the Arai GP-6.

Career helmets (Career tab on both lists; they have no season, so the season tab doesn't apply to them):
- **Replicas** (`livery_package/replica/replica_<driver><yy><a|b>`, 18, plus Novelty Hamilton 2): built like a driver's
  helmet (full-colour design, own helmet parts model, own bindings), so any helmet, any make and any part can go onto
  them and come from them. Root `eaid://livery_package/replica/<f>/`, textures under
  `eaid://livery_package_shared/replica/<f>/textures/`; there is no body ERP.
- **Standard designs** (`tier01..05`, `unassigned`, 47): colours are picked in game (generic `charname_helmet_d` +
  the design's own mask), so they can be changed, not given. They wear the Arai GP-6 (one wears a Bell HP7), whose mesh
  is in `livery_package/tier_common_helmet.erp`, so a design must come from a helmet of the same make; a visor from any.
  Most share the My Team helmet parts model (`noyear-custom-myteam_helmet_attachments.idf`, which also carries the
  in-game logo/badge decals and shares surface names, so it is read, never rebuilt): for those the app unticks and locks
  the visor strip, wing and attachments. 13 have their own parts model and take those parts too.
- In the app the standard designs are **one "Career Design" chip** (they're the same helmet in different in-game
  slots); a picker under the list chooses which slot it replaces (default Career Design 52 · Tier 1, remembered as
  `DesignSlot`). The player selects that design in career to see the swap; the file name says which.
- The preview shows a standard design's "before" in plain white (the in-game colours are the player's choice).

3D: shells are shared per make, but every driver has an own helmet-attachments model (`<driver>_helmet_attachments.idf`,
one mesh, skinned to bone_helmet_main / bone_helmet_visor like the shell). The swap writes the donor's attachments mesh
into it (buffers under private names, node groups re-pointed by node name, bounds updated). For another make the
donor's shell (LOD 0: helmet, visor, tear-off) is merged into that mesh too, and the target's own shell is hidden for
the target only: their shell binding (`helmet_<shell>.emb?context=<driver folder>`) points the shell's slots at private
copies of the game's tear-off material with opacity 0 and refraction 1 (roughness left at 0: higher roughness shows as
static where the old shell pokes through). All three makes use the same 9-bone palette, so no bone remapping is needed.
Detail levels: shells have 5 (drawn to 4 / 10 / 24 / 58 / 524), the helmet parts model has 1 (drawn to 100). For a
different make the donor shell lives in the parts model, so its draw distance is raised to the shell's 524 (v1.2.1);
before that the driver showed no helmet beyond 100. Same-make swaps are unchanged (the shell keeps its own levels). The output is a helmet template for the
driver being changed: `<Driver> - <donor make> Swap (<donor first name + initial>).serps` (e.g. `Charles Leclerc - Schuberth Swap (Max V).serps`), same zip comment as the step-1
templates. Inside, one folder per helmet part (`Core/HelmetSerps.cs`), each with its own ERP, a part taken from the donor
marked like the Steering Wheel Swapper marks a swapped wheel:

```
ERPs/01--00--Base - 26 Max Verstappen/        design: helmet material + textures, the shell, material bindings
ERPs/02--00--Visor/                           visor + tear-offs
ERPs/03--00--Visor Strip/
ERPs/04--00--Wing - 26 Max Verstappen/
ERPs/05--00--Attachments/
ERPs/06--00--3D Model - 26 Max Verstappen/    the parts model (wing, strip, attachment shapes; another make's shell)
```

The whole helmet is in the file: parts not swapped carry the driver's own textures at full resolution under their game
names (as a template), so every part can be edited in SERPs Studio. A part with nothing of its own (attachments in the
game's shared clear plastic, a model that didn't change) has no folder. Donor tag: `<yy> <name>[ <variant>]`, career
helmets without the year (`Lewis Hamilton 2008 Replica`). The verifier reads all the ERPs together and also checks each
resource sits in its part's ERP and every own texture the finished helmet uses is there with the game's pixels.

What it carries, per helmet part (helmet, helmet_wing, visor, visor_attachments): the donor's material, written as the
target's own material, and the donor's own textures embedded at full resolution under **private names** inside the
target's folder (`.../<target folder>/hsw/<hash>_helmet_d.tif`), so the donor's helmet and the donor's own template are
untouched. Where the donor uses the game's shared (generic) material, its settings are copied; where the target uses a
shared one, it gets a private copy and the target's own material bindings are pointed at it (same-length name swap, so
the binary layout is unchanged). Nothing shared by other drivers is ever written.

Every build is re-opened and verified before it is saved (failures save nothing): for every part the target's
effective material equals the donor's byte for byte apart from the private texture names, every private texture is the
donor's full-resolution texture with the donor's pixels, bindings only change shared -> private, hashes valid, nothing
donor-named or shared. `tests/out/swap_matrix_all.log`: all 462 driver pairs pass (208 same make, 254 other make). `tests/gui_smoke.ps1` drives the exe.

CLI: `helmetswap swap --driver leclerc --from norris --out DIR`.

## Names from the game

`Core/GameText.cs` reads the game's English text (`localisation/<build>/language_eng.lng`: "LNGT", then tagged blocks;
SIDA = u32 count + (u32 key offset, u32 text offset) pairs, SIDB = keys, LNGB = UTF-8 texts) so helmets carry the names
the game shows: career designs `lng_<folder>` ("Teal", "Heatsink", ...; file names "Career Helmet Teal Tier 1 - ..."),
replicas `lng_helmet_<folder>` / `lng_<folder>` / `lng_replica_<driver>_<yy><x>` / `lng_livery_<folder>` with the driver's
name taken out ("Lewis Hamilton · 2008", "Max Verstappen · Orange Lion 2025", "Lewis Hamilton · Roscoe", "F1® 75").
"zenith" in the files is the F1 movie's **APXGP** team (`lng_zenith`): zenith_driverfirst / second / third = Sonny Hayes,
Joshua Pearce, Luca Cortez (`lng_zenith_driver_N_fullname`); replica_zenith25a / b = Sonny Hayes's / Joshua Pearce's
helmets (their signatures). Without the file the names fall back to the folders'.

## 3D model export

**Export 3D model** (app; CLI `helmetswap export --driver a [--from b] [--parts ...] --out DIR`, `Core/HelmetExport.cs`)
writes the helmet the after view shows (the driver's own when no valid helmet is picked to give) into
`<name> - 3D Model/`: `<token>_helmet.fbx` (binary FBX 7.4, written directly) and `.obj` + `.mtl`, metres, Y up.
One object + material per Painter texture set, named after the game's helmet surface and the same on every helmet
(as other helmet exporters name them), so a Painter project re-imports any exported helmet and keeps its texture sets:
`helmet` (shell), `helmet_wing` (wing; Schuberth front vents too), `visor_attachments` (strip), `visor`, and
`helmet_attachments` when the swap moves front vents to their own surface.
Also `visor_tear` (the tear-off films) and `helmet_transparent` (the clear plastic parts): their
materials only have the game's blank placeholders, so they get a material and a UV layout (`UVs/`, 2048 px) but no
maps. Nothing sits flush (visor strip 1.4-5 mm above the shell, tear-offs ~2 mm off the visor, touching only at
their edges), so no offset is needed and the geometry is the game's. `textures/`: each set's colour, normal
(Z rebuilt from BC5, DirectX green) and surface maps at full resolution (the maps only); `UVs/`: the UV
wireframes (tiny maps such as an 8 x 8 visor tint get a 1024 px+ wireframe). The game's shared placeholder normal /
surface maps are left out. Checked by importing both files in Blender 5.0 and rendering (matches the preview).

## Helmet templates

`out/Helmet Templates/2026 <Driver> Helmet - SERPs Template v1.0.serps` — one per driver. Each holds the driver's own
helmet textures (helmet, wing, visor, visor attachments — colour / normal / specular as the game has them) at full
resolution, under the game's own names, so an unedited template changes nothing in-game. Where a driver has no texture
of a kind, the game uses a shared generic one; templates leave those out on purpose (editing them would change every
driver).

Layout (as Team Simplified's v1.1 templates): `ERPs/01--00--Helmet/01--00--26-<team>_<driver>_helmet.erp` +
`template.serps` (author blank, studio_version 2.0); zip comment `{"erp_file_string":"character_helmet.nefx2.sm51"}`
(the helmet shader ERP, as the My Player Helmet template uses).

Regenerate: `helmetswap templates --out "out/Helmet Templates" [--year 2025] [--series F1|F2] [--drivers leclerc,verstappen] [--game DIR]`
(2025 templates: `out/Helmet Templates 2025`, 73 drivers incl. special liveries and story drivers; none for 2024).
(`helmetswap drivers` lists the drivers and their helmet shells).
## How helmets work in the game (findings)

- Per driver (`character_package/drivers/male/2026-<team>_<driver>/`): `<driver>.erp` (body) and `<driver>_helmet.erp`
  (helmet attachments model, helmet/wing/visor textures, materials, material bindings).
- The helmet **shell** is shared: Bell HP7 (14 drivers), Schuberth SF2 Pro (5), Stilo ST6 (3), stored as
  `kitpart_assets/generic/helmets/<shell>/` and copied into each driver's helmet ERP. Which shell a driver wears is set
  in the game database (`game_package/gamedatabases/gamedatabases.2025.bin`), not in the driver's files.
- Shell, visor and attachments are skinned to `bone_helmet_main` / `bone_helmet_visor` on the driver's head.

## Build

`dotnet build HelmetSwapper.slnx -c Release`; app: `dotnet publish src/HelmetSwap.App -c Release -o dist` (.NET 10 SDK). `external/EgoEngineLibrary` is a vendored copy of Petar
Tasev's MIT-licensed library (see its LICENSE). Nothing in the game folder or your templates folder is ever modified.

Window frame (main window and the popped-out preview): borderless, from the shared `Studio.Ui` (`../shared/src/Studio.Ui`): no Windows title bar, the purple band is the title bar (drag to move, double-click to maximise) with the minimise / maximise / close buttons at its right end; resizing, snapping (incl. Windows 11 snap layouts), rounded corners and maximise work as on any window. It lives in the RM Designs Studio shared folder, so this app builds with `../shared` beside it (`$(StudioShared)` in `Directory.Build.props`).
