param([Parameter(Mandatory)][string]$FolderPublish)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $FolderPublish).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('bundle-audit-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$targets = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'BundleAudit.targets'))
$escapedSource = [Security.SecurityElement]::Escape($source)
$project = Join-Path $fixture 'test.proj'
@"
<Project>
 <Import Project="$targets" />
 <Target Name="Inputs">
  <ItemGroup>
   <Candidate Include="$escapedSource\CallReceiver.dll"><RelativePath>CallReceiver.dll</RelativePath></Candidate>
   <Candidate Include="$escapedSource\e_sqlite3.dll"><RelativePath>e_sqlite3.dll</RelativePath></Candidate>
   <Candidate Include="$escapedSource\ko\*.resources.dll" Condition="'`$(OmitKorean)' != 'true'"><RelativePath>ko\%(Filename)%(Extension)</RelativePath></Candidate>
   <Outside Include="$escapedSource\CallReceiver.dll" Condition="'`$(ExcludeManaged)' == 'true'" />
  </ItemGroup>
  <BundleAudit Candidates="@(Candidate)" External="@(Outside)" Report="input.tsv" />
 </Target>
 <Target Name="Output">
  <BundleAudit Report="output.tsv" PublishDirectory="output" Executable="CallReceiver.exe" />
 </Target>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding UTF8
function Check([string]$name, [bool]$success, [string[]]$arguments) {
    $log = & dotnet msbuild $project -nologo @arguments 2>&1
    if (($LASTEXITCODE -eq 0) -ne $success) { throw "$name unexpected result: $log" }
    Write-Output "PASS: $name"
}
Push-Location $fixture
try {
    Check 'Valid SDK input' $true @('-t:Inputs')
    Check 'Missing Korean resources rejected' $false @('-t:Inputs', '-p:OmitKorean=true')
    Check 'Excluded managed DLL rejected' $false @('-t:Inputs', '-p:ExcludeManaged=true')
    New-Item -ItemType Directory output | Out-Null
    # Fixtures exercise file checks only; no executable is started.
    Set-Content output/CallReceiver.exe 'fixture'
    Copy-Item (Join-Path $source 'e_sqlite3.dll') output/e_sqlite3.dll
    @("native`te_sqlite3.dll`t`t", "managed`tCallReceiver.dll`t`t") | Set-Content output.tsv
    Check 'Valid external native output' $true @('-t:Output')
    Rename-Item output/e_sqlite3.dll e_sqlite3.missing
    Check 'Missing native DLL rejected' $false @('-t:Output')
    Rename-Item output/e_sqlite3.missing e_sqlite3.dll
    Copy-Item (Join-Path $source 'CallReceiver.dll') output/CallReceiver.dll
    Check 'External managed DLL rejected' $false @('-t:Output')
} finally { Pop-Location }
Write-Output "Validation fixtures retained at $fixture"
exit 0
