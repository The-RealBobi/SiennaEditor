# Sienna Editor v0.1.0

Desktop inspector and editor for Level-5 T2B and RDBNP BIN records, built with .NET 10 and Avalonia for Windows and macOS, including Apple Silicon.

## Use

1. Open a BIN or readable CFG. Lua bytecode is not a supported record file.
2. Select a record type or search names and values. Records retain their native order and are shown in pages of 200.
3. Edit the selected row's fields. Arrays, vectors and nested structures expose their individual components; ranges use `start, count`. Numbers accept decimal or `0x` hexadecimal. Booleans use `true`/`false`. The separate `null` checkbox distinguishes null strings from empty strings. `bits:0x…` preserves exact floating-point bit patterns when needed.
4. Apply the row, or select another record to apply valid edits automatically. Revert restores unapplied edits. Invalid edits leave the row unchanged.
5. Save a reconstructed BIN or export readable CFG. Files are written through a temporary sibling and atomic replacement. The save dialog asks before replacing an existing file; the suggested BIN filename is a separate edited copy.

Duplicate and Delete operate through the API on a separate document before replacing the in-memory version. Confirmed COUNT/range/index rules are maintained by CFGBINManager. Unsupported structural edits fail explicitly. A duplicated identifier may need to be changed according to the game's own rules.

## Structure catalogs

The application embeds the catalog supplied by its exact CFGBINManager package. **Load Structure** accepts an external native GAME/T2B/RDBNP catalog or the older declaration format. A multi-game catalog prompts for a profile; switching a profile relabels the current document while preserving its edited values. New catalogs require no editor compilation.

T2B labels are used only when the declared field count matches the actual row. Unknown and incompatible layouts remain editable by native position. RDBNP retains its native descriptors and shows friendly aliases alongside native names. **Schema** opens native descriptor, layout and format information.

## Build

The editor consumes **CFGBINManager 0.2.6** as a pinned NuGet package. It does not contain that library's source or a submodule. Clone/build CFGBINManager beside this checkout first:

```sh
dotnet pack ../CFGBINManager/src/CFGBINManager/CFGBINManager.csproj -c Release -o ../CFGBINManager/dist
dotnet build src/SiennaEditor/SiennaEditor.csproj -c Release
dotnet run --project src/SiennaEditor/SiennaEditor.csproj -c Release --no-build
```

Pass a local filename after `--` to open it at startup. Restore uses the sibling package feed and NuGet.org; the packaged application needs neither the sibling checkout nor a separate Structure file.

Distributions are generated under `dist/`:

```sh
bash build.sh osx-arm64
bash build.sh win-x64
```

On Windows, use `./build.ps1 win-x64`. The macOS archive contains **Sienna Editor.app**; Windows contains **Sienna Editor.exe**. Builds are self-contained and include the .NET runtime. They are unsigned; signing/notarization is a separate distribution step. See [repository workflow](REPOSITORIES.md) for the public/private arrangement.

## Boundaries

CFGBINManager owns binary parsing, CFG interchange, serialization and supported structural maintenance. The editor preserves the document's physical kinds and does not infer gameplay meaning or legality from an alias. Native four-byte T2B and RDBNP version 100 profiles apply. Unknown format versions and unsupported native layouts produce an error. Retain the exported CFG metadata for lossless interchange.

Build results, actual macOS operation and Windows runtime execution are separate evidence; a cross-published Windows executable does not establish a Windows UI run.
