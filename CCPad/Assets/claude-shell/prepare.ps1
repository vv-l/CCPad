param([Parameter(Mandatory=$true)][string]$CacheRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# Give only CC Pad's local Claude a private Git Bash /usr/bin. PATH wrappers
# alone miss /usr/bin/grep and find -exec. Leave the installed Git untouched.
$configured = $env:CLAUDE_CODE_GIT_BASH_PATH
if ($configured) {
    $dir = [IO.Path]::GetDirectoryName($configured)
    $gitRoot = [IO.Path]::GetDirectoryName($dir)
    if ([IO.Path]::GetFileName($gitRoot) -eq 'usr') { $gitRoot = [IO.Path]::GetDirectoryName($gitRoot) }
} else {
    $git = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $gitRoot = if ($git) { [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($git.Source)) }
    if (!$gitRoot -or ![IO.File]::Exists("$gitRoot\usr\bin\msys-2.0.dll")) {
        $gitRoot = @('HKCU:\Software\GitForWindows','HKLM:\SOFTWARE\GitForWindows') |
            ForEach-Object { (Get-ItemProperty -LiteralPath $_ -ErrorAction SilentlyContinue).InstallPath } |
            Where-Object { $_ } | Select-Object -First 1
    }
}
if (!$gitRoot -or ![IO.File]::Exists("$gitRoot\usr\bin\msys-2.0.dll")) { return }
$tools = Join-Path $gitRoot 'usr\bin'
$files = @(Get-ChildItem -LiteralPath $tools -File -Force | Sort-Object Name)
$signature = @($gitRoot) + @($files | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
$fstab = Join-Path $gitRoot 'etc\fstab'
$mounts = if ([IO.File]::Exists($fstab)) { [IO.File]::ReadAllText($fstab) } else { '' }
$signature += $mounts
foreach ($asset in 'grep','ccpad-log-commands','prepare.ps1') {
    $signature += (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $asset)).Hash
}
$hash = [Security.Cryptography.SHA256]::Create()
try { $tag = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes(($signature -join "`n")))).Replace('-','').Substring(0,20) }
finally { $hash.Dispose() }
$root = Join-Path $CacheRoot $tag
$bash = Join-Path $root 'usr\bin\bash.exe'
if (![IO.File]::Exists($bash)) {
    $stage = Join-Path $CacheRoot ('.building-' + [guid]::NewGuid().ToString('N'))
    $bin = [IO.Directory]::CreateDirectory((Join-Path $stage 'usr\bin')).FullName
    try {
        foreach ($pair in @(@("$gitRoot\usr","$stage\usr",'bin'),@($tools,$bin,''))) {
            foreach ($dir in [IO.Directory]::EnumerateDirectories($pair[0])) {
                $name = [IO.Path]::GetFileName($dir)
                if ($name -ne $pair[2]) {
                    New-Item -ItemType Junction -Path (Join-Path $pair[1] $name) -Target $dir | Out-Null
                }
            }
        }
        # Keep /, users' custom mount entries and all project files at their original
        # locations. Only the two spellings of the Git tools directory are private.
        $etc = [IO.Directory]::CreateDirectory((Join-Path $stage 'etc')).FullName
        function MountPath([string]$path) { $path.Replace('\','/').Replace(' ','\040').Replace("`t",'\011') }
        $privateBin = MountPath (Join-Path $root 'usr\bin')
        $mounts += "`n$(MountPath $gitRoot) / ntfs binary,noacl,override 0 0`n"
        $mounts += "$privateBin /usr/bin ntfs binary,noacl,override 0 0`n$privateBin /bin ntfs binary,noacl,override 0 0`n"
        [IO.File]::WriteAllText((Join-Path $etc 'fstab'),$mounts,[Text.UTF8Encoding]::new($false))
        if ([IO.Directory]::Exists("$gitRoot\etc\fstab.d")) {
            New-Item -ItemType Junction -Path (Join-Path $etc 'fstab.d') -Target "$gitRoot\etc\fstab.d" | Out-Null
        }
        foreach ($file in $files) {
            $name = if ($file.Name -eq 'grep.exe') { 'ccpad-gnu-grep.exe' } else { $file.Name }
            [IO.File]::Copy($file.FullName,(Join-Path $bin $name))
        }
        if (![IO.File]::Exists((Join-Path $bin 'ccpad-gnu-grep.exe'))) { throw 'GNU grep is missing.' }
        # Bash reads these as POSIX scripts, including when installed from a CRLF checkout.
        foreach ($name in 'grep','grep.exe','ccpad-log-commands') {
            $asset = if ($name -eq 'grep.exe') { 'grep' } else { $name }
            $script = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $asset)).Replace("`r`n","`n")
            [IO.File]::WriteAllText((Join-Path $bin $name),$script,[Text.UTF8Encoding]::new($false))
        }
        try { [IO.Directory]::Move($stage,$root) }
        catch [IO.IOException] { if (![IO.File]::Exists($bash)) { throw } }
    } finally {
        if ([IO.Directory]::Exists($stage)) {
            # Remove junctions themselves before cleaning our unique staging tree.
            if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -ne [IO.Path]::GetFullPath($CacheRoot) -or
                [IO.Path]::GetFileName($stage) -notmatch '^\.building-[a-f0-9]{32}$') { throw 'Invalid staging path.' }
            foreach ($parent in @($bin,"$stage\usr","$stage\etc",$stage)) {
                if (![IO.Directory]::Exists($parent)) { continue }
                foreach ($child in [IO.Directory]::EnumerateDirectories($parent)) {
                    if ([IO.File]::GetAttributes($child) -band [IO.FileAttributes]::ReparsePoint) {
                        [IO.Directory]::Delete($child)
                    }
                }
            }
            [IO.Directory]::Delete($stage,$true)
        }
    }
}
[Console]::WriteLine($bash)
