# gm8x_fix

Patch tables from [gm8x_fix](https://github.com/skyfloogle/gm8x_fix) by Floogle,
which fixes input lag, joystick polling, timer resolution and other issues in
GameMaker 7-8.1 runners.

- **Upstream:** https://github.com/skyfloogle/gm8x_fix
- **Commit:** `2f71b2417d6db705eaaf5e2003c839ac1f64fb9c`
- **Licence:** MIT, see [LICENSE](LICENSE)

Only the tables are taken, and only the patches that apply to a GM8.0 runner.
The code that applies them is gm8-builder's own (`src/Gm8Builder/Pe/Gm8xFix.cs`).

## Updating

`Gm8xFixPatches.cs` is generated. Don't edit it by hand. To update it from a
newer gm8x_fix:

```sh
git clone https://github.com/skyfloogle/gm8x_fix
python tools/update-gm8x-fix.py path/to/gm8x_fix
```

The script rewrites `Gm8xFixPatches.cs` and `LICENSE` here. Update the commit
above to match.
