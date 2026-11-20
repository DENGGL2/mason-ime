param(
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [Parameter(Mandatory=$true)][string]$InstalledProgramDir,
    [Parameter(Mandatory=$true)][string]$TestDir
)
$ErrorActionPreference='Stop'
$taskTest=[IO.Path]::GetFullPath($TestDir)
if(Test-Path -LiteralPath $taskTest){throw 'Use a new directory so previous proof results remain intact.'}
[IO.Directory]::CreateDirectory($taskTest)|Out-Null
$taskVcRoot=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC'
$taskVc=(Get-ChildItem -LiteralPath $taskVcRoot -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$taskVersion=(Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory|Sort-Object Name -Descending|Select-Object -First 1).Name
$env:INCLUDE=$taskVc+'\include;'+$taskSdk+'\Include\'+$taskVersion+'\ucrt;'+$taskSdk+'\Include\'+$taskVersion+'\um;'+$taskSdk+'\Include\'+$taskVersion+'\shared'
$taskResults=[Collections.Generic.List[object]]::new()
foreach($taskArch in @('x64','x86')){
    $taskRoot=Join-Path $taskTest $taskArch
    [IO.Directory]::CreateDirectory($taskRoot)|Out-Null
    $taskClient=if($taskArch -eq 'x64'){'weaselx64.dll'}else{'weasel.dll'}
    $taskAdapter=if($taskArch -eq 'x64'){'Mason64.dll'}else{'Mason32.dll'}
    $taskAssembly=if($taskArch -eq 'x64'){'amd64'}else{'x86'}
    Copy-Item -LiteralPath (Join-Path $InstalledProgramDir $taskClient) -Destination (Join-Path $taskRoot $taskClient)
    Copy-Item -LiteralPath (Join-Path $PackageDir ('_files\native\'+$taskArch+'\Mason.dll')) -Destination (Join-Path $taskRoot $taskAdapter)
    $taskManifest=@"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity name="Mason.ActualMotionProof" version="2.4.6.0" type="win32" processorArchitecture="$taskAssembly"/>
  <file name="$taskClient"><comClass clsid="{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}" threadingModel="Apartment"/></file>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo>
</assembly>
"@
    $taskManifestPath=Join-Path $taskRoot 'private-client.manifest'
    [IO.File]::WriteAllText($taskManifestPath,$taskManifest,[Text.UTF8Encoding]::new($false))
    $env:LIB=$taskVc+'\lib\'+$taskArch+';'+$taskSdk+'\Lib\'+$taskVersion+'\ucrt\'+$taskArch+';'+$taskSdk+'\Lib\'+$taskVersion+'\um\'+$taskArch
    $taskHost=Join-Path $taskRoot 'Mason.ActualCandidateProof.exe'
    & (Join-Path $taskVc ('bin\Hostx64\'+$taskArch+'\cl.exe')) /nologo /EHsc /MT /utf-8 /std:c++17 /O2 /DUNICODE /D_UNICODE ('/Fe:'+$taskHost) ('/Fo:'+(Join-Path $taskRoot 'Mason.ActualCandidateProof.obj')) (Join-Path $PackageDir '_files\native\tests\MasonActualCandidateProof.cpp') /link /SUBSYSTEM:WINDOWS
    if($LASTEXITCODE -ne 0){throw ('Actual client proof compilation failed: '+$taskArch)}
    & (Join-Path $taskSdk ('bin\'+$taskVersion+'\x64\mt.exe')) -nologo -manifest $taskManifestPath ('-outputresource:'+$taskHost+';#1')
    if($LASTEXITCODE -ne 0){throw 'Private COM manifest embedding failed.'}
    $taskReport=Join-Path $taskRoot 'result.json'
    $taskProcess=Start-Process -FilePath $taskHost -ArgumentList ('"'+$taskReport+'"') -WindowStyle Hidden -PassThru
    try{if(-not $taskProcess.WaitForExit(15000)){throw 'Actual TSF proof timed out.'};$taskExit=$taskProcess.ExitCode}finally{$taskProcess.Dispose()}
    $taskResult=ConvertFrom-Json ([IO.File]::ReadAllText($taskReport,[Text.Encoding]::UTF8))
    if($taskExit -ne 0 -or $taskResult.status -ne 'passed'){throw ($taskArch+': '+$taskResult.error)}
    $taskResults.Add($taskResult)
}
$taskSummary=[pscustomobject]@{Status='passed';ActualTsfClients=$true;Results=$taskResults}
[IO.File]::WriteAllText((Join-Path $taskTest 'summary.json'),($taskSummary|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
$taskResults|Select-Object architecture,status,opening_observed,exit_observed,typing_did_not_reopen|Format-Table -AutoSize
