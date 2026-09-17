# Installation

## Requirements

- **ArcGIS Pro 3.6** or later (the add-in manifest targets `desktopVersion="3.6.0"`).
- **ArcGIS Pro SDK for .NET** installed (adds the Visual Studio project templates and the
  `Esri.ProApp.SDK.Desktop.targets` build integration this project relies on).
- **Visual Studio**, with the ArcGIS Pro SDK for .NET extension installed.

No separate [PDAL](https://pdal.io/) install is needed -- the convert (and optional clip) step runs
PDAL through ArcGIS Pro's own bundled conda Python environment (`arcgispro-py3`), which already
ships it.

## Download pre-built add-in

Prefer not to build from source? Grab the compiled `.esriAddinX` matching your installed ArcGIS Pro
version from the repo's [`AddInX-Files/`](https://github.com/ianhorn/kylidar-addin/tree/main/AddInX-Files)
folder:

- **ArcGIS Pro 3.6.x** -- [KylidarAddin-3.6.x.esriAddinX](https://raw.githubusercontent.com/ianhorn/kylidar-addin/main/AddInX-Files/KylidarAddin-3.6.x.esriAddinX)
- **ArcGIS Pro 3.7 or later** -- [KylidarAddin-3.7.x.esriAddinX](https://raw.githubusercontent.com/ianhorn/kylidar-addin/main/AddInX-Files/KylidarAddin-3.7.x.esriAddinX)

Both are built from the same source, at the same commit -- the only difference is which .NET
version they target, matching whichever .NET runtime that Pro release hosts (Pro 3.6.x hosts .NET
8, Pro 3.7+ hosts .NET 10; a build targeting the wrong one won't load). Download the one matching
your Pro version, then double-click it (or copy it to
`%LocalAppData%\ESRI\ArcGISPro\AssemblyCache`) to install it through ArcGIS Pro's normal Add-In
Manager flow.

## Build from source

1. Clone the repository:

    ```bash
    git clone https://github.com/ianhorn/kylidar-addin.git
    cd kylidar-addin
    ```

2. Open `kylidar-addin.slnx` in Visual Studio.
3. Build the `KylidarAddin` project (Debug or Release). The ArcGIS Pro SDK's build targets
   package the compiled assembly, `Config.daml`, and the toolbar images into an `.esriAddinX`
   file and register it with ArcGIS Pro automatically.
4. Press **F5** (or **Start**) to launch ArcGIS Pro with the add-in already loaded, or just open
   ArcGIS Pro normally -- once built, the add-in stays registered.

!!! warning "Build with Visual Studio's MSBuild, not `dotnet build`"
    The ArcGIS Pro SDK's packaging step uses `CodeTaskFactory`, which the .NET (Core) MSBuild
    used by `dotnet build` doesn't support. From the command line, build with:

    ```
    "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" KylidarAddin.csproj -t:Build -p:Configuration=Debug
    ```

!!! tip "No Visual Studio?"
    You only need the compiled `.esriAddinX` file to *use* the add-in. Copy it to
    `%LocalAppData%\ESRI\ArcGISPro\AssemblyCache` (or double-click it) and ArcGIS Pro will install
    it through its normal Add-In Manager flow.

## Upgrading to a new ArcGIS Pro version

Project references use `HintPath`s pointing at `C:\Program Files\ArcGIS\Pro\bin\...` rather than
versioned NuGet packages. Since ArcGIS Pro upgrades in place (same install directory), moving to a
new Pro release normally just means installing it and rebuilding -- no project changes required
unless the add-in starts using APIs introduced in the newer release.

The project file itself always targets `net8.0-windows` (matching whatever Pro version is
currently installed for day-to-day development), but the two `AddInX-Files/` downloads above are
both built from that same, single `.csproj` -- the 3.7.x one just overrides the target framework on
the command line, without needing Pro 3.7 actually installed (the `HintPath` references only need
*some* Pro install to compile against, and CopyLocal is off, so nothing about the referenced
assembly's version ships in the built add-in):

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" KylidarAddin.csproj -t:Restore -p:Configuration=Release -p:TargetFramework=net10.0-windows
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" KylidarAddin.csproj -p:Configuration=Release -p:TargetFramework=net10.0-windows
```

## Verifying the install

Open ArcGIS Pro and look for the **Kylidar** ribbon tab with a **Kylidar** button. Clicking it
should open the dockable Kylidar pane (see [Getting Started](getting-started.md)). If the tab is
missing, confirm the build succeeded with no errors and that ArcGIS Pro was restarted after the
first build.
