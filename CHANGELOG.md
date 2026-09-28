# Changelog

## Unreleased

- `build --extensions <dir>`: take the extension packages from a directory of
  `.gex` files instead of from the installation or template.

## 0.1.0

First release.

- `build`: packs a GmkSplitter tree into a Game Maker 8.0 executable, matching
  Game Maker's own *Create Executable* output asset for asset.
- `--gm8x-fix`: applies gm8x_fix's runner patches, byte-identical to
  `gm8x_fix -s`; `--gm8x-fix-skip` leaves out chosen kinds of patch.
- `lint`: checks GML against Game Maker 8, with `--json`, `--stdin` and a
  line-based JSON `--serve` mode; `build --lint` stops on errors.
- `info`, `compare` and `roundtrip` for inspecting executables.
- `--version`.
- Single-file binaries for Windows, Linux and macOS (Apple silicon).
