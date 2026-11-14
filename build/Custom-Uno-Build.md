# Using the custom Uno 7 build

Our apps run on a custom build of Uno Platform 7. It contains fixes they need that Uno hasn't released yet. This document explains how the custom build reaches the apps, how to set it up on your machine, and how to work on it.

- **Repository:** <https://github.com/Singulink/uno>, a public fork of [unoplatform/uno](https://github.com/unoplatform/uno)
- **Branch:** `custom/7`
- **Based on:** Uno's `master` branch at `46ea872727`, plus our pull requests to Uno that it hasn't merged yet. The apps restore `Uno.Sdk` `7.1.0-dev.18`, that is the official Uno.WinUI `7.1.0-dev.173` (commit `5db002dd36` on `master`), and the custom build replaces that version's files.
- **Publish script:** [`build/Publish-NuGetCacheOverride.ps1`](Publish-NuGetCacheOverride.ps1)

## How the branch is made

`custom/7` is Uno's `master` with our open pull requests to Uno merged in, plus the few changes of our own that Uno doesn't take. A fix goes to Uno first, as a pull request against `master`, and its branch is merged into `custom/7` until Uno merges it. Once Uno releases it, the next catch-up merge brings it in from `master` as the same change.

The branch starts from `master`, not from Uno's `release/stable/7.0`, because our pull requests are based on `master`: merging them into the release branch would bring `master`'s history in with them.

## How apps pick up the custom build

The apps reference the **official, published** Uno packages. Their `global.json` pins `Uno.Sdk` `7.1.0-dev.18`, which brings in `Uno.WinUI` `7.1.0-dev.173` and the related packages from nuget.org. Nothing in an app's repository points at the fork.

The custom build is applied on each developer machine. It **overwrites the files of those package versions in the local NuGet cache** (`%USERPROFILE%\.nuget\packages`, or `%NUGET_PACKAGES%` if set). After that, every app on the machine that restores Uno `7.1.0-dev.173` builds and runs with the custom assemblies. The overwrite uses Uno's own NuGet override build mechanism (`UnoNugetOverrideVersion`), driven by the publish script.

This has consequences:

- **It is per machine.** A machine that never ran the publish script builds the apps with the stock Uno packages, and nothing warns about it. The apps compile and run, just without the fixes. Every machine that builds the apps, including any build server that produces release builds, needs the custom build published to its cache.
- **Clearing the NuGet cache removes it.** That includes `dotnet nuget locals all --clear`, `global-packages --clear`, and deleting `.nuget\packages`. If you do any of these, restore the apps and run the publish script again.
- **iOS builds through Pair to Mac use the Windows cache.** Restore and compilation happen on Windows; the Mac only runs the native steps. You don't need to do anything on the Mac.
- **Only .NET 10 is covered.** Uno 7 ships .NET 10 and .NET 11 builds. The script builds the .NET 10 ones (`-Framework`), which is what the apps target; the packages' .NET 11 folders stay the official ones.
- **Dependency versions.** The custom build can be built against newer dependencies (SkiaSharp, Svg.Skia, HarfBuzzSharp...) than the official packages of that version declare. The script therefore gives each overridden package the dependencies the build would pack, and raises the matching versions Uno.Sdk adds to apps itself, so apps restore them without pinning anything. The official files are kept next to them as `.<id>.nuspec.official` and `.packages.json.official`, and the changes are listed in `<worktree>\artifacts\nuget-override\dependencies.txt`.

## One-time setup

### Prerequisites

- Windows, with Windows PowerShell 5.1 (built in) or PowerShell 7.
- Git.
- .NET SDK 10.0.403 (the script's `-SdkVersion`). The script pins it for its own builds, so other SDKs, such as .NET 11's, can stay the machine default.
- These .NET workloads: `dotnet workload install android ios maccatalyst tvos wasm-tools`. Only Android and iOS get built, but the mobile projects need every platform they target installed to restore.
- For Android, the Android SDK (API 36) and a JDK 17 or later. Visual Studio's .NET MAUI or mobile workload installs both.
- No Mac or Xcode. The iOS parts of Uno are class libraries, and they compile on Windows.
- About 3 GB of disk: about 1 GB for the repository, plus the build's working copy and outputs.

### Steps

1. **Clone the fork** on the custom build's branch, anywhere you like:

   ```powershell
   git clone --branch custom/7 https://github.com/Singulink/uno.git C:\src\uno
   ```

2. **Restore the apps you work on** with every platform they target. For example, open the solution in Visual Studio, or run `dotnet restore` on it. The script only overwrites packages that are already in the cache. Packages that get restored later stay stock until you publish again, such as when you first build for iOS or when an app starts using a new Uno feature. See [When to publish again](#when-to-publish-again).

3. **See what it will do,** if you like. A dry run changes nothing:

   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\src\uno\build\Publish-NuGetCacheOverride.ps1 -DryRun
   ```

4. **Run the publish script:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\src\uno\build\Publish-NuGetCacheOverride.ps1
   ```

   The first run takes a while as it creates its working copy and builds everything once. Visual Studio can stay open; see [Files in use](#files-in-use).

5. **Rebuild the apps.** A plain build picks up the changed package files, but use Rebuild (or delete `bin` and `obj`) the first time and whenever in doubt. That matters most for Android, which caches assemblies aggressively.

The script exits with code 0 on success and 1 if anything failed. On failure it prints a `Not done:` list.

## When to publish again

Run the script again when any of these happens:

- New commits land on `custom/7`. The script builds the branch as it is in your clone, so run `git -C C:\src\uno pull` first.
- You restored a platform or an app for the first time after the last publish, such as a first iOS build.
- The NuGet cache was cleared.
- The script's check reports assemblies that weren't rebuilt (see [Troubleshooting](#troubleshooting)).

To see what your cache currently has without building anything, run it with `-VerifyOnly`, or with `-DryRun`, which also changes nothing while tidying:

```powershell
powershell -ExecutionPolicy Bypass -File C:\src\uno\build\Publish-NuGetCacheOverride.ps1 -VerifyOnly
```

You can also check one file by hand. Look at the product version of `%USERPROFILE%\.nuget\packages\uno.winui\7.1.0-dev.173\lib\net10.0\Uno.UI.dll`, in Properties > Details. The custom build shows `255.255.255.255+<commit>`, and the official one shows `7.1.0-dev.173+5db002dd36...`.

## What the script does

```text
Publish-NuGetCacheOverride.ps1 [-Ref <branch or commit>] [-Version <package version>] [-Framework <tfm>] [-SdkVersion <version>] [-Worktree <path>] [-VerifyOnly] [-DryRun]
```

| Parameter | Default | Meaning |
| --- | --- | --- |
| `-Ref` | `custom/7` | Branch or commit to build, as it is in your clone. |
| `-Version` | `7.1.0-dev.173` | Version of the Uno packages the apps restore. It must match what the apps' `Uno.Sdk` resolves to. Uno 6 versions are refused: the custom 6.8 build has its own script on `custom/6.8`. |
| `-Framework` | `net10.0` | .NET version the apps target. Its package folders are the ones built and checked. |
| `-SdkVersion` | `10.0.403` | .NET SDK the builds run with, pinned by a `global.json` in the log folder. |
| `-Worktree` | `uno-71-publish` next to your clone | Folder the build runs in. |
| `-VerifyOnly` | off | Only tidy and check the cache, without building. |
| `-DryRun` | off | Change nothing: list the builds a run would make, the cache folders they would replace with the commit each one's assemblies come from now, and what tidying would change, then check the cache as it is. |

**1. A working copy of its own.** The script builds in a separate git worktree. By default that's `uno-71-publish` next to your clone, for example `C:\src\uno-71-publish`. The script creates it on the first run, and each run checks it out at the commit `-Ref` points to. This has three effects:

- Only committed code gets published. Unfinished edits in your clone never reach the cache.
- A local `src\crosstargeting_override.props` in your clone has no effect. That file is Uno's way to make development builds faster by building fewer platforms.
- The build outputs stay warm between runs, which is why later runs are fast.

Don't edit anything in that folder. The script refuses to run if it has changes. If it's ever in a bad state, delete it with `git -C C:\src\uno worktree remove --force C:\src\uno-71-publish`; the next run recreates it.

**2. The builds.** All builds use the Release configuration:

| Step | What it builds | Used by |
| --- | --- | --- |
| Skia assemblies, source generators, tasks and dev server | Uno's `build\filters\Uno.UI-packages-generic.slnf`, without the native mobile projects, for `-Framework` | All platforms: `Uno.UI` (one Skia build for every platform), the desktop and WebAssembly runtimes, SVG, media player, the dev server, source generators and build tasks |
| Reference assemblies | `build\filters\Uno.UI-packages-reference.slnf` | What app code compiles against (`uno.winrt` and `uno.foundation` `lib\net10.0`) |
| WebAssembly WinRT assemblies | `build\filters\Uno.UI-packages-wasm.slnf` | WebAssembly apps (`uno-runtime\net10.0\wasm`) |
| Android runtime and WinRT assemblies | `Uno.UI.Runtime.Android` for `net10.0-android` | Android apps |
| iOS WinRT assemblies | `src\Uno.WinRT\Uno.WinRT.netcoremobile.csproj` for `net10.0-ios26.0` | iOS apps |
| AppleUIKit runtime | `Uno.UI.Runtime.AppleUIKit` for `net10.0-ios26.0` | iOS apps |

Uno 7's packages are laid out like this: `Uno.WinUI` has one Skia build of `Uno.UI` in `lib\<tfm>`. The platform runtimes are their own packages, `Uno.WinUI.Runtime.Android` (`lib\net10.0-android36.0`), `Uno.WinUI.Runtime.AppleUIKit` (`lib\net10.0-ios26.0`), `Uno.WinUI.Runtime.WebAssembly.Browser`, `Uno.WinUI.Runtime.Win32` and so on. Only the WinRT layer, `Uno.WinRT` (with `Uno.UI.Dispatching`) and `Uno.Foundation`, still has a build per platform: `lib\net10.0-android30.0` and `lib\net10.0-ios26.0`, plus `uno-runtime\net10.0\generic` and `uno-runtime\net10.0\wasm`, which replace the reference build in `lib\net10.0` when an app builds.

The step builds don't write the cache directly. With `UnoNugetOverrideVersion` set, each project's build copies its output over the matching folder of the restored package (see `src\Uno.CrossTargetting.targets`). An override only writes into folders the package already has (looking up the Android API level of the package's folder), and it never replaces the package's Android archives (`.aar`).

The script also passes `-p:NuGetAudit=false`. Uno treats NuGet vulnerability warnings as errors, so an advisory published after a commit would otherwise stop a build of code that built fine before.

**3. The dependencies.** The overridden packages keep version `7.1.0-dev.173`, so NuGet would still resolve the official build's dependencies from the nuspec extracted in the cache. Each package gets the dependency groups the build would pack instead: from `build\nuget\<id>.nuspec`, with the Uno versions CI stamps (`PrepareNuGetPackage` in `build\Uno.UI.Build.csproj`) set to `-Version`, or from `dotnet pack --no-build` of its project for the frameworks the script builds (the other groups stay official). In each cached `Uno.Sdk` that pins `-Version`, a group of its `packages.json` that holds a package those nuspecs declare is raised to the declared version, never lowered. The `.nupkg`, its hash, metadata and signature stay as downloaded: they describe the package NuGet downloaded, while restore reads the dependencies from the extracted nuspec. An app that restored before needs `dotnet restore --force` (or a Rebuild) to see them.

**4. Tidying the cache.** The script deletes `7.1.0-dev.173` folders that NuGet never restored, which older overrides created. It removes target framework folders that the package's `.nupkg` doesn't have. It also puts back the package's own `.aar` files where an older override replaced them. It never looks at another version's folders.

**5. The check.** For every Uno package at that version in the cache, the script looks at the folders that Skia apps on desktop, WebAssembly, Android and iOS load assemblies from. It sorts each `Uno*.dll` by the commit it was built from, using the commit embedded in its product version:

- **Built from the published commit:** correct. So is an earlier commit with the same `src`, which means commits that only change documentation or this script don't call for another publish.
- **Still the package's own build:** no override reaches it. That is harmless as long as the fork doesn't change that assembly's source. The build tasks are the exception that looks like one: the package's `Uno.UI.Tasks.<commit>.dll` is replaced by the local `Uno.UI.Tasks.v0.dll`, and the package's targets are switched over to it.
- **Built from another commit:** a stale leftover, which makes the run fail.

All the binary logs are in `<worktree>\artifacts\nuget-override\`, one `.binlog` per step. Open them with the [MSBuild Structured Log Viewer](https://msbuildlog.com/).

### Files in use

Visual Studio, its MSBuild worker processes and a running Uno dev server keep some files in the cache loaded, such as the build tasks and the dev server host. A loaded file can't be overwritten, but Windows does allow renaming it. So the script moves each in-use file aside as `<name>.inuse-<timestamp>`, puts a copy in its place for the build to replace, and carries on. At the end it names the processes that still have the old copies loaded:

```text
1 files were in use. Restart these to load the new copies:
  Uno.UI.RemoteControl.Host (process 55044)
```

Restart those processes when convenient, for example by closing and reopening Visual Studio or restarting the debug session. Until then they keep running the previous version of those particular files. A later run deletes the moved-aside copies once nothing holds them.

## Why every assembly must come from the same commit

Don't override individual projects by hand, for example with `dotnet build -p:UnoNugetOverrideVersion=7.1.0-dev.173` on one project. Always publish with the script.

Uno's assemblies call each other's internal and public members. Changes in the fork can change those members, and C# binds some things at compile time, such as optional parameter defaults. An assembly compiled against one commit can therefore crash against another. For example:

- A stock Android runtime running against the fork's `Uno.UI` failed on the first keystroke with `MissingMethodException: ImeCompositionEventArgs..ctor(...)`.
- An Android runtime built locally by hand shipped its own `.aar`. Apps then failed to build with `D8: Type Uno.UI.UnoWebViewHandlerJavascriptInterface is defined multiple times`.

The script rebuilds the whole set from one commit and verifies the result, so mixes like these can't happen silently.

## Working on the custom build

- **Where changes go:** a fix that applies to Uno goes to Uno as a pull request against `master` in `unoplatform/uno`, then its branch is merged into `custom/7` (merge, don't rebase or cherry-pick: the next merge of the same branch then only brings what changed). Only what Uno won't take goes straight onto `custom/7`, through a pull request against `custom/7` in `Singulink/uno`.
- **Trying a change before it's merged:** commit it on your branch, then publish that branch with `-Ref`, for example `-Ref my-fix-branch`. The script only builds commits, so uncommitted changes need a commit first; a temporary one is fine. Publish `custom/7` again when you're done, so your cache matches everyone else's.
- **Faster development builds:** to build and test inside the Uno repository itself, Uno's [building documentation](https://platform.uno/docs/articles/uno-development/building-uno-ui.html#building-unoui-for-a-single-target-platform) describes `src\crosstargeting_override.props`, which limits builds to one target framework. That file only affects your clone. The publish script ignores it because it builds in its own worktree. Uno's `master` targets .NET 11 as well as 10; to build it with SDK 10, limit it to `net10.0` with `UnoTargetFrameworkOverride`, and leave out the projects that only target .NET 11 (some test and tool projects, and the SamplesApp unless you pass `net10.0-desktop` or `net10.0-browserwasm`).
- **When the apps move to another Uno version:** see [Catching up to a new Uno release](#catching-up-to-a-new-uno-release).
- **What the custom build contains:** Uno's `master`, merged in, plus our pull request branches and our own commits. Your clone's `origin` is the fork, so add Uno's repository as `upstream` once, then list what `custom/7` adds:

  ```powershell
  git -C C:\src\uno remote add upstream https://github.com/unoplatform/uno.git
  git -C C:\src\uno fetch upstream master
  git -C C:\src\uno log --merges --first-parent --oneline upstream/master..custom/7
  git -C C:\src\uno log --no-merges --first-parent --oneline upstream/master..custom/7
  ```

  The first list has one merge per pull request, named after it (`Merge unoplatform/uno#<number> (<branch>) at <commit>`); the second has our own commits.
- **Taking a pull request's later commits:** merge its branch again. Where it conflicts with another pull request's merge, resolve it as the earlier merge commit of that pair did; `git log --merges` on the conflicting file shows it.

## Catching up to a new Uno release

When the apps move to a newer `Uno.Sdk`, the custom build moves to the Uno version that SDK uses:

1. **Find the release.** In the new `Uno.Sdk` package, `targets/netstandard2.0/packages.json` gives the Uno version under the `Core` group, for example `7.1.0-dev.173`. That version's `uno.winui.nuspec` gives the commit it was built from, in the `commit` attribute of `<repository>`. You can read it at `https://api.nuget.org/v3-flatcontainer/uno.winui/<version>/uno.winui.nuspec`.
2. **Merge it into `custom/7`.** Merge rather than rebase: a rebase gives every commit a new ID, which breaks links to them and everyone's clone of the branch. If our pull request branches have moved to a later `master`, merging them takes that part of `master` in too, so the build can be ahead of the release it overrides; then check the dependency versions (see [How apps pick up the custom build](#how-apps-pick-up-the-custom-build)).

   ```powershell
   git -C C:\src\uno fetch upstream master
   git -C C:\src\uno merge <commit>
   ```

   Pull requests Uno has merged since usually merge as the same change. Resolve any conflicts, then check what upstream changed in the areas our fixes touch, for example with `git log <previous base>..<commit> -- <folder>`. A clean merge can still change behavior around a fix.
3. **Update the version.** Set the default of `-Version` in the script, the "Based on" line at the top of this guide and the `Uno.Sdk` and Uno.WinUI versions in the notice at the top of the repository's `README.md`. Also replace the old version number elsewhere in this guide.
4. **Move all the apps together.** Update `Uno.Sdk` in each app's `global.json`, restore, run the publish script, then rebuild and retest the apps. An app still on the old `Uno.Sdk` restores the old Uno version and quietly builds with the stock packages. To keep one on the old version for a while, publish the previous commit for it too: `-Ref <commit> -Version <old version>`.

## Going back to the official packages

To build with the stock Uno packages on one occasion, for example to check whether a bug also happens without the custom build before reporting it to Uno, use a separate package folder for that build. That leaves your normal cache alone:

```powershell
$env:NUGET_PACKAGES = 'C:\Temp\official-nuget'
dotnet build <app project> --disable-build-servers
```

Delete the app's `obj\project.assets.json` before that build. NuGet can otherwise reuse file lists that came from the overridden cache.

To remove the custom build from your normal cache completely, delete the `7.1.0-dev.173` folders of the Uno packages and the apps' `obj` folders, then restore. NuGet then downloads the official packages again:

```powershell
Get-ChildItem "$env:USERPROFILE\.nuget\packages" -Directory -Filter 'uno*' |
    ForEach-Object { Join-Path $_.FullName '7.1.0-dev.173' } |
    Where-Object { Test-Path $_ } |
    Remove-Item -Recurse -Force
```

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `Uno.WinUI 7.1.0-dev.173 isn't in the NuGet cache` | Restore one of the apps first, then run the script. |
| `dotnet runs SDK <version>, not 10.0.403` | Install SDK 10.0.403, or pass the SDK to use with `-SdkVersion`. |
| `<worktree> has changes` | Something edited the script's working copy. Delete it with `git worktree remove --force <worktree>` and run again. |
| `N assemblies weren't rebuilt from <commit>` | A build step failed (see the binlog named in the output), or a package was restored after the last publish. Fix the step if needed and run again. |
| A step fails with `MSB3021`/`MSB3027` (file in use) | Something locked a file type the script doesn't move aside yet. Close the process named in the error and run again. |
| A step fails with `NETSDK1045` (.NET 11 not supported) | A project targets only .NET 11 and ignores the target framework override. Leave it out of the step's filter. |
| Restore fails with `NU1902`/`NU1903` in a dev build of the Uno repository | That's NuGet's vulnerability audit. The publish script turns it off; for your own builds, pass `-p:NuGetAudit=false`. |
| An app throws `MissingMethodException`, `TypeLoadException` or `MissingFieldException` from Uno code | It has assemblies from different builds, or a SkiaSharp/HarfBuzzSharp older than the custom build's. Run the script, check that it ends with no failures, check the dependency versions, then Rebuild the app. On Android, also uninstall the app from the device. |
| An Android build fails in D8 with `Type ... is defined multiple times` | An `.aar` from an old manual override is in the cache. Run the script; it restores the packages' own `.aar` files. |
| An app still behaves as if the fix is missing | Run with `-VerifyOnly`, check the commit it reports, then Rebuild the app. For Android, uninstall the app from the device first. |
