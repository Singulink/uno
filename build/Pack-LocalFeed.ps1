<#
.SYNOPSIS
	Packs this branch with Uno's own packaging, under our own version, into a local NuGet feed.

.DESCRIPTION
	Builds the package set CI builds (build/filters/Uno.UI-packages-all.slnf and -windows.slnf), .NET 10 only,
	packs the build/nuget nuspecs and Uno.Sdk (under the id Uno.Sdk, its packages.json naming -Version), and copies
	the packages to -Feed. Apps then set "Uno.Sdk": "<Version>" in global.json with the feed in their nuget.config.

	.NET 11 is left out: the net11.0 groups of the nuspecs are dropped, so a net11.0 app gets the net10.0 assets.
	The macOS native library can't be built on Windows: it is taken from -MacNativeDir (an official package).

.EXAMPLE
	build\Pack-LocalFeed.ps1 -Version 7.1.0-singulink.1 -Feed C:\Temp\uno7\singulink-feed
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')][string]$Version,
	[Parameter(Mandatory)][string]$Feed,
	[string]$MacNativeDir = "$env:USERPROFILE\.nuget\packages\uno.winui.runtime.macos\7.1.0-dev.173\runtimes\osx\native",
	[switch]$SkipWindows,
	[switch]$PackOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo "artifacts\local-feed\$Version"
$log = Join-Path $repo 'artifacts\local-feed\logs'

function Invoke-Step([string]$name, [scriptblock]$block) {
	$sw = [Diagnostics.Stopwatch]::StartNew()
	Write-Host "== $name" -ForegroundColor Cyan
	& $block
	if ($LASTEXITCODE) { throw "$name failed (exit $LASTEXITCODE)" }
	Write-Host ("== {0} done in {1:mm\:ss}" -f $name, $sw.Elapsed) -ForegroundColor Cyan
}

Push-Location $repo
try {
	if ((dotnet --version) -notlike '10.*') { throw 'global.json must pin a .NET 10 SDK.' }
	if (git status --porcelain --untracked-files=no) { throw 'Commit or stash first: packing rewrites tracked files and restores them after.' }
	if ((Test-Path $Feed) -and (Get-ChildItem $Feed -Filter "*.$Version.nupkg")) { throw "$Feed already has $Version packages. NuGet caches by version: bump it." }

	$sha = git rev-parse HEAD
	$branch = git rev-parse --abbrev-ref HEAD
	# What `nbgv cloud` gives CI; build/Uno.UI.Build.csproj and the nuspecs read these.
	$env:NBGV_SemVer2 = $Version
	$env:NBGV_GitCommitId = $sha
	$env:NBGV_BuildingRef = "refs/heads/$branch"
	$env:NBGV_AssemblyInformationalVersion = "$Version+$sha-$($branch -replace '/', '-')"

	New-Item -ItemType Directory -Force $out, $log | Out-Null
	# NetCurrent=net10.0 folds every net11.0 target framework into its net10.0 twin. CI=true keeps Uno.Sdk's pack from
	# deleting uno.sdk.private from the NuGet cache.
	$props = @('-p:Configuration=Release', '-p:NetCurrent=net10.0', '-p:NetCurrentAppleOSVersion=26.0', '-p:CI_Build=true',
		'-p:_IsCIBuild=true', '-p:CI=true', "-p:PackageVersion=$Version", "-p:InformationalVersion=$env:NBGV_AssemblyInformationalVersion",
		"-p:PackageOutputPath=$out")

	try {
		# Renames the _v0 MSBuild task assemblies after the commit, and stamps the version into the nuspecs.
		Invoke-Step 'Prepare' { dotnet msbuild build\Uno.UI.Build.csproj -nologo -v:m '-t:UpdateTasksSHA;PrepareNuGetPackage' '-p:CombinedConfiguration=Release|Any CPU' -p:NetCurrent=net10.0 }

		if (-not $PackOnly) {
			$macNative = 'src\Uno.UI.Runtime.MacOS\UnoNativeMac\build\Release'
			New-Item -ItemType Directory -Force $macNative | Out-Null
			Copy-Item "$MacNativeDir\libUnoNativeMac.*" $macNative

			Invoke-Step 'Build packages' { dotnet build build\filters\Uno.UI-packages-all.slnf -nologo -v:m @props "-bl:$log\all.binlog" }

			if (-not $SkipWindows) {
				$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
				Invoke-Step 'Build Windows packages' { & $msbuild build\filters\Uno.UI-packages-windows.slnf -nologo -r -m -v:m @props -p:BuildGraphics3DGLForWindows=true -p:UNO_DISABLE_ANALYZERS_IN_SAMPLES=true "-bl:$log\windows.binlog" }
			}
		}

		Invoke-Step 'Provision natives' {
			dotnet msbuild src\Uno.UI.Composition.WebGpu\Uno.UI.Composition.WebGpu.csproj -nologo -v:m -t:ProvisionWgpuNativeForPack -p:NetCurrent=net10.0
			if (-not $LASTEXITCODE) { dotnet msbuild src\Uno.UI.Composition.WebGpu.Init\wgpu-wasm.targets -nologo -v:m -t:ProvisionEmdawnForPack }
		}

		Invoke-Step 'Pack nuspecs' {
			$nuspecDir = Join-Path $out 'nuspec'
			New-Item -ItemType Directory -Force $nuspecDir | Out-Null
			$nuspecProps = "NoWarn=NU5100,NU5105,NU5131,NU5123;branch=$env:NBGV_BuildingRef;commitid=$sha;winuisourcepath=net10.0-windows10.0.19041.0;year=$((Get-Date).Year)"
			foreach ($nuspec in Get-ChildItem build\nuget\*.nuspec) {
				# Drop the .NET 11 groups and files: nothing built them.
				$xml = [xml](Get-Content $nuspec.FullName -Raw)
				$ns = @{ x = $xml.DocumentElement.NamespaceURI }
				$net11 = @(Select-Xml -Xml $xml -Namespace $ns -XPath "//x:group[starts-with(@targetFramework, 'net11')] | //x:file[contains(@target, 'net11')]")
				$net11 | ForEach-Object { [void]$_.Node.ParentNode.RemoveChild($_.Node) }
				$filtered = Join-Path $nuspecDir $nuspec.Name
				$xml.Save($filtered)
				& build\external\nuget\NuGet.exe pack $filtered -BasePath build\nuget -OutputDirectory $out -Version $Version -Properties $nuspecProps -NonInteractive
				if ($LASTEXITCODE) { return }
			}
		}

		Invoke-Step 'Pack Uno.Sdk' { dotnet pack src\Uno.Sdk\Uno.Sdk.csproj -nologo -v:m @props -p:PackageId=Uno.Sdk }
	}
	finally {
		# Put back what Prepare rewrote.
		$rewritten = git diff --name-only
		if ($rewritten) { git checkout -- $rewritten }
	}

	New-Item -ItemType Directory -Force $Feed | Out-Null
	$packages = Get-ChildItem $out -Filter "*.$Version.nupkg" | Where-Object Name -NotLike 'Uno.Sdk.Private.*'
	$packages | Copy-Item -Destination $Feed
	Write-Host "$($packages.Count) packages copied to $Feed"
	$packages | ForEach-Object { "  $($_.Name)" }
}
finally {
	Pop-Location
}
