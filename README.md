# Kylidar Add-in

An ArcGIS Pro add-in built with the ArcGIS Pro SDK for .NET.

## Documentation

Published at **https://ianhorn.github.io/kylidar-addin/**, built with
[MkDocs](https://www.mkdocs.org/) and the [Material theme](https://squidfunk.github.io/mkdocs-material/).

Markdown source lives in [`docs-src/`](docs-src/index.md). A GitHub Actions workflow
([`.github/workflows/docs.yml`](.github/workflows/docs.yml)) builds the MkDocs site and deploys it
to GitHub Pages automatically on every push to `main` that touches `docs-src/`, `mkdocs.yml`, or
`docs-requirements.txt` -- the built `docs/` output is never committed, it's ephemeral CI artifact
only. Repo Settings → Pages → Source must be set to **GitHub Actions**.

To preview locally:

```bash
pip install -r docs-requirements.txt
mkdocs serve
```

Then open <http://127.0.0.1:8000>.

To publish an update, just commit and push your `docs-src/` changes -- the workflow rebuilds and
redeploys automatically:

```bash
git add docs-src
git commit -m "docs: update"
git push
```

## Requirements

- ArcGIS Pro 3.6+ (developed against 3.6.5)
- Visual Studio 2026, with the ArcGIS Pro SDK for .NET extension installed

## Upgrading to a new ArcGIS Pro version

Project references use `HintPath`s pointing at `C:\Program Files\ArcGIS\Pro\bin\...`
rather than versioned NuGet packages. Since ArcGIS Pro upgrades in place (same
install directory), moving to a new Pro release (e.g. 3.7.x) normally just
means installing it and rebuilding -- no project changes required unless the
add-in starts using APIs introduced in the newer release.

## Building

Open `KylidarAddin.csproj` in Visual Studio and build (F5 launches ArcGIS Pro
with the add-in loaded).

Note: this must be built with Visual Studio's MSBuild, not the `dotnet` CLI.
The ArcGIS Pro SDK's packaging step uses `CodeTaskFactory`, which the .NET
(Core) MSBuild used by `dotnet build` doesn't support. From the command line,
build with:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" KylidarAddin.csproj -t:Build -p:Configuration=Debug
```
