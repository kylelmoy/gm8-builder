# gm8-builder

Build Game Maker 8.0 games from source without Game Maker. Cross-platform,
headless, and fast.

gm8-builder turns a [GmkSplitter](https://github.com/Medo42/Gmk-Splitter)
source tree straight into a playable Windows executable. It doesn't run Game
Maker, so builds work on any platform .NET runs on, suit CI and scripting, and
typically finish in under a second.

## Features

- **No Game Maker process.** It needs only four data files from a Game Maker
  8.0 installation, which can be copied anywhere.
- **Faithful output.** It reproduces Game Maker's *Create Executable*, down to
  its image, collision-mask and resource quirks, so builds match what Game Maker
  produces asset for asset.
- **Fast.** A mid-sized game builds in well under a second.
- **Built-in [gm8x_fix](https://github.com/skyfloogle/gm8x_fix) patches.**
  Optionally applies its runner fixes for input lag, joystick polling and more.
- **Inspection tools.** Summarise, round-trip and diff existing GM8.0
  executables.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A project in GmkSplitter's split-tree format
- These files from a Game Maker 8.0 installation, or a copy of them:

  ```
  rundata
  dxdata
  lib/*.lib
  extensions/*.ged, extensions/*.dat
  ```

  They contain Game Maker's runner and libraries, which are not
  redistributable, so they are not included here.

## Installation

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
```

Set `GM8_DIR` to skip `--gm8`. Instead of an installation you can pass
`--template <game.exe>`, an earlier build of the same game, to supply the
runner, DLL and extensions.

Other commands:

| Command | Description |
|---|---|
| `gm8-builder info <game.exe>` | Summarise a GM8.0 executable's contents |
| `gm8-builder compare <a.exe> <b.exe> [--limit N]` | List content differences between two executables, ignoring compression and random filler |
| `gm8-builder roundtrip <game.exe>` | Read and rewrite an executable, and check the result is identical |

## How it works

Game Maker 8 doesn't compile GML into the executable. It appends the project,
code included as source, to a fixed runner that compiles the code at start-up.
So building a game means writing that data format and reproducing the IDE's
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
