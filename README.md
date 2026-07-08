# Datfile Creator Studio

A datfile generation tool built for collectors who manage large, structured archives and need consistent, reproducible **Logiqx XML** DAT files across hundreds or thousands of folders and millions of files. Point it at a folder, choose how you want the DATs shaped, and click **Start** — no scripting, no manual XML editing.

Datfile Creator Studio is a native **C# / [Avalonia](https://avaloniaui.net/)** desktop application. It is a ground-up rewrite of *Eggman's Datfile Creator Suite* (the Python/Tkinter original), with a modern Fluent interface, an auto-sliding activity-log drawer, true multithreaded hashing, and a single self-contained executable that needs no runtime installed.

Its output is **byte-for-byte identical** to the original suite — the generation engine, all four structure options, the incremental-update carry-forward logic, and every dat-writing tool are verified against the Python version by an automated parity harness (see [Parity](#parity)).

<!-- Add a main window screenshot here, e.g.:
<img width="1600" alt="Datfile Creator Studio" src="https://github.com/user-attachments/assets/REPLACE_ME" />
-->

---

If this tool saves you time, consider supporting the work:

<a href="https://buymeacoffee.com/eggmansworld">
  <img src="https://cdn.buymeacoffee.com/buttons/v2/default-orange.png" height="45" alt="Buy Me a Coffee">
</a>

---

## Highlights

- **Two dat types** — *Mixed (Archive as File)* and *Zipped* (archive contents hashed).
- **Three generation modes** — one dat for the whole input, one dat per root folder, or one dat per folder at every depth (TOSEC-style).
- **Four structure options** matching RomVault's dir2datUI output styles, with a live **Preview** window that re-renders any completed dat across all four structures instantly — no re-hashing.
- **Hashes** — CRC32 + SHA1 always; MD5, SHA-256, and forward-looking BLAKE3 optional.
- **ZStandard (RVZSTD, method 93) zips** read natively — no external tools needed for hashing.
- **Incremental update** — rehash only new or changed files and carry forward everything unchanged, with a pre-flight validation report before the run.
- **Network cap** — token-bucket throttle with automatic NIC-speed detection, so scanning a network share won't saturate your link.
- **Auto-sliding log drawer** with colour-coded, live activity, progress, and elapsed time.
- **Fully portable** — every setting lives in a single `DatfileCreatorStudio.config` file next to the executable. No registry, no `%APPDATA%`, no temp files.
- **Follows your OS light/dark theme**, with a manual override.

### Included tools

| Tool | What it does |
|---|---|
| **Folder Structure Analyzer** | Inspects a collection and recommends the right Generation mode and Structure before you run, plus a path-length report |
| **Long Path Length Repair** | Finds and interactively renames files/folders whose full path approaches the Windows 260-character `MAX_PATH` limit |
| **Bulk Datfile Header Updater** | Find-and-replace header fields (author, version, URL, date, …) across an entire folder of datfiles at once |
| **Game and ROM Counter** | Tallies games, ROMs, and total size across any set of datfiles, with tree/flat views, selection subtotals, and CSV export |
| **Validate Datfiles** | Checks every `<rom>` entry for present, correctly formatted required attributes |
| **Merge Datfiles** | Merges per-subfolder datfiles upward into one dat at a category root |
| **Recursive Archive Extractor** | Recursively extracts ZIP / 7Z / RAR archives, with nested-archive auto-extraction (uses 7-Zip-ZStandard) |
| **ZIP Store Packer** | Wraps files into uncompressed `ZIP_STORED` containers, verified before the original is removed |
| **Remove ReadOnly Attribute** | Clears read-only flags and removes the Zone.Identifier "downloaded from another computer" stream in bulk |

<!-- Add a Tools menu / preview window screenshot here if you like. -->

---

## Install (Windows)

1. Download the latest `DatfileCreatorStudio-win-x64.zip` from the [Releases](../../releases) page.
2. Extract it anywhere.
3. Run `DatfileCreatorStudio.exe`.

The build is fully self-contained — you do **not** need the .NET runtime installed. Only the *Recursive Archive Extractor* tool needs an external program: [7-Zip-ZStandard](https://github.com/mcmilk/7-Zip-zstd/releases) (`7z.exe`), and only if you use that tool.

Linux users can download `DatfileCreatorStudio-linux-x64.tar.gz` from the same page (`chmod +x DatfileCreatorStudio` and run it).

---

## Quick start

1. Set the **Input top-level folder** — the folder that contains the game folders you want to dat.
2. Set the **Output folder** — where the datfiles are written (the structure mirrors the input).
3. Fill in the DAT **header** fields to taste (all optional).
4. Choose **Dat Type** (Mixed or Zipped), **Generation** (usually *1 dat per root folder*), **Structure** (*Archives as Games* suits most collections), and **Format** (Modern).
5. Click **Start**. The log drawer slides up with live progress; **Preview Dats** lights up when the run completes.

Not sure which structure to use? Open **Tools → Analyze Folder Structure** first.

---

## Build from source

Requires the **.NET 10 SDK**.

```
git clone https://github.com/Eggmansworld/DatfileCreatorStudio.git
cd DatfileCreatorStudio
dotnet run --project src/DatfileCreatorStudio
```

To produce a self-contained single-file build:

```
pwsh ./publish-win.ps1      # -> dist/win-x64/DatfileCreatorStudio.exe
./publish-linux.sh          # -> dist/linux-x64/DatfileCreatorStudio
```

The solution is split into `DatfileCreator.Core` (the engine and tool logic, no UI dependencies) and `DatfileCreatorStudio` (the Avalonia app).

---

## Parity

The engine is a faithful port, and that claim is enforced. `tools/parity/compare.ps1` runs the original Python suite and this engine over the same test collection — including deterministic RVZSTD archives with zero-byte files, empty folders, and nested empty folders — and compares every output **byte for byte**. The matrix covers both dat types across all three generation modes and all four structures, the legacy/`<machine>` variants, every hash (BLAKE3 included), extension filters, incremental update, the folder analyzer's recommendations, and the dat-writing tools (Merge, Header Updater, Counter, Validate). All checks pass byte-identical.

---

## License

Original source code, scripts, and hand-authored documentation are licensed under the **MIT License**. Archived game data, binaries, firmware, media assets, and other third-party materials are **not** covered by the MIT License and remain the property of their respective copyright holders. See the [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE) files for full details.

---

*Made with ❤️ for the retro game preservation community. Created by Eggman, with Claude's help turning ideas into code.*
