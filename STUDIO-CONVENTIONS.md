# Studio conventions

How the RM Designs Studio folders fit together, and what every app follows.

## Layout

```
shared/                     the Studio's shared code (its own build files: Directory.Build.props, Directory.Packages.props)
  src/Studio.Core/          Studio Core: ERP / SERPs / .simplified reading and writing, the game install, car models,
                            the 3D preview scene + WebGL2 viewer, the texture pipeline
  src/Studio.Ui/            Studio UI: the borderless window and its window buttons
  external/EgoEngineLibrary Petar Tasev's EGO engine library (MIT)
Wheel Swap Engine/          the steering wheel engine (WheelSwap.Core), with its own build files
<App>/                      one folder per app: <App>.slnx, Directory.Build.props, Directory.Packages.props,
                            global.json, README.md, src/, tests/
```

- An app finds the shared code through `$(StudioShared)` (`..\shared\`), set in its own `Directory.Build.props`.
  Its project files reference `$(StudioShared)src\Studio.Core\Studio.Core.csproj` and/or `...\Studio.Ui\Studio.Ui.csproj`.
- Each app's solution lists the shared projects it uses under a `/shared/` folder, so the whole app opens and builds
  from its one `.slnx`.
- Packages are pinned centrally per folder in `Directory.Packages.props`; .NET 10, Windows (`net10.0-windows`).

## Every app

- **Window**: borderless, from Studio UI. In the window's constructor, after `InitializeComponent()`:
  `Studio.Ui.StudioChrome.Apply(this, 64)` (the band's height), and in the band
  `<ui:CaptionButtons DockPanel.Dock="Right"/>`. Moving, resizing, snapping, rounded corners and maximise keep working.
- **Look**: the RM Designs palette (#111111, #1C0040, #4300AD, #F2F2F2), Presicav headings and Roboto Mono text,
  embedded in the exe.
- **Never change the user's files**: every result is a new file; nothing in the game folder is written.
- **Verify before writing**: a build is read back and checked before it replaces anything.
- **Tests skip, never fail, without the game**: the tests find the game and templates through environment variables
  and skip when they aren't there.
- **Credits**: an app that uses Simplified Suite code keeps Team Simplified's notice and shows its credit line in its
  About and README. Every app credits Petar Tasev for EgoEngineLibrary.

## A change in `shared`

Changes every app that uses it. Build and test each of those apps afterwards; an app picks the change up the next time
it is published.
