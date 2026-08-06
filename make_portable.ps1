# Galgu Watch 포터블 배포본 생성 스크립트
# - csproj의 <Version>을 읽어 실행파일명과 zip명에 버전을 자동 표기
# - 주의: 이 파일은 UTF-8(BOM) 인코딩을 유지해야 함 (한글 문자열 포함)
$ErrorActionPreference = 'Stop'
$repo = Join-Path $env:USERPROFILE 'Desktop\Claude\Galgu_Watch'
$proj = Join-Path $repo 'GalguWatch\GalguWatch.csproj'
$dist = Join-Path $repo 'dist\GalguWatch'

[xml]$csproj = Get-Content $proj
$ver = @($csproj.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
if (-not $ver) { throw 'csproj에서 <Version>을 찾지 못했습니다' }
$v = ($ver -split '\.')[0..1] -join '.'

$publishArgs = @(
    'publish', $proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
    '-o', $dist, '-nologo', '-v', 'minimal'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 실패' }

Remove-Item (Join-Path $dist '*.xml') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dist 'GalguWatch_v*.exe') -Force -ErrorAction SilentlyContinue
Rename-Item (Join-Path $dist 'GalguWatch.exe') "GalguWatch_v$v.exe"
Copy-Item (Join-Path $repo '사용법.txt') $dist -Force

$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "GalguWatch_포터블_v$v.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $dist -DestinationPath $zip
"완료: $zip ($([Math]::Round((Get-Item $zip).Length / 1MB))MB) — 실행파일: GalguWatch_v$v.exe"
