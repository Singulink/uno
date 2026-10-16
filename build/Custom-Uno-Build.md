# Using the custom Uno 6.8 build

Our apps run on a custom build of Uno Platform 6.8. It contains fixes they need that Uno hasn't released yet. This document explains how the custom build reaches the apps, how to set it up on your machine, and how to work on it.

- **Repository:** <https://github.com/Singulink/uno>, a public fork of [unoplatform/uno](https://github.com/unoplatform/uno)
- **Branch:** `custom/6.8`
- **Based on:** the official Uno.WinUI `6.8.0-dev.222`, which is commit `324a97b766` on Uno's `servicing/6.8` branch
- **Publish script:** [`build/Publish-NuGetCacheOverride.ps1`](Publish-NuGetCacheOverride.ps1)

## How apps pick up the custom build

The apps reference the **official, published** Uno packages. Their `global.json` pins `Uno.Sdk` `6.8.0-dev.66`, which brings in `Uno.WinUI` `6.8.0-dev.222` and the related packages from nuget.org. Nothing in an app's repository points at the fork.

The custom build is applied on each developer machine. It **overwrites the files of those package versions in the local NuGet cache** (`%USERPROFILE%\.nuget\packages`, or `%NUGET_PACKAGES%` if set). After that, every app on the machine that restores Uno `6.8.0-dev.222` builds and runs with the custom assemblies. The overwrite uses Uno's own NuGet override build mechanism (`UnoNugetOverrideVersion`), driven by the publish script.

This has consequences:

- **It is per machine.** A machine that never ran the publish script builds the apps with the stock Uno packages, and nothing warns about it. The apps compile and run, just without the fixes. Every machine that builds the apps, including any build server that produces release builds, needs the custom build published to its cache.
- **Clearing the NuGet cache removes it.** That includes `dotnet nuget locals all --clear`, `global-packages --clear`, and deleting `.nuget\packages`. If you do any of these, restore the apps and run the publish script again.
- **iOS builds through Pair to Mac use the Windows cache.** Restore and compilation happen on Windows; the Mac only runs the native steps. You don't need to do anything on the Mac.

## One-time setup

### Prerequisites

- Windows, with Windows PowerShell 5.1 (built in) or PowerShell 7.
- Git.
- .NET SDK 10.0.400 or later. We currently use 10.0.401.
- These .NET workloads: `dotnet workload install android ios maccatalyst tvos`. Only Android and iOS get built, but the mobile projects need every platform they target installed to restore.
- For Android, the Android SDK (API 36) and a JDK 17 or later. Visual Studio's .NET MAUI or mobile workload installs both.
- No Mac or Xcode. The iOS parts of Uno are class libraries, and they compile on Windows.
- About 3 GB of disk: about 1 GB for the repository, plus the build's working copy and outputs.

### Steps

1. **Clone the fork** on the custom build's branch, anywhere you like:

   ```powershell
   git clone --branch custom/6.8 https://github.com/Singulink/uno.git C:\src\uno
   ```

2. **Restore the apps you work on** with every platform they target. For example, open the solution in Visual Studio, or run `dotnet restore` on it. The script only overwrites packages that are already in the cache. Packages that get restored later stay stock until you publish again, such as when you first build for iOS or when an app starts using a new Uno feature. See [When to publish again](#when-to-publish-again).

3. **Run the publish script:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\src\uno\build\Publish-NuGetCacheOverride.ps1
   ```

   The first run takes a little longer while it creates its working copy and builds everything once. Later runs take about 6 minutes. Visual Studio can stay open; see [Files in use](#files-in-use).

4. **Rebuild the apps.** A plain build picks up the changed package files, but use Rebuild (or delete `bin` and `obj`) the first time and whenever in doubt. That matters most for Android, which caches assemblies aggressively.

A successful run ends like this:

```text
== Checking what Skia apps load on desktop, WebAssembly, Android and iOS
63 assemblies built from f834cbef72.
4 still the package's own (no override reaches them; fine while the fork leaves their source alone):
  uno.winui.runtime.webassembly\lib\net10.0\Uno.UI.Runtime.WebAssembly.dll
  uno.winui\analyzers\dotnet\cs\Uno.Analyzers.dll
  uno.winui\lib\net10.0\Uno.Xaml.dll
  uno.winui\uno-runtime\net10.0\skia\Uno.Xaml.dll

The NuGet cache has custom/6.8 (f834cbef72). Rebuild the apps to pick it up.
```

The script exits with code 0 on success and 1 if anything failed. On failure it prints a `Not done:` list.

## When to publish again

Run the script again when any of these happens:

- New commits land on `custom/6.8`. The script builds the branch as it is in your clone, so run `git -C C:\src\uno pull` first.
- You restored a platform or an app for the first time after the last publish, such as a first iOS build.
- The NuGet cache was cleared.
- The script's check reports assemblies that weren't rebuilt (see [Troubleshooting](#troubleshooting)).

To see what your cache currently has without building anything, run it with `-VerifyOnly`:

```powershell
powershell -ExecutionPolicy Bypass -File C:\src\uno\build\Publish-NuGetCacheOverride.ps1 -VerifyOnly
```

You can also check one file by hand. Look at the product version of `%USERPROFILE%\.nuget\packages\uno.winui\6.8.0-dev.222\uno-runtime\net10.0\skia\Uno.UI.dll`, in Properties > Details. The custom build shows `255.255.255.255+<commit>`, and the official one shows `6.8.0-dev.222+324a97b766...`.

## What the script does

```text
Publish-NuGetCacheOverride.ps1 [-Ref <branch or commit>] [-Version <package version>] [-Worktree <path>] [-VerifyOnly]
```

| Parameter | Default | Meaning |
| --- | --- | --- |
| `-Ref` | `custom/6.8` | Branch or commit to build, as it is in your clone. |
| `-Version` | `6.8.0-dev.222` | Version of the Uno packages the apps restore. It must match what the apps' `Uno.Sdk` resolves to. |
| `-Worktree` | `uno-68-publish` next to your clone | Folder the build runs in. |
| `-VerifyOnly` | off | Only tidy and check the cache, without building. |

**1. A working copy of its own.** The script builds in a separate git worktree. By default that's `uno-68-publish` next to your clone, for example `C:\src\uno-68-publish`. The script creates it on the first run, and each run checks it out at the commit `-Ref` points to. This has three effects:

- Only committed code gets published. Unfinished edits in your clone never reach the cache.
- A local `src\crosstargeting_override.props` in your clone has no effect. That file is Uno's way to make development builds faster by building fewer platforms.
- The build outputs stay warm between runs, which is why later runs are fast.

Don't edit anything in that folder. The script refuses to run if it has changes. If it's ever in a bad state, delete it with `git -C C:\src\uno worktree remove --force C:\src\uno-68-publish`; the next run recreates it.

**2. The builds.** All builds use the Release configuration:

| Step | What it builds | Used by |
| --- | --- | --- |
| Skia assemblies, source generators, tasks and dev server | Uno's `build\filters\Uno.UI-packages-skia.slnf`, without the native mobile projects, for net9.0 and net10.0 | All platforms: `Uno.UI`, the Skia runtimes, Toolkit, Fluent theme, Lottie, SVG, the dev server, source generators and build tasks |
| Reference assemblies | `build\filters\Uno.UI-packages-reference.slnf` | What app code compiles against (`lib\net10.0`) |
| WebAssembly WinRT assemblies | `src\Uno.UWP\Uno.Wasm.csproj` | WebAssembly apps |
| Android runtime and WinRT assemblies | `Uno.UI.Runtime.Skia.Android` for `net10.0-android` | Android apps |
| iOS WinRT assemblies | `src\Uno.UWP\Uno.netcoremobile.csproj` for `net10.0-ios26.0` | iOS apps |
| AppleUIKit runtime | `Uno.UI.Runtime.Skia.AppleUIKit` for `net9.0-ios18.0` (the only iOS folder its package has) | iOS apps |

The step builds don't write the cache directly. With `UnoNugetOverrideVersion` set, each project's build copies its output over the matching folder of the restored package (see `src\Uno.CrossTargetting.targets`). An override only writes into folders the package already has, and it never replaces the package's Android archives (`.aar`).

The script also passes `-p:NuGetAudit=false`. Uno treats NuGet vulnerability warnings as errors, so an advisory published after a commit would otherwise stop a build of code that built fine before.

**3. Tidying the cache.** The script deletes `6.8.0-dev.222` folders that NuGet never restored, which older overrides created. It removes target framework folders that the package's `.nupkg` doesn't have. It also puts back the package's own `.aar` files where an older override replaced them.

**4. The check.** For every Uno package at that version in the cache, the script looks at the folders that Skia apps on desktop, WebAssembly, Android and iOS load assemblies from. It sorts each `Uno*.dll` by the commit it was built from, using the commit embedded in its product version:

- **Built from the published commit:** correct. So is an earlier commit with the same `src`, which means commits that only change documentation or this script don't call for another publish.
- **Still the package's own build:** no override reaches it. That is harmless as long as the fork doesn't change that assembly's source. Today that covers `Uno.Xaml`, `Uno.Analyzers`, and the native WebAssembly renderer, which Skia apps don't use.
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

Don't override individual projects by hand, for example with `dotnet build -p:UnoNugetOverrideVersion=6.8.0-dev.222` on one project. Always publish with the script.

Uno's assemblies call each other's internal and public members. Changes in the fork can change those members, and C# binds some things at compile time, such as optional parameter defaults. An assembly compiled against one commit can therefore crash against another. For example:

- A stock Android runtime running against the fork's `Uno.UI` failed on the first keystroke with `MissingMethodException: ImeCompositionEventArgs..ctor(...)`.
- An Android runtime built locally by hand shipped its own `.aar`. Apps then failed to build with `D8: Type Uno.UI.UnoWebViewHandlerJavascriptInterface is defined multiple times`.

The script rebuilds the whole set from one commit and verifies the result, so mixes like these can't happen silently.

## Working on the custom build

- **Where changes go:** commit to a branch off `custom/6.8` and open a pull request against `custom/6.8` in `Singulink/uno`. Fixes that also apply to Uno itself should be reported or proposed upstream in `unoplatform/uno` as well.
- **Trying a change before it's merged:** commit it on your branch, then publish that branch with `-Ref`, for example `-Ref my-fix-branch`. The script only builds commits, so uncommitted changes need a commit first; a temporary one is fine. Publish `custom/6.8` again when you're done, so your cache matches everyone else's.
- **Faster development builds:** to build and test inside the Uno repository itself, Uno's [building documentation](https://platform.uno/docs/articles/uno-development/building-uno-ui.html#building-unoui-for-a-single-target-platform) describes `src\crosstargeting_override.props`, which limits builds to one target framework. That file only affects your clone. The publish script ignores it because it builds in its own worktree.
- **When the apps move to another Uno version:** see [Catching up to a new Uno release](#catching-up-to-a-new-uno-release).
- **What the custom build contains:** see the branch's history on top of the official build it's based on. The commit subjects say what each fix does, and "(port)" marks backports of upstream changes. A few of those ports have since been released by Uno too, and are listed anyway.

  ```powershell
  git -C C:\src\uno log --no-merges --oneline 324a97b766..custom/6.8
  ```

  As of October 2026, it includes:
  - iOS: the scene life cycle, which apps built with Xcode 27 need to launch on iOS 27, plus URL activation and window visibility events.
  - Android: the soft keyboard's Next/Done keys.
  - WebAssembly: soft keyboard Enter key handling, Android and iOS text input, and clipboard.
  - Image memory release and cancellation.
  - Long-press and context flyouts, tooltips, RadioButton groups and popup dismissal.
  - The publish tooling.

## Catching up to a new Uno release

When the apps move to a newer `Uno.Sdk` 6.8 build, the custom build moves to the Uno release that SDK uses:

1. **Find the release.** In the new `Uno.Sdk` package, `targets/netstandard2.0/packages.json` gives the Uno version under the `Core` group, for example `6.8.0-dev.222`. That version's `uno.winui.nuspec` gives the commit it was built from, in the `commit` attribute of `<repository>`. You can read it at `https://api.nuget.org/v3-flatcontainer/uno.winui/<version>/uno.winui.nuspec`.
2. **Merge it into `custom/6.8`.** Merge rather than rebase: a rebase gives every commit a new ID, which breaks links to them and everyone's clone of the branch.

   ```powershell
   git -C C:\src\uno fetch https://github.com/unoplatform/uno.git servicing/6.8
   git -C C:\src\uno merge <commit>
   ```

   Fixes this branch ported early, and that Uno has released since, usually merge as the same change. Resolve any conflicts, then check what upstream changed in the areas the branch's fixes touch, for example with `git log <previous base>..<commit> -- <folder>`. A clean merge can still change behavior around a fix.
3. **Update the version.** Set the default of `-Version` in the script, the "Based on" line at the top of this guide and the version in the notice at the top of the repository's `README.md`. Also replace the old version number elsewhere in this guide.
4. **Move all the apps together.** Update `Uno.Sdk` in each app's `global.json`, restore, run the publish script, then rebuild and retest the apps. An app still on the old `Uno.Sdk` restores the old Uno version and quietly builds with the stock packages. To keep one on the old version for a while, publish the previous commit for it too: `-Ref <commit> -Version <old version>`.

## Going back to the official packages

To build with the stock Uno packages on one occasion, for example to check whether a bug also happens without the custom build before reporting it to Uno, use a separate package folder for that build. That leaves your normal cache alone:

```powershell
$env:NUGET_PACKAGES = 'C:\Temp\official-nuget'
dotnet build <app project> --disable-build-servers
```

Delete the app's `obj\project.assets.json` before that build. NuGet can otherwise reuse file lists that came from the overridden cache.

To remove the custom build from your normal cache completely, delete the `6.8.0-dev.222` folders of the Uno packages and the apps' `obj` folders, then restore. NuGet then downloads the official packages again:

```powershell
Get-ChildItem "$env:USERPROFILE\.nuget\packages" -Directory -Filter 'uno*' |
    ForEach-Object { Join-Path $_.FullName '6.8.0-dev.222' } |
    Where-Object { Test-Path $_ } |
    Remove-Item -Recurse -Force
```

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `Uno.WinUI 6.8.0-dev.222 isn't in the NuGet cache` | Restore one of the apps first, then run the script. |
| `<worktree> has changes` | Something edited the script's working copy. Delete it with `git worktree remove --force <worktree>` and run again. |
| `N assemblies weren't rebuilt from <commit>` | A build step failed (see the binlog named in the output), or a package was restored after the last publish. Fix the step if needed and run again. |
| A step fails with `MSB3021`/`MSB3027` (file in use) | Something locked a file type the script doesn't move aside yet. Close the process named in the error and run again. |
| Restore fails with `NU1902`/`NU1903` in a dev build of the Uno repository | That's NuGet's vulnerability audit. The publish script turns it off; for your own builds, pass `-p:NuGetAudit=false`. |
| An app throws `MissingMethodException`, `TypeLoadException` or `MissingFieldException` from Uno code | It has assemblies from different builds. Run the script, check that it ends with no failures, then Rebuild the app. On Android, also uninstall the app from the device. |
| An Android build fails in D8 with `Type ... is defined multiple times` | An `.aar` from an old manual override is in the cache. Run the script; it restores the packages' own `.aar` files. |
| An app still behaves as if the fix is missing | Run with `-VerifyOnly`, check the commit it reports, then Rebuild the app. For Android, uninstall the app from the device first. |
