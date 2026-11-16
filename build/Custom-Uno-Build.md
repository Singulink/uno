# Using the custom Uno 7 build

Our apps run on a custom build of Uno Platform 7. It contains fixes they need that Uno hasn't released yet. We pack it with Uno's own packaging under our own version, `7.1.0-singulink.N`, into a local NuGet feed, and the apps reference that version.

- **Repository:** <https://github.com/Singulink/uno>, a public fork of [unoplatform/uno](https://github.com/unoplatform/uno)
- **Branch:** `custom/7`
- **Based on:** Uno's `master` at `46ea872727`, plus our pull requests to Uno that it hasn't merged yet.
- **Pack script:** [`build/Pack-LocalFeed.ps1`](Pack-LocalFeed.ps1)

## How the branch is made

`custom/7` is Uno's `master` plus the commits of our open pull requests to Uno, plus the few changes of our own that Uno doesn't take.

- **A fix goes to Uno first,** as a pull request against `master` in `unoplatform/uno`. Its commits are then **cherry-picked with `-x`** onto `custom/7`: `git cherry-pick -x <first>^..<last>`. The `-x` line records the original commit, so it can be matched up later.
- **Don't merge pull request branches** into `custom/7`. They often sit on a newer `master` than ours, and merging one drags that part of `master` in with it. Older pull requests on the branch were merged that way; new ones aren't.
- **A pull request's later commits** are cherry-picked the same way.
- **The base only moves on purpose,** when we catch up to a new Uno `master` (see below).
- **Our own changes,** which Uno won't take, go straight onto `custom/7` through a pull request against `custom/7` in `Singulink/uno`.

To see what `custom/7` adds to Uno, with Uno's repository as `upstream`:

```powershell
git fetch upstream master
git log --no-merges --oneline upstream/master..custom/7
```

## One-time setup

- Windows, with Git and Visual Studio 2022 or later (its MSBuild builds the Windows assets).
- .NET SDK 11.0.100 or later. The packaging needs .NET 11, as Uno's CI does, and builds the .NET 11 and .NET 10 assets.
- The workloads: `dotnet workload install android ios maccatalyst tvos wasm-tools`.
- The Android SDK platform for API 37 (`platforms\android-37.0`), from Visual Studio's Android SDK manager.
- JDK 21, with `JAVA_HOME` pointing at it (`winget install Microsoft.OpenJDK.21` installs one and sets it).
- No Mac. The macOS native library can't be built on Windows, so the script takes it from the official `Uno.WinUI.Runtime.MacOS` package in the NuGet cache (`-MacNativeDir`).
- Clone the fork on `custom/7`:

  ```powershell
  git clone --branch custom/7 https://github.com/Singulink/uno.git C:\src\uno
  ```

## Packing

```powershell
C:\src\uno\build\Pack-LocalFeed.ps1 -Version 7.1.0-singulink.N -Feed C:\Projects\Singulink\+LocalPackages
```

- **Always bump `N`.** NuGet caches packages by version, so a repack under a version that was restored before is silently ignored. The script refuses a version the feed already has.
- **Only committed code is packed.** The script refuses to run with uncommitted changes to tracked files, because it rewrites some during the pack and puts them back after.
- It builds Uno's package filters (`build\filters\Uno.UI-packages-all.slnf` and `-windows.slnf`) in Release, packs the `build\nuget` nuspecs and `Uno.Sdk`, and copies about 34 packages to the feed. Logs are in `artifacts\local-feed\logs`.
- `Uno.Sdk`'s `packages.json` names `-Version` for the Uno packages, and keeps `master`'s versions of the others (SkiaSharp, Windows App SDK and so on).

## How apps use it

Each app's `global.json` pins our version:

```json
"msbuild-sdks": {
  "Uno.Sdk": "7.1.0-singulink.N"
}
```

The feed has to be a NuGet source, in the user `NuGet.Config` (`%AppData%\NuGet\NuGet.Config`) or the app's `nuget.config`. Every machine that builds the apps, including a build server, needs the packages in a feed it can reach.

The apps then restore our packages from the feed into the normal NuGet cache, like any other package. Nothing on the machine is overwritten, and clearing the cache is harmless.

## Working on the branch

- **Trying a change:** commit it on a branch made from `custom/7`, pack that branch under a new `N`, and point an app at that version.
- **Faster development builds:** to build and test inside the repository, Uno's [building documentation](https://platform.uno/docs/articles/uno-development/building-uno-ui.html#building-unoui-for-a-single-target-platform) describes `src\crosstargeting_override.props`. Remove it, or make sure it isn't limiting the target frameworks, before packing.

## Catching up to a new Uno master

1. **Pick the commit.** Usually a `master` commit that Uno has published as a `7.1.0-dev.*` build. Its `uno.winui.nuspec` names it in the `commit` attribute of `<repository>`: `https://api.nuget.org/v3-flatcontainer/uno.winui/<version>/uno.winui.nuspec`.
2. **Merge it into `custom/7`.** Merge rather than rebase: a rebase gives every commit a new ID, which breaks links to them and everyone's clone of the branch.

   ```powershell
   git fetch upstream master
   git merge <commit>
   ```

   Fixes Uno has merged since usually merge as the same change. Resolve any conflicts, then check what upstream changed in the areas our fixes touch, for example with `git log <previous base>..<commit> -- <folder>`. A clean merge can still change behavior around a fix.
3. **Update the "Based on" line** above, and the default of `-MacNativeDir` if that package version is gone from the cache.
4. **Pack under a new `N`,** then move the apps' `global.json` to it, rebuild and retest them.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `Packing needs the .NET 11 SDK, as on CI.` | A `global.json` pins an older SDK. Install .NET 11 and check the repository's `global.json`, including a locally edited one. |
| `<feed> already has <version> packages` | Bump `N`. |
| `Commit or stash first` | Commit or stash your changes to tracked files. |
| `XA5207: Could not find android.jar for API level 37.0` | Install the Android SDK platform for API 37. |
| Restore fails with `NU1902`/`NU1903` | NuGet's vulnerability audit; Uno treats its warnings as errors. Pass `-p:NuGetAudit=false` for a dev build. |
| An app still behaves as if a fix is missing | Check the Uno version the app restored (`obj\project.assets.json`), and that the pack ran from a commit that has the fix. On Android, uninstall the app from the device, then Rebuild. |
| An app throws `MissingMethodException` or `TypeLoadException` from Uno code | It mixes Uno packages of different versions, for example from a package reference that pins its own. Every Uno package should resolve to the same `7.1.0-singulink.N`. |
