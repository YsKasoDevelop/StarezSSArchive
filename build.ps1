$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'src/StarResonanceUpscaler/StarResonanceUpscaler.csproj'
$dist = Join-Path $repoRoot ('dist/StarezSSArchive-' + [guid]::NewGuid().ToString('N'))
dotnet publish $project -c Release -r win-x64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RuntimeFrameworkVersion=8.0.20 -p:DebugType=None -p:DebugSymbols=false -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Path (Join-Path $dist 'docs'),(Join-Path $dist 'engine') -Force | Out-Null
foreach ($name in @('LICENSE','README.md','THIRD-PARTY-TERMS.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $dist }
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $dist -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/USER_GUIDE.md') -Destination (Join-Path $dist 'docs')
Copy-Item -LiteralPath (Join-Path $repoRoot 'engine/THIRD-PARTY-NOTICES.md') -Destination (Join-Path $dist 'engine')
$unexpected = Get-ChildItem -LiteralPath $dist -Recurse -File | Where-Object { $_.Extension -in @('.bin','.param','.pdf') -or $_.Name -match 'realesrgan|vcomp140' }
if ($unexpected) { throw 'Unexpected third-party or image document files in package.' }
Write-Host "Build complete: $dist"
