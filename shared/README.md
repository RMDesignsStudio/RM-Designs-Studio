# RM Designs Studio – shared engine

The code every RM Designs Studio app builds on, kept once so the apps behave and look the same.

- `src/Studio.Core` – EGO / ERP reading (`ErpX`, `MeshDesc`, `NodeTable`, `EgoMaterial`), the game install
  (`GameData`) and car models (`CarModel`), SERPs templates (`SerpsTemplate`: read, list car entries, write a new
  .serps), the 3D preview scene (`PreviewScene`) and its WebGL2 viewer (`Viewer`: self-contained page or hosted live),
  the texture pipeline (`TextureIo`), and Simplified Studio projects (`SimplifiedProject`: read a .simplified, write one
  from a template).
  The viewer's car studio (2026-10-18): carbon follows the game's own `vehicle_carbon` shader (the weave's G = colour,
  R = fibre sheen, A = fibre direction; the fibres catch lights only, the room reflects only in the lacquer), and a soft
  low fill light comes from the camera's side (car mode only, not on figures, not in the clear coat) so near-black
  metal such as wheel covers stays readable.
- `src/Studio.Ui` – the Studio window look shared by every app: `StudioChrome.Apply(window, bandHeight)` makes a
  window borderless (the brand band is the title bar; moving, resizing, snapping incl. Windows 11 snap layouts, rounded
  corners and a correct maximise all keep working) and `<ui:CaptionButtons/>` at the band's right end draws the
  minimise / maximise / close buttons. A new app gets the same frame with those two lines.
- `external/EgoEngineLibrary` – vendored copy of Petar Tasev's MIT-licensed library (see its LICENSE), with its
  ImageSharp / glTF parts moved out to `external/_removed_from_egoengine` (not built).

Used by:

- `../Steering Wheel Swapper` (from v1.8.0)
- `../Livery Previewer`
- `../Swapper` (also uses `../Steering Wheel Swapper/src/WheelSwap.Core`)

- `../Helmet Swapper` (Studio.Ui only: it has its own engine code)

(`../Car UV Freer`, a command-line tool, sits in the same Studio folder but uses none of this.)

Each app finds this folder through `$(StudioShared)` in its own `Directory.Build.props` (`<app>\..\shared\`), so
the apps and `shared` must stay side by side in the `RM Designs Studio` folder.
The props files here hold the build settings and package versions for the shared projects.

A change here changes every app: build and test each of them afterwards. Each app's share script
(`tools/make_share.py`) puts a copy of this folder in its source zip.
