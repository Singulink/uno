<#
.SYNOPSIS
Builds the Uno packages our apps use from a branch of this fork and writes them over the same version in the NuGet cache.

.DESCRIPTION
Apps keep restoring the published version (7.1.0-dev.173 for Uno.Sdk 7.1.0-dev.18), and the override build target
(UnoNugetOverrideVersion, src/Uno.CrossTargetting.targets) replaces that version's files in the cache with local builds.

The build runs in a worktree of its own, created on first use next to the main checkout and moved to -Ref on every run, so
it always builds a commit and never anything a dev worktree has in progress, and it has no crosstargeting_override.props
narrowing what gets built. It builds what Skia apps load on desktop, WebAssembly, Android and iOS for .NET 10 (-Framework):

  - the Skia and reference assemblies, with the source generators, tasks and dev server,
  - the WinRT assemblies that WebAssembly apps take from uno-runtime/<tfm>/wasm,
  - the Android runtime and the <tfm>-android WinRT assemblies,
  - the AppleUIKit runtime and the <tfm>-ios WinRT assemblies.

Uno 7 targets .NET 10 and 11, and .NET 11 needs a newer SDK than the apps build with, so the .NET 11 folders of the packages
stay the official ones. Every dotnet call runs with SDK -SdkVersion, pinned by a global.json the script writes in its log
folder.

Then it tidies the cache (restores the packages' own Android archives, removes folders earlier overrides made up) and checks
that every Uno assembly those apps load was built from the commit, or still is the package's original. Rebuild the apps
afterwards.

It never touches another version's folders, and refuses any Uno 6 version: the custom 6.8 build in the cache has its own
script on custom/6.8.

Visual Studio and running dev servers can stay open. Files they keep loaded (the build tasks, the dev server host) are moved
aside and replaced, and they use the new ones once restarted; the script names what to restart when that happens.

See Custom-Uno-Build.md next to this script for setting up a machine, working on the custom build and troubleshooting.

.EXAMPLE
build\Publish-NuGetCacheOverride.ps1

.EXAMPLE
build\Publish-NuGetCacheOverride.ps1 -DryRun

.EXAMPLE
build\Publish-NuGetCacheOverride.ps1 -VerifyOnly
#>
[CmdletBinding()]
param(
	# Branch or commit to build.
	[string]$Ref = 'custom/7',

	# Version of the Uno packages the apps restore (the Core group version in the Uno.Sdk package's packages.json).
	[string]$Version = '7.1.0-dev.173',

	# .NET version the apps target. The packages' folders for it are the ones built and checked.
	[string]$Framework = 'net10.0',

	# .NET SDK the builds run with. It must support -Framework and build what the apps build with.
	[string]$SdkVersion = '10.0.403',

	# Worktree to build in. Defaults to uno-<major><minor>-publish next to the main checkout. Don't work in it: every run
	# moves it to -Ref, and it refuses to run while it has changes.
	[string]$Worktree,

	# Only tidy and check the cache, without building.
	[switch]$VerifyOnly,

	# Change nothing: report the builds a run would make, the cache folders and assemblies they would replace and what
	# tidying would change, then check the cache as it is.
	[switch]$DryRun
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Names the processes holding files open, through the Restart Manager.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnoPublish
{
	public static class FileLocks
	{
		[StructLayout(LayoutKind.Sequential)]
		struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct RM_PROCESS_INFO
		{
			public RM_UNIQUE_PROCESS Process;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
			public int ApplicationType;
			public uint AppStatus;
			public uint TSSessionId;
			[MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
		}

		[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, string key);
		[DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);
		[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint handle, uint files, string[] names, uint apps, RM_UNIQUE_PROCESS[] processes, uint services, string[] serviceNames);
		[DllImport("rstrtmgr.dll")] static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] infos, ref uint reasons);

		// "name|process id" for every process holding one of the files.
		public static List<string> GetHolders(string[] paths)
		{
			var holders = new List<string>();
			uint handle;
			if (RmStartSession(out handle, 0, Guid.NewGuid().ToString()) != 0) return holders;
			try
			{
				if (RmRegisterResources(handle, (uint)paths.Length, paths, 0, null, 0, null) != 0) return holders;
				uint needed, count = 0, reasons = 0;
				RmGetList(handle, out needed, ref count, null, ref reasons);
				if (needed == 0) return holders;
				var infos = new RM_PROCESS_INFO[needed];
				count = needed;
				if (RmGetList(handle, out needed, ref count, infos, ref reasons) != 0) return holders;
				for (int i = 0; i < count; i++) holders.Add(infos[i].strAppName + "|" + infos[i].Process.dwProcessId);
			}
			finally { RmEndSession(handle); }
			return holders;
		}
	}
}
'@

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-DryRun([string]$Text) { Write-Host "  [dry run] $Text" -ForegroundColor DarkYellow }

# Windows PowerShell turns what a native command writes to stderr into errors when output is redirected, which
# $ErrorActionPreference = 'Stop' would make fatal, so native commands only fail by their exit code.
function Invoke-Native([string]$Command, [string[]]$Arguments) {
	$previous = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	try { & $Command @Arguments }
	finally { $ErrorActionPreference = $previous }
}

function Invoke-Git {
	$output = Invoke-Native git $args
	if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
	return $output
}

# The Uno 6.8 packages in the cache are a custom build of their own (custom/6.8 and its script), so this script stays away
# from every Uno 6 version.
if ($Version -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 7) {
	throw "-Version $Version isn't an Uno 7 version. This script only publishes Uno 7 or later; the custom 6.8 build has its own on custom/6.8."
}

$cache = $env:NUGET_PACKAGES
if (-not $cache) { $cache = Join-Path $env:USERPROFILE '.nuget\packages' }
$cache = [IO.Path]::GetFullPath($cache)
$cacheHasVersion = Test-Path (Join-Path $cache "uno.winui\$Version")
if (-not $cacheHasVersion -and -not $DryRun) {
	throw "Uno.WinUI $Version isn't in the NuGet cache ($cache). Restore an app that uses it first."
}

$repoRoot = Invoke-Git -C $PSScriptRoot rev-parse --show-toplevel
$commit = Invoke-Git -C $repoRoot rev-parse --verify "$Ref^{commit}"
$shortCommit = $commit.Substring(0, 10)

if (-not $Worktree) {
	$mainCheckout = Split-Path ([IO.Path]::GetFullPath((Invoke-Git -C $repoRoot rev-parse --path-format=absolute --git-common-dir))) -Parent
	$majorMinor = ($Version.Split('.')[0..1]) -join ''
	$Worktree = Join-Path (Split-Path $mainCheckout -Parent) "uno-$majorMinor-publish"
}
$Worktree = [IO.Path]::GetFullPath($Worktree)
$logs = Join-Path $Worktree 'artifacts\nuget-override'

$failures = New-Object System.Collections.Generic.List[string]

# The cache folders of the version this run publishes: never another version's.
function Get-VersionFolders([string]$Filter = 'uno*') {
	@(Get-ChildItem $cache -Directory -Filter $Filter -ErrorAction SilentlyContinue |
			ForEach-Object { Join-Path $_.FullName $Version } |
			Where-Object { Test-Path $_ })
}

# A filter of Uno's own package filter, minus what doesn't ship in the packages these apps use. Platform projects build per
# platform below, with the target framework their package folder needs.
# Without a source filter, the filter has the given projects.
function New-Filter([string]$Name, [string]$Source, [string[]]$Exclude, [string[]]$Projects) {
	if ($Source) {
		$filter = Get-Content (Join-Path $Worktree "build\filters\$Source") -Raw | ConvertFrom-Json
		$Projects = $filter.solution.projects
	}
	$projects = @($Projects | Where-Object {
			$project = $_
			-not ($project -like '*.netcoremobile.csproj') -and -not ($Exclude | Where-Object { $project -like $_ })
		})
	$path = "artifacts\nuget-override\$Name.slnf"
	if (-not $DryRun) {
		@{ solution = @{ path = '..\..\src\Uno.UI.slnx'; projects = $projects } } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Worktree $path) -Encoding UTF8
	}
	return $path
}

# The TFM folders the apps load the per-platform builds from. The API level of Android folders differs between packages.
# Every step also sets UnoTargetFrameworkOverride: restore evaluates every target framework of the projects it reaches, and
# -SdkVersion can't evaluate the .NET 11 ones.
$android = "$Framework-android"
$ios = "$Framework-ios26.0"

$steps = @(
	@{ Name = 'generic'; Title = 'Skia assemblies, source generators, tasks and dev server'; Filter = @('Uno.UI-packages-generic.slnf', @()); TfmOverride = $Framework },
	@{ Name = 'reference'; Title = 'Reference assemblies'; Filter = @('Uno.UI-packages-reference.slnf', @('Uno.Sdk\*', 'Uno.ReferenceImplComparer\*')); TfmOverride = $Framework },
	@{ Name = 'wasm'; Title = 'WebAssembly WinRT assemblies'; Filter = @('Uno.UI-packages-wasm.slnf', @()); TfmOverride = $Framework },
	# Skia add-ins none of Uno's package filters above have.
	@{ Name = 'addins'; Title = 'WebGPU and OpenGL graphics and spell checking add-ins'; Filter = @('', @(), @('AddIns\Uno.WinUI.Graphics3DWebGpu\Uno.WinUI.Graphics3DWebGpu.csproj', 'AddIns\Uno.WinUI.Graphics3DGL\Uno.WinUI.Graphics3DGL.csproj', 'AddIns\Uno.WinUI.SpellChecking\Uno.WinUI.SpellChecking.csproj')); TfmOverride = $Framework },
	@{ Name = 'android'; Title = 'Android runtime and WinRT assemblies'; Target = 'src\Uno.UI.Runtime.Android\Uno.UI.Runtime.Android.csproj'; Framework = $android; TfmOverride = $android },
	@{ Name = 'ios-winrt'; Title = 'iOS WinRT assemblies'; Target = 'src\Uno.WinRT\Uno.WinRT.netcoremobile.csproj'; Framework = $ios; TfmOverride = "$Framework-ios" },
	@{ Name = 'ios-runtime'; Title = 'AppleUIKit runtime'; Target = 'src\Uno.UI.Runtime.AppleUIKit\Uno.UI.Runtime.AppleUIKit.csproj'; Framework = $ios; TfmOverride = "$Framework-ios" }
)

# Folders those apps load Uno assemblies from. '*' applies to every package; a folder may hold a wildcard.
$checked = [ordered]@{
	'*' = @("lib\$Framework", 'lib\netstandard2.0')
	'uno.winui' = @('analyzers\dotnet\cs', 'buildTransitive\Uno.UI.Tasks')
	'uno.winrt' = @("uno-runtime\$Framework\generic", "uno-runtime\$Framework\wasm", "lib\$android*", "lib\$ios")
	'uno.foundation' = @("uno-runtime\$Framework\generic", "uno-runtime\$Framework\wasm", "lib\$android*", "lib\$ios")
	'uno.winui.devserver' = @("tools\rc\host\$Framework", "tools\rc\processors\$Framework")
	'uno.winui.runtime.android' = @("lib\$android*")
	'uno.winui.runtime.appleuikit' = @("lib\$ios")
}

function Get-CheckedFolders([string]$PackageFolder) {
	$id = Split-Path (Split-Path $PackageFolder -Parent) -Leaf
	$subfolders = @($checked['*'])
	if ($checked.Contains($id)) { $subfolders += $checked[$id] }
	foreach ($subfolder in $subfolders) {
		foreach ($folder in @(Resolve-Path (Join-Path $PackageFolder $subfolder) -ErrorAction SilentlyContinue)) {
			if (Test-Path $folder.Path -PathType Container) {
				[pscustomobject]@{ Id = $id; Folder = $folder.Path; Name = $folder.Path.Substring($PackageFolder.Length + 1) }
			}
		}
	}
}

# Pins the SDK: dotnet picks it from the global.json of the folder it runs in.
$sdkFolder = Join-Path $logs 'sdk'
function Invoke-Dotnet([string[]]$Arguments) {
	Push-Location $sdkFolder
	try { Invoke-Native dotnet $Arguments }
	finally { Pop-Location }
}

if ($DryRun) {
	Write-Step "Dry run: $Ref ($shortCommit) over Uno $Version in $cache, for $Framework with SDK $SdkVersion"
	Write-Host 'Nothing is built, moved, deleted or written.'
	if (-not $cacheHasVersion) {
		Write-Host "Uno.WinUI $Version isn't in the cache: a run would stop here until an app restores it." -ForegroundColor Yellow
	}

	if (Test-Path $Worktree) {
		Write-DryRun "would check $Worktree has no changes and check it out at $shortCommit"
	}
	else {
		Write-DryRun "would create the worktree $Worktree at $shortCommit"
	}

	Write-Step 'Builds'
	foreach ($step in $steps) {
		$target = if (-not $step.ContainsKey('Filter')) { $step.Target }
		elseif (-not $step.Filter[0]) { $step.Filter[2] -join ', ' }
		else { "$($step.Filter[0]) without its netcoremobile projects$(if ($step.Filter[1]) { ' and ' + ($step.Filter[1] -join ', ') })" }
		$frameworkArgument = "-p:UnoTargetFrameworkOverride=$($step.TfmOverride)"
		if ($step.ContainsKey('Framework')) { $frameworkArgument = "-f $($step.Framework) $frameworkArgument" }
		Write-DryRun "$($step.Title): dotnet build $target -c Release $frameworkArgument -p:UnoNugetOverrideVersion=$Version"
	}

	$inUse = @()
	foreach ($file in Get-ChildItem (Get-VersionFolders) -Recurse -File -Include *.dll, *.exe -ErrorAction SilentlyContinue) {
		try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
		catch { $inUse += $file.FullName }
	}
	if ($inUse) {
		Write-DryRun "would move $($inUse.Count) files in use aside, and name the processes holding them:"
		foreach ($group in [UnoPublish.FileLocks]::GetHolders([string[]]$inUse) | Group-Object { $_.Split('|')[0] }) {
			Write-Host "    $($group.Name) (process $(($group.Group | ForEach-Object { $_.Split('|')[1] }) -join ', '))"
		}
	}

	Write-Step "Cache folders the builds would replace (Uno*.dll by the commit they're built from now)"
	foreach ($folder in Get-VersionFolders) {
		foreach ($checkedFolder in Get-CheckedFolders $folder) {
			$dlls = @(Get-ChildItem $checkedFolder.Folder -Filter 'Uno*.dll')
			if (-not $dlls) { continue }
			$byCommit = $dlls | Group-Object {
				$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductVersion
				if ($productVersion -match '\+([0-9a-f]{10})') { $Matches[1] } else { '?' }
			}
			$summary = ($byCommit | ForEach-Object { "$($_.Count) from $($_.Name)" }) -join ', '
			Write-DryRun "$($checkedFolder.Id)\$Version\$($checkedFolder.Name): $($dlls.Count) assemblies ($summary)"
		}
	}
}
elseif (-not $VerifyOnly) {
	Write-Step "Preparing $Worktree at $Ref ($shortCommit)"

	if (-not (Test-Path $Worktree)) {
		Invoke-Git -C $repoRoot worktree add --quiet --detach $Worktree $commit | Out-Null
	}
	else {
		if ((Invoke-Git -C $Worktree rev-parse --path-format=absolute --git-common-dir) -ne (Invoke-Git -C $repoRoot rev-parse --path-format=absolute --git-common-dir)) {
			throw "$Worktree isn't a worktree of $repoRoot."
		}

		$changes = Invoke-Git -C $Worktree status --porcelain
		if ($changes) {
			throw "$Worktree has changes. It only builds commits; move them to a dev worktree and commit them there.`n$($changes -join "`n")"
		}

		Invoke-Git -C $Worktree checkout --quiet --detach $commit | Out-Null
	}

	if (Test-Path (Join-Path $Worktree 'src\crosstargeting_override.props')) {
		throw "$Worktree has src\crosstargeting_override.props, which narrows the target frameworks. Delete it; the publish worktree builds what the apps load."
	}

	New-Item -ItemType Directory -Force $sdkFolder | Out-Null
	@{ sdk = @{ version = $SdkVersion; rollForward = 'latestPatch'; allowPrerelease = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $sdkFolder 'global.json') -Encoding ASCII
	$sdkInUse = (Invoke-Dotnet @('--version') | Select-Object -Last 1)
	if ("$sdkInUse".Trim() -notlike "$($SdkVersion.Substring(0, $SdkVersion.LastIndexOf('.')))*") {
		throw "dotnet runs SDK $sdkInUse, not $SdkVersion. Install SDK $SdkVersion or pass the one to use with -SdkVersion."
	}

	# MSBuild nodes left by earlier command line builds keep the cached tasks loaded for nothing.
	Invoke-Dotnet @('build-server', 'shutdown', '--msbuild') | Out-Null

	# Visual Studio, its MSBuild nodes and running dev servers keep files of this version loaded (build tasks, the dev server
	# host), so they can't be overwritten. A loaded file can still be renamed, though: it moves aside, a copy takes its place
	# for the override to replace, and whatever loaded it goes on with the old one until it restarts. Earlier runs' are
	# deleted once free.
	$packageFolders = Get-VersionFolders
	$movedAside = @()
	foreach ($file in Get-ChildItem $packageFolders -Recurse -File -Include *.dll, *.exe) {
		try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
		catch {
			$aside = "$($file.FullName).inuse-$(Get-Date -Format yyyyMMddHHmmss)"
			Move-Item $file.FullName $aside
			Copy-Item $aside $file.FullName
			$movedAside += $aside
		}
	}
	foreach ($file in Get-ChildItem $packageFolders -Recurse -File -Filter *.inuse-*) {
		Remove-Item $file.FullName -Force -ErrorAction SilentlyContinue
	}

	New-Item -ItemType Directory -Force $logs | Out-Null

	foreach ($step in $steps) {
		Write-Step $step.Title
		$target = if ($step.ContainsKey('Filter')) { New-Filter $step.Name $step.Filter[0] $step.Filter[1] $step.Filter[2] } else { $step.Target }
		# NuGetAudit: Uno makes audit warnings errors, so a vulnerability published after the commit (in the dev server's
		# dependencies, say) would stop a build of the same code that built before. Upstream's own updates bring the fixes.
		$arguments = @('build', (Join-Path $Worktree $target), '-c', 'Release', '-nologo', '-nodeReuse:false',
			"-p:UnoNugetOverrideVersion=$Version", '-p:GeneratePackageOnBuild=false', '-p:NuGetAudit=false',
			'-consoleLoggerParameters:ErrorsOnly', "-bl:$(Join-Path $logs "$($step.Name).binlog")")
		if ($step.ContainsKey('Framework')) { $arguments += @('-f', $step.Framework) }
		if ($step.ContainsKey('TfmOverride')) { $arguments += "-p:UnoTargetFrameworkOverride=$($step.TfmOverride)" }

		$watch = [Diagnostics.Stopwatch]::StartNew()
		Invoke-Dotnet $arguments
		$watch.Stop()

		if ($LASTEXITCODE -ne 0) {
			$failures.Add("$($step.Title) failed (log: $logs\$($step.Name).binlog)")
			Write-Host "Failed after $([int]$watch.Elapsed.TotalMinutes) min" -ForegroundColor Red
		}
		else {
			Write-Host "Done in $([int]$watch.Elapsed.TotalMinutes) min"
		}
	}
}

Write-Step "Tidying the Uno $Version packages in $cache"

foreach ($folder in Get-VersionFolders) {
	$id = Split-Path (Split-Path $folder -Parent) -Leaf
	$nupkg = Get-ChildItem $folder -Filter *.nupkg -ErrorAction SilentlyContinue | Select-Object -First 1

	if (-not $nupkg) {
		# Folders earlier overrides made for projects that ship inside another package. NuGet never restored them.
		if (Test-Path (Join-Path $folder '.nupkg.metadata')) { continue }
		if ($DryRun) { Write-DryRun "would remove $id\$Version, which only an override made"; continue }
		Write-Host "Removing $id\$Version, which only an override made"
		Remove-Item $folder -Recurse -Force
		$parent = Split-Path $folder -Parent
		if (-not (Get-ChildItem $parent -Force)) { Remove-Item $parent -Force }
		continue
	}

	$zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
	try {
		$entries = @{}
		foreach ($entry in $zip.Entries) { $entries[[Uri]::UnescapeDataString($entry.FullName).ToLowerInvariant()] = $entry }

		# Target framework folders the package doesn't have: NuGet would pick them up at the next restore.
		foreach ($root in 'lib', 'ref') {
			foreach ($tfmFolder in Get-ChildItem (Join-Path $folder $root) -Directory -ErrorAction SilentlyContinue) {
				$prefix = "$root/$($tfmFolder.Name)/".ToLowerInvariant()
				if (-not ($entries.Keys | Where-Object { $_.StartsWith($prefix) } | Select-Object -First 1)) {
					if ($DryRun) { Write-DryRun "would remove $id\$Version\$root\$($tfmFolder.Name), which the package doesn't have"; continue }
					Write-Host "Removing $id\$Version\$root\$($tfmFolder.Name), which the package doesn't have"
					Remove-Item $tfmFolder.FullName -Recurse -Force
				}
			}
		}

		# Files in those folders the package doesn't have: a project that ships in another package wrote them, and NuGet would
		# hand them to apps at the next restore as this package's assets. Override markers (dot files) stay.
		foreach ($root in 'lib', 'ref') {
			foreach ($file in Get-ChildItem (Join-Path $folder $root) -Recurse -File -ErrorAction SilentlyContinue) {
				if ($file.Name.StartsWith('.') -or $file.Name -like '*.inuse-*') { continue }
				$relative = $file.FullName.Substring($folder.Length + 1).Replace('\', '/').ToLowerInvariant()
				if ($entries.ContainsKey($relative)) { continue }
				if ($DryRun) { Write-DryRun "would remove $id\$Version\$($file.FullName.Substring($folder.Length + 1)), which the package doesn't have"; continue }
				Write-Host "Removing $id\$Version\$($file.FullName.Substring($folder.Length + 1)), which the package doesn't have"
				Remove-Item $file.FullName -Force
			}
		}

		# The package's own Android archives, never a local build's (see _UnoOverrideNuget).
		foreach ($key in @($entries.Keys | Where-Object { $_.EndsWith('.aar') })) {
			$entry = $entries[$key]
			$path = Join-Path $folder ([Uri]::UnescapeDataString($entry.FullName))
			$sha = [Security.Cryptography.SHA256]::Create()
			$stream = $entry.Open()
			try { $expected = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() }
			$actual = if (Test-Path $path) { (Get-FileHash $path -Algorithm SHA256).Hash } else { $null }
			if ($actual -ne $expected) {
				if ($DryRun) { Write-DryRun "would restore the package's own $id\$Version\$($entry.FullName)"; continue }
				Write-Host "Restoring the package's own $id\$Version\$($entry.FullName)"
				[IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
			}
		}
	}
	finally {
		$zip.Dispose()
	}
}

Write-Step "Checking what Skia apps load on desktop, WebAssembly, Android and iOS ($Framework)"

# An assembly built from an earlier commit is still current when nothing under src changed since, so commits that only
# touch this script or documentation don't call for another publish.
$sameSource = @{ $shortCommit = $true }
function Test-SameSource([string]$Built) {
	if (-not $sameSource.ContainsKey($Built)) {
		$previous = $ErrorActionPreference
		$ErrorActionPreference = 'Continue'
		try { & git -C $repoRoot diff --quiet $Built $commit -- src 2>$null | Out-Null }
		finally { $ErrorActionPreference = $previous }
		$sameSource[$Built] = $LASTEXITCODE -eq 0
	}
	return $sameSource[$Built]
}

$current = 0
$currentCommits = New-Object System.Collections.Generic.HashSet[string]
$original = New-Object System.Collections.Generic.List[string]
$stale = New-Object System.Collections.Generic.List[string]

foreach ($folder in Get-VersionFolders | Where-Object { Test-Path (Join-Path $_ '*.nupkg') }) {
	$nuspec = Get-ChildItem $folder -Filter *.nuspec | Select-Object -First 1
	$repository = ([xml](Get-Content $nuspec.FullName -Raw)).SelectSingleNode("//*[local-name()='repository']")
	$originalCommit = $null
	if ($repository -and $repository.GetAttribute('commit')) { $originalCommit = $repository.GetAttribute('commit').Substring(0, 10) }

	foreach ($checkedFolder in Get-CheckedFolders $folder) {
		foreach ($dll in Get-ChildItem $checkedFolder.Folder -Filter 'Uno*.dll' -ErrorAction SilentlyContinue) {
			$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll.FullName).ProductVersion
			if ($productVersion -notmatch '\+([0-9a-f]{10})') { continue }
			$built = $Matches[1]
			$name = "$($checkedFolder.Id)\$($checkedFolder.Name)\$($dll.Name)"

			if ($built -eq $originalCommit) { $original.Add($name) }
			elseif (Test-SameSource $built) { $current++; $currentCommits.Add($built) | Out-Null }
			else { $stale.Add("$name ($built)") }
		}
	}
}

$builtFrom = $shortCommit
if (@($currentCommits | Where-Object { $_ -ne $shortCommit }).Count) {
	$builtFrom = "$(@($currentCommits) -join ', ') (the same source as $shortCommit)"
}
Write-Host "$current assemblies built from $builtFrom."
if ($original.Count) {
	Write-Host "$($original.Count) still the package's own$(if (-not $DryRun) { ' (no override reaches them; fine while the fork leaves their source alone)' }):"
	$original | Sort-Object | ForEach-Object { Write-Host "  $_" }
}
if ($stale.Count) {
	Write-Host "$($stale.Count) built from another commit:" -ForegroundColor Red
	$stale | Sort-Object | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
	$failures.Add("$($stale.Count) assemblies weren't rebuilt from $shortCommit")
}

if ($DryRun) {
	Write-Host "`nDry run done: nothing changed." -ForegroundColor Green
	exit 0
}

if (-not $VerifyOnly -and $movedAside) {
	Write-Host "`n$($movedAside.Count) files were in use. Restart these to load the new copies:" -ForegroundColor Yellow
	foreach ($group in [UnoPublish.FileLocks]::GetHolders([string[]]$movedAside) | Group-Object { $_.Split('|')[0] }) {
		Write-Host "  $($group.Name) (process $(($group.Group | ForEach-Object { $_.Split('|')[1] }) -join ', '))" -ForegroundColor Yellow
	}
}

if ($failures.Count) {
	Write-Host "`nNot done:" -ForegroundColor Red
	$failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
	exit 1
}

Write-Host "`nThe NuGet cache has $Ref ($shortCommit). Rebuild the apps to pick it up." -ForegroundColor Green
