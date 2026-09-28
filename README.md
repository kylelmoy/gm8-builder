# gm8-builder

Build Game Maker 8.0 games from source non-interactively.

gm8-builder packs a Game Maker 8.0 source split with
[GmkSplitter](https://github.com/Medo42/Gmk-Splitter) into a
playable Windows executable without needing to run Game Maker.
Some data from Game Maker is still required, but the IDE is not
needed- see Requirements.

## Features

- **No Game Maker process.** It needs only four data files from a Game Maker
  8.0 installation, which can be copied anywhere.
- **Faithful output.** It reproduces Game Maker's *Create Executable*, down to
  its image, collision-mask and resource quirks, so builds match what Game Maker
  produces asset for asset.
- **Fast.** A mid-sized game builds in well under a second.
- **GML linter.** Catches code that would fail to compile in Game Maker 8 -
  unknown or modern-only functions, wrong argument counts, syntax GM8 lacks -
  before the game hits it at runtime.
- **Built-in [gm8x_fix](https://github.com/skyfloogle/gm8x_fix) patches.**
  Optionally applies its runner fixes for input lag, joystick polling and more.

## Requirements

- A project in GmkSplitter's split-tree format
- These files from a Game Maker 8.0 installation, or a copy of them:

  ```
  rundata
  dxdata
  lib/*.lib
  extensions/*.ged, extensions/*.dat
  ```

  Linting also needs `fnames`, Game Maker's list of built-in functions and
  variables. These files contain Game Maker's runner and libraries, which are
  not redistributable, so they are not included here.

## Installation

Download a build for Windows, Linux or macOS (Apple silicon) from
[Releases](https://github.com/kylelmoy/gm8-builder/releases). It is a single
file with nothing else to install.

To build from source instead, with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```sh
git clone https://github.com/kylelmoy/gm8-builder.git
cd gm8-builder
dotnet publish src/Gm8Builder.Cli -c Release -o dist
```

This puts the `gm8-builder` command in `dist/`.

## Usage

```sh
# Build a game
gm8-builder build path/to/tree game.exe --gm8 path/to/Game_Maker_8

# The same, with gm8x_fix's patches applied to the runner
gm8-builder build path/to/tree game.exe --gm8 path/to/Game_Maker_8 --gm8x-fix

# Check the tree's GML first, and don't build if it has errors
gm8-builder build path/to/tree game.exe --gm8 path/to/Game_Maker_8 --lint

# Lint scripts and event files on their own
gm8-builder lint path/to/tree --gm8 path/to/Game_Maker_8
```

Set `GM8_DIR` to skip `--gm8`. Instead of an installation you can pass
`--template <game.exe>`, an earlier build of the same game, to supply the
runner, DLL and extensions.

`--gm8x-fix` applies the same patches as `gm8x_fix -s` at the vendored
commit, and the runner comes out byte-identical to gm8x_fix's. To leave some
out, as gm8x_fix's `-n` options do, list their kinds after `--gm8x-fix-skip`:
`memory`, `joystick`, `scheduler`, `input-lag`, `directplay` or `keyboard`.
For example, `--gm8x-fix-skip keyboard` reproduces gm8x_fix v0.5.9, the latest
release, which predates the keyboard_check_direct patch.

Other commands:

| Command | Description |
|---|---|
| `gm8-builder lint <file-or-dir>... [options]` | Check GML against Game Maker 8; `lint --help` lists options, including `--stdin`, `--json` and a line-based JSON `--serve` mode for editors and tools |
| `gm8-builder info <game.exe>` | Summarise a GM8.0 executable's contents |
| `gm8-builder compare <a.exe> <b.exe> [--limit N]` | List content differences between two executables, ignoring compression and random filler |
| `gm8-builder roundtrip <game.exe>` | Read and rewrite an executable, and check the result is identical |

## How it works

Game Maker 8 doesn't compile GML into the executable. It appends the project,
code included as source, to a fixed runner that compiles the code at start-up.
Building a game means writing that data format and reproducing the IDE's
transformations: pixel conversion, collision masks, runner resources and a few
defaults.

## Limitations

Unsupported features are refused with an error, never built incorrectly:

- Fonts: Game Maker renders them with Windows GDI.
- The *transparent* and *smooth edges* image options, and disk and diamond
  collision masks.
- Triggers.
- GM8.1.

## Development

```sh
dotnet test
```

Tests that need real Game Maker output run when these environment variables
are set, and pass trivially otherwise:

| Variable | Meaning |
|---|---|
| `GM8_TEST_EXE` | An executable built by Game Maker 8.0 |
| `GM8_TEST_TREE` | The split tree that executable was built from |
| `GM8_DIR` | The Game Maker 8.0 installation that built it |

To get a matching tree, split the `.gmk` Game Maker built the executable from.

## Acknowledgements

- [OpenGMK](https://github.com/OpenGMK/OpenGMK), whose GM8 executable reader
  was the main reference for the file format. No code is taken from it.
- [GmkSplitter](https://github.com/Medo42/Gmk-Splitter) defines the source-tree
  format read here.
- [gm8x_fix](https://github.com/skyfloogle/gm8x_fix) by Floogle, MIT licensed.
  Its GM8.0 patch tables are vendored in [`third_party/gm8x_fix`](third_party/gm8x_fix).

## License

[MIT](LICENSE). The included gm8x_fix patch tables are MIT licensed by their
author.

Game Maker is a trademark of YoYo Games. This project is not affiliated with
YoYo Games.
