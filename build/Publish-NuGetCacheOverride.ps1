<#
.SYNOPSIS
Builds the Uno packages our apps use from a branch of this fork and writes them over the same version in the NuGet cache.

.DESCRIPTION
Apps keep restoring the published version (6.8.0-dev.212 for Uno.Sdk 6.8.0-dev.60), and the override build target
(UnoNugetOverrideVersion, src/Uno.CrossTargetting.targets) replaces that version's files in the cache with local builds.

The build runs in a worktree of its own, created on first use next to the main checkout and moved to -Ref on every run, so
it always builds a commit and never anything a dev worktree has in progress, and it has no crosstargeting_override.props
narrowing what gets built. It builds what Skia apps load on desktop, WebAssembly, Android and iOS for .NET 10:

  - the Skia and reference assemblies, with the source generators, tasks and dev server (net9.0 and net10.0),
  - the WinRT assemblies that WebAssembly apps take from uno-runtime/<tfm>/webassembly,
  - the Android runtime and the net10.0-android WinRT assemblies,
  - the AppleUIKit runtime (net9.0-ios18.0, the folder its package ships) and the net10.0-ios WinRT assemblies.

Then it tidies the cache (restores the packages' own Android archives, removes folders earlier overrides made up) and checks
that every Uno assembly those apps load was built from the commit, or still is the package's original. Rebuild the apps
afterwards.

Close Visual Studio solutions that use the overridden version first: Visual Studio keeps the cached build tasks loaded, and
they can't be replaced while it does. The dev servers running from that version are stopped automatically.

.EXAMPLE
build\Publish-NuGetCacheOverride.ps1

.EXAMPLE
build\Publish-NuGetCacheOverride.ps1 -VerifyOnly
#>
[CmdletBinding()]
param(
	# Branch or commit to build.
	[string]$Ref = 'dev/mikernet/68-dropdoc',

	# Version of the Uno packages the apps restore.
	[string]$Version = '6.8.0-dev.212',

	# Worktree to build in. Defaults to uno-<major><minor>-publish next to the main checkout. Don't work in it: every run
	# moves it to -Ref, and it refuses to run while it has changes.
	[string]$Worktree,

	# Only tidy and check the cache, without building.
	[switch]$VerifyOnly
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

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

$cache = $env:NUGET_PACKAGES
if (-not $cache) { $cache = Join-Path $env:USERPROFILE '.nuget\packages' }
if (-not (Test-Path (Join-Path $cache "uno.winui\$Version"))) {
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

$failures = New-Object System.Collections.Generic.List[string]

if (-not $VerifyOnly) {
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
		throw "$Worktree has src\crosstargeting_override.props, which narrows the target frameworks. Delete it; the publish worktree builds every framework."
	}

	# Dev servers started from this version hold its host and processor assemblies open.
	$devServerFolder = [regex]::Escape((Join-Path $cache "uno.winui.devserver\$Version"))
	$devServers = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -match $devServerFolder })
	foreach ($process in $devServers) {
		Write-Host "Stopping dev server process $($process.ProcessId)"
		Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
	}

	# MSBuild nodes left by earlier command line builds keep the cached tasks loaded too.
	Invoke-Native dotnet @('build-server', 'shutdown', '--msbuild') | Out-Null

	$locked = @()
	foreach ($file in Get-ChildItem (Join-Path $cache "uno.winui\$Version\buildTransitive\Uno.UI.Tasks"), (Join-Path $cache "uno.winui\$Version\analyzers\dotnet\cs") -Filter *.dll -ErrorAction SilentlyContinue) {
		try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
		catch { $locked += $file.FullName }
	}
	if ($locked) {
		throw "These files are in use, most likely by Visual Studio with a solution that uses Uno $Version open. Close it and run this again.`n$($locked -join "`n")"
	}

	$logs = Join-Path $Worktree 'artifacts\nuget-override'
	New-Item -ItemType Directory -Force $logs | Out-Null

	# A filter of Uno's own package filter, minus what doesn't ship in the packages these apps use. Platform projects build per
	# platform below, with the target framework their package folder needs.
	function New-Filter([string]$Name, [string]$Source, [string[]]$Exclude) {
		$filter = Get-Content (Join-Path $Worktree "build\filters\$Source") -Raw | ConvertFrom-Json
		$projects = @($filter.solution.projects | Where-Object {
				$project = $_
				-not ($project -like '*.netcoremobile.csproj') -and -not ($Exclude | Where-Object { $project -like $_ })
			})
		$path = Join-Path $logs "$Name.slnf"
		@{ solution = @{ path = '..\..\src\Uno.UI.slnx'; projects = $projects } } | ConvertTo-Json -Depth 4 | Set-Content $path -Encoding UTF8
		return $path
	}

	$steps = @(
		@{ Name = 'skia'; Title = 'Skia assemblies, source generators, tasks and dev server'; Target = (New-Filter 'skia' 'Uno.UI-packages-skia.slnf' @()) },
		@{ Name = 'reference'; Title = 'Reference assemblies'; Target = (New-Filter 'reference' 'Uno.UI-packages-reference.slnf' @('Uno.Sdk\*', 'Uno.ReferenceImplComparer\*')) },
		@{ Name = 'wasm-winrt'; Title = 'WebAssembly WinRT assemblies'; Target = 'src\Uno.UWP\Uno.Wasm.csproj' },
		@{ Name = 'android'; Title = 'Android runtime and WinRT assemblies'; Target = 'src\Uno.UI.Runtime.Skia.Android\Uno.UI.Runtime.Skia.Android.csproj'; Framework = 'net10.0-android' },
		@{ Name = 'ios-winrt'; Title = 'iOS WinRT assemblies'; Target = 'src\Uno.UWP\Uno.netcoremobile.csproj'; Framework = 'net10.0-ios26.0' },
		@{ Name = 'ios-runtime'; Title = 'AppleUIKit runtime'; Target = 'src\Uno.UI.Runtime.Skia.AppleUIKit\Uno.UI.Runtime.Skia.AppleUIKit.csproj'; Framework = 'net9.0-ios18.0' }
	)

	foreach ($step in $steps) {
		Write-Step $step.Title
		$arguments = @('build', (Join-Path $Worktree $step.Target), '-c', 'Release', '-nologo', '-nodeReuse:false',
			"-p:UnoNugetOverrideVersion=$Version", '-p:GeneratePackageOnBuild=false',
			'-consoleLoggerParameters:ErrorsOnly', "-bl:$(Join-Path $logs "$($step.Name).binlog")")
		if ($step.ContainsKey('Framework')) { $arguments += @('-f', $step.Framework) }

		$watch = [Diagnostics.Stopwatch]::StartNew()
		Invoke-Native dotnet $arguments
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

$packages = @(Get-ChildItem $cache -Directory | ForEach-Object { Join-Path $_.FullName $Version } | Where-Object { Test-Path $_ })
foreach ($folder in $packages) {
	$id = Split-Path (Split-Path $folder -Parent) -Leaf
	$nupkg = Get-ChildItem $folder -Filter *.nupkg -ErrorAction SilentlyContinue | Select-Object -First 1

	if (-not $nupkg) {
		# Folders earlier overrides made for projects that ship inside another package. NuGet never restored them.
		if (Test-Path (Join-Path $folder '.nupkg.metadata')) { continue }
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
					Write-Host "Removing $id\$Version\$root\$($tfmFolder.Name), which the package doesn't have"
					Remove-Item $tfmFolder.FullName -Recurse -Force
				}
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
				Write-Host "Restoring the package's own $id\$Version\$($entry.FullName)"
				[IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
			}
		}
	}
	finally {
		$zip.Dispose()
	}
}

Write-Step "Checking what Skia apps load on desktop, WebAssembly, Android and iOS"

# Folders those apps load Uno assemblies from. The native renderers' folders (uno.winui's lib/net10.0-android36.0, for
# example) are replaced at build time by uno-runtime/<tfm>/skia, so they aren't checked.
$checked = @{
	'*' = @('lib\net10.0', 'lib\netstandard2.0', 'uno-runtime\net10.0\skia')
	'uno.winui' = @('analyzers\dotnet\cs', 'buildTransitive\Uno.UI.Tasks')
	'uno.winrt' = @('uno-runtime\net10.0\webassembly', 'lib\net10.0-android36.0', 'lib\net10.0-ios26.0')
	'uno.foundation' = @('uno-runtime\net10.0\webassembly', 'lib\net10.0-android36.0', 'lib\net10.0-ios26.0')
	'uno.foundation.logging' = @('lib\net9.0')
	'uno.foundation.runtime.webassembly' = @('uno-runtime\net10.0\webassembly')
	'uno.winui.devserver' = @('tools\rc\host\net10.0', 'tools\rc\processors\net10.0')
	'uno.winui.runtime.skia.android' = @('lib\net10.0-android36.0')
	'uno.winui.runtime.skia.appleuikit' = @('lib\net9.0-ios18.0')
}

$current = 0
$original = New-Object System.Collections.Generic.List[string]
$stale = New-Object System.Collections.Generic.List[string]

foreach ($folder in Get-ChildItem $cache -Directory -Filter 'uno*' | ForEach-Object { Join-Path $_.FullName $Version } | Where-Object { Test-Path (Join-Path $_ '*.nupkg') }) {
	$id = Split-Path (Split-Path $folder -Parent) -Leaf
	$nuspec = Get-ChildItem $folder -Filter *.nuspec | Select-Object -First 1
	$repository = ([xml](Get-Content $nuspec.FullName -Raw)).SelectSingleNode("//*[local-name()='repository']")
	$originalCommit = $null
	if ($repository -and $repository.GetAttribute('commit')) { $originalCommit = $repository.GetAttribute('commit').Substring(0, 10) }

	$subfolders = @($checked['*'])
	if ($checked.ContainsKey($id)) { $subfolders += $checked[$id] }

	foreach ($subfolder in $subfolders) {
		foreach ($dll in Get-ChildItem (Join-Path $folder $subfolder) -Filter 'Uno*.dll' -ErrorAction SilentlyContinue) {
			$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll.FullName).ProductVersion
			if ($productVersion -notmatch '\+([0-9a-f]{10})') { continue }
			$built = $Matches[1]
			$name = "$id\$subfolder\$($dll.Name)"

			if ($built -eq $shortCommit) { $current++ }
			elseif ($built -eq $originalCommit) { $original.Add($name) }
			else { $stale.Add("$name ($built)") }
		}
	}
}

Write-Host "$current assemblies built from $shortCommit."
if ($original.Count) {
	Write-Host "$($original.Count) still the package's own (no override reaches them; fine while the fork leaves their source alone):"
	$original | Sort-Object | ForEach-Object { Write-Host "  $_" }
}
if ($stale.Count) {
	Write-Host "$($stale.Count) built from another commit:" -ForegroundColor Red
	$stale | Sort-Object | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
	$failures.Add("$($stale.Count) assemblies weren't rebuilt from $shortCommit")
}

if ($failures.Count) {
	Write-Host "`nNot done:" -ForegroundColor Red
	$failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
	exit 1
}

Write-Host "`nThe NuGet cache has $Ref ($shortCommit). Rebuild the apps to pick it up." -ForegroundColor Green
