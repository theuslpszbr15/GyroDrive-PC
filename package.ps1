$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dist=Join-Path $root 'dist'
$runtime=Join-Path $root 'build\release-runtime'
$source=Join-Path $root 'build\release-source'
$binaries=@('GyroDrive.exe','JoyShockLibrary.dll','ViGEmClient.dll')
$sources=@('App.cs','Controller.cs','Steering.cs','VirtualPad.cs','build.ps1','package.ps1','.gitignore','README.md')
foreach($name in $binaries+$sources){if(-not(Test-Path (Join-Path $root $name))){throw "Missing input: $name"}}
$executable=Get-Item "$root\GyroDrive.exe"
foreach($name in @('App.cs','Controller.cs','Steering.cs','VirtualPad.cs')){if((Get-Item (Join-Path $root $name)).LastWriteTimeUtc -gt $executable.LastWriteTimeUtc){throw "Rebuild before packaging: $name"}}
if(-not(Test-Path "$root\licenses\JoyShockLibrary.md")){
    $license=Join-Path $root 'vendor\JoyShockLibrary\JSL\LICENSE.md'
    if(-not(Test-Path $license)){throw 'Missing official JoyShockLibrary license'}
    Copy-Item $license "$root\licenses\JoyShockLibrary.md"
}
if(-not(Test-Path "$root\docs\preview.png")){
    [IO.Directory]::CreateDirectory("$root\docs")|Out-Null
    Copy-Item "$root\preview-repair.png" "$root\docs\preview.png"
}
foreach($directory in @($runtime,$source)){
    if(Test-Path $directory){Remove-Item $directory -Recurse -Force}
    [IO.Directory]::CreateDirectory($directory)|Out-Null
}
[IO.Directory]::CreateDirectory($dist)|Out-Null
foreach($name in $binaries+@('README.md')){Copy-Item (Join-Path $root $name) $runtime}
Copy-Item "$root\licenses","$root\docs" $runtime -Recurse
foreach($name in $sources){Copy-Item (Join-Path $root $name) $source}
if(Test-Path "$root\publish.cjs"){Copy-Item "$root\publish.cjs" $source}
Copy-Item "$root\licenses","$root\docs" $source -Recurse
$runtimeZip=Join-Path $dist 'GyroDrive-PC-0.1.1-beta.zip'
$sourceZip=Join-Path $dist 'GyroDrive-PC-Source-0.1.1-beta.zip'
Compress-Archive "$runtime\*" $runtimeZip -Force
Compress-Archive "$source\*" $sourceZip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach($file in @($runtimeZip,$sourceZip)){
    $archive=[IO.Compression.ZipFile]::OpenRead($file)
    try{
        $names=@($archive.Entries|ForEach-Object{$_.FullName.Replace('\','/')})
        if($names|Where-Object{$_ -match '(^|/)(vendor|build|dist)/|\.(log|keystore|jks)$|(^|/)settings\.txt$'}){throw 'Unwanted file in ZIP'}
        $required=if($file -eq $runtimeZip){$binaries+@('README.md','licenses/JoyShockLibrary.md','licenses/ViGEmClient.txt','docs/preview.png')}else{$sources+@('licenses/JoyShockLibrary.md','licenses/ViGEmClient.txt','docs/preview.png')}
        foreach($name in $required){if($names -notcontains $name){throw "Missing ZIP entry: $name"}}
        if($file -eq $sourceZip -and ($names|Where-Object{$_ -match '\.(exe|dll|zip)$'})){throw 'Binary in source ZIP'}
        if($file -eq $runtimeZip){
            foreach($name in $binaries){
                $stream=$archive.GetEntry($name).Open()
                $hash=[Security.Cryptography.SHA256]::Create()
                try{$zipped=[BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','');if($zipped -ne (Get-FileHash (Join-Path $root $name) -Algorithm SHA256).Hash){throw "ZIP binary differs: $name"}}
                finally{$hash.Dispose();$stream.Dispose()}
            }
        }
    }finally{$archive.Dispose()}
}
$hashes=foreach($file in @($runtimeZip,$sourceZip)){(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($file)}
[IO.File]::WriteAllLines("$dist\SHA256SUMS.txt",[string[]]$hashes,[Text.Encoding]::ASCII)
Write-Output 'PASS: PC package contents, current compiled binary, DLL integrity, licenses and private-file exclusions.'
Get-Item $runtimeZip,$sourceZip,"$dist\SHA256SUMS.txt"|Select-Object Name,Length|Format-Table -AutoSize|Out-String