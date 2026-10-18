# FOM swaps: how they work

A "FOM swap" puts the game's generic F1 car (the FOM car, game folder `teams/fom_car`) in a team's slot instead of the
team's own car. The 2026 SERPs templates ship one as the option `NN--01--Livery - FOM Chassis`. The My Team templates
are built entirely on it. Swapper doesn't make new FOM options, but it reads them and builds onto them. This note is what
the code relies on.

## What a template's FOM option is

Example: the Cadillac template, `03--01--Livery - FOM Chassis/`.

| Part | What it holds |
|---|---|
| `01--00--26-cadillac_car_FOM_livery-and-chassis.erp` | the **team's own slot** (`gm_cadillac/wep/*.layer`, `.wep`, event graph), with:<br>• `gm_cadillac-carmodel.layer` and `-carshowroommodel.layer` pointing at `fom_car/idf/fom_car.idf?context=render` (the game's FOM model, not a copy) and at the binding `fom_car/idf/fom_cad.emb?context=default`<br>• the binding, materials (`fom_car/materials/fom_cad_*`, `liveries/fom_cad_serps_custom_livery*`) and textures (`fom_car/textures/fom_cad_*`, `liveries/fom_cad_car_livery_26_paint_d/c/s`, carbon sheets, decals, steering wheel, rims `fom_cad_wheel_d/n/s`)<br>• the team's animset (`driver_in_car_2026_gm_cadillac`) re-pointed at the FOM clips `driver_in_car_fom_26` |
| `02--00--26-cadillac_car_FOM_numbers.erp` | 99 number contexts `fom_cad.emb?context=1..99` (each the full binding with `fom_cad_number_N`) |

- Which layers are whose (checked line by line against the Williams template, 2026-10-18):
  - `<team>-carmodel.layer` and `-carshowroommodel.layer` are the **team's own**, with `model` -> `fom_car.idf?context=render`
    and both `mat binding` lines -> `fom_<code>.emb` (`...?context=default`); the car-model layer **drops its
    `baseVtfAsset`** (the team's `fom_car_<team>.vtf`). The team's handling, engine sound (`bundleName`) and the layer's
    part offsets stay the team's.
  - `-camscargame`, `-camscarrepon`, `-camscargame_ss`, `-mirrors` and `-vfx` are the **FOM car's own** layers with
    `fom_car` renamed to the team (they fit the FOM car's shape).
  - The `.wep` and its event graph are the team's, unchanged.
- `fom_<code>` is a 3-letter code per team (`fom_cad`, `fom_fer`, `fom_alp`...), so the template's FOM materials never collide with the game's own `fom_car` ones.
- **The livery is painted for the FOM car's UV layout.** A team livery can't just be copied over: its paint sheet is laid out for the team car's UVs.
  - The game ships no team liveries for the FOM car.
  - `fom_car.erp` has the model, the 4,956 story / My Team binding contexts and the number sets, but no `fom_<team>` liveries.
  - So every FOM option's livery comes from the template author (Team Simplified).

## My Team

- A My Team livery ERP (`26-myteam_car_livery_paint-*.erp`) carries **no car**. It has one binding context of the game's `fom_car.emb` (e.g. `?context=livery_tier01_livery74`) with its materials and textures (`serps/myteam/...`, rims `myteam_a_wheel_*`).
- The game spawns the My Team car from the **`fom_car` slot**, and the six `myteam_<engine>` slots also carry a `fom_car` model. The livery binding dresses that car.
- Its numbers ERP binds the wheel to the Alpine template's `fom_alp_*` materials. This is a template quirk that the wheel swap works around.
- A chassis swap into a My Team slot builds, but the game doesn't load it the way other teams' swaps are built. Swapper's
  My Team chassis swap (`MyTeamChassis`, since 2026-10-18) is built differently and works in game (Career too):
  1. the car goes into the **`fom_car` slot** (the game spawns the My Team car from it), with its driver animations
     published as the FOM car's (`driver_in_car_fom_26`, shared by every FOM car);
  2. every My Team slot's handling draws it at its own height (`myteam_<engine>.vtf`, and the FOM car's own `fom_car.vtf`,
     which is in `teams/common.erp`); the swapped layer's equal-cars file `fom_car_fom_car.vtf` (not in the game) is carried;
  3. the six `myteam_<engine>` car layers point at the car (their handling and engine sound stay theirs);
  4. the car's binding gets **every binding name `fom_car.emb` has** (4,955): its own numbers 1-99 as they are, each number
     font (`number_set_..._N`) its number N, the rest (liveries, stickers) its default. Without them the game stalls
     entering Career: it looks up the player's livery on the car's binding.
  - The car shows its own livery (a My Team livery is painted for the FOM car's shape). Engine sound stays the supplier's.
  - The garage monitors in the My Team / World car garage are placed for the FOM car, so with another car they can sit a
    little off (they're not in any car file).

## The base files (checked 2026-10-18, as Team Simplified's RK pointed out)

Not everything the FOM car uses is in its own car file (`f1_SEASON_vehicle_package/teams/fom_car/wep/fom_car.erp`). The
season's shared base files under `teams/` hold the rest, and the game always has them loaded:

| Season | Own car file | Base files |
|---|---|---|
| 2026 | the whole car: model, the 4,956 `fom_car.emb` bindings (liveries, numbers, number fonts, stickers), materials, textures, slot layers (20,698 resources) | `common.erp` (204): the FOM car's handling `fom_car/handling/fom_car.vtf`, shared generic textures (`dummy_normal_n`...), generic materials (indicator lights), the shadow material, the shared tyres |
| 2025 | only the slot layers and `.wep` (9 resources) | `common_fom.erp` (20,873): the 2025 FOM car itself (model, 4,956 bindings, 4,973 materials, 5,114 textures); `common.erp` (171): handling (`fom_car.vtf` is in both), generics, tyres |

How Swapper handles it:
- the car model (preview, wheel and rims swaps) falls back to `common_fom.erp` when the car file hasn't got the model (`CarModel`);
- the preview takes the shared generics, glass, carbon patterns and tyres from `common.erp` (`PreviewScene`);
- the height fix finds the FOM car's handling in `common.erp` when its car file has none (`CarLook.HandlingOf`);
- the My Team chassis swap reads the FOM car's binding names from the car file and `common_fom.erp` (`MyTeamChassis`);
- a private copy of the FOM model keeps naming the base files' shared resources (they're always there), and never copies
  them.

## What Swapper does with FOM options (`EntryKind.FomChassis` / `MyTeamLivery`)

| Pick | FOM option of a team template | My Team livery |
|---|---|---|
| Steering wheel | yes. The wheel engine makes a private copy of `fom_car`, the layers point at it, and the animset uses the donor's clips. The wheel, driver position and gear stick keep the FOM car's own placement. | yes. The `fom_car` and `myteam_<engine>` car-model layers come into the ERP, pointed at the private copy. The FOM animset is shared, so the log warns that other FOM cars get the new hand animations. |
| Rims | yes. Private copy of `fom_car` (`fom` + 4 letters); the rim textures are the option's own `fom_<code>_wheel_*`. | yes (since 1.2.2). As for the wheel: the slot layers come along, and the rim textures are the livery's own `myteam_*_wheel_*`. |
| Chassis | the new chassis is another option beside it (group 01), placed after the FOM option | yes (since 1.2.2): the car in the FOM car's slot, see above |
| Suits | independent of the chassis | yes (MyTeamSuitSwap: the set's suit geometry dressed in the template's My Team driverwear) |

The game's model is never changed: every geometry edit is a private copy carried in the option's own ERP.

## If a FOM option ever has to be built

For a mod without one, or a 2025 car, the steps are the template's own recipe:

1. Take the team slot's `wep` resources; replace its camera, replay-camera, split-screen camera, mirror and effects
   layers with the FOM car's own, `fom_car` renamed to the team.
2. Re-point the team's car-model and showroom layers at `fom_car.idf?context=render` and `fom_<code>.emb`, and drop the
   car-model layer's `baseVtfAsset`.
3. Add a binding `fom_<code>.emb?context=default`, its materials and the FOM-UV livery textures (or any of the game's own
   `fom_car.emb` livery contexts, which need no textures).
4. Re-point the team's animset at the FOM clips (`driver_in_car_fom_26`, `AnimsetSwap.Retarget`).
5. Add the 99 `fom_<code>.emb?context=N` bindings: the livery binding with the number slots (`number_front`,
   `number_back`) taken from `fom_car.emb?context=N`.

For a **2025** FOM option the steps are the same with the 2025 files: the FOM car's layers are in the 2025
`teams/fom_car/wep/fom_car.erp`, but its model, its `fom_car.emb` livery and number bindings and its materials are in the
base file `teams/common_fom.erp` (see "The base files"); the animset is re-pointed at `driver_in_car_fom_25`; the paths
are `f1_2025_vehicle_package`. (Not built or tested in game: the user has no 2025 templates.)

This knowledge is kept here for reference only: FOM options aren't built by the app (the user's choice, 2026-10-18).

Proved 2026-10-18: a from-scratch build (Williams, the game's APXGP livery `livery_zenith_01`: the 2025 APXGP design, which the 2026 FOM car file carries remapped for its shape; there is no 2026 APXGP car) matches the Williams
template's FOM option in all seven layers, the `.wep`, its event graph and the animset, and its binding has the same 24
slots in the same order. The material binding format (count, linked count, entry-list length; 8-byte entries; label
pool; URI pool) is in `EgoReverseEngineeringEfforts.html`, "Context files"; a writer built on it rewrote 400 of the
game's FOM bindings byte for byte.

The only real work is the livery: it has to be painted (or re-baked) for the FOM UVs. Everything else is renaming.
