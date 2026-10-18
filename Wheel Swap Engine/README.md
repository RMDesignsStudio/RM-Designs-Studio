# Wheel Swap Engine

The steering wheel engine behind the Swapper (`WheelSwap.Core`):

- **WheelBuild**: builds a donor car's steering wheel into a template's car entries (`tpl.Build(...)`): grafts the
  wheel model into a private copy of the car's model, re-points the car layers, carries the in-car driver animations
  (and, for an older season's wheel, its animation clips), swaps the wheel textures and fixes sibling bindings.
- **WheelGrafter**, **TextureSwap**, **AnimsetSwap**: the graft, the textures, the animation set.
- **BuildVerifier**: checks every built entry before anything is written; a failed check blocks the write.
- **BuildReport**, **WheelPreview**, **WheelThumbnail**: the HTML build report, the 3D preview data and the wheel
  pictures.

It builds on Studio Core (`../shared`), found through `$(StudioShared)` in this folder's `Directory.Build.props`.
