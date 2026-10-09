$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$managed = Join-Path $gameRoot 'Afterbeat_Data\Managed'
$output = Join-Path $gameRoot 'Mods\KoreanLocalization'
$sdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK is required to build the mod.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '\Roslyn\bincore\csc.dll')
New-Item -ItemType Directory -Path $output -Force | Out-Null
$arguments = @($compiler, '/nologo', '/target:library', '/nostdlib+', '/optimize+', '/langversion:9.0',
    ('/out:' + (Join-Path $output 'KoreanLocalization.Mod.dll')))
$references = @('mscorlib', 'System', 'System.Core', 'netstandard', 'Assembly-CSharp', 'Assembly-CSharp-firstpass',
    '0Harmony', 'Newtonsoft.Json', 'UnityEngine', 'UnityEngine.CoreModule', 'UnityEngine.UI',
    'UnityEngine.TextRenderingModule', 'UnityEngine.AssetBundleModule', 'Unity.TextMeshPro',
    'Unity.Localization', 'Unity.Addressables', 'Unity.ResourceManager')
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $managed ($reference + '.dll')) }
$arguments += Join-Path $PSScriptRoot 'KoreanLocalizationMod.cs'
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Korean localization build failed.' }
