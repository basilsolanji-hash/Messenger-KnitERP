# Moves the backup TLS private key (second pin) off the VPS to this computer, checks the copy and erases it on the server.
#   powershell -ExecutionPolicy Bypass -File .\move-backup-key.ps1
param(
    [string]$Server = 'root@155.212.217.23',
    [string]$SshKey = "$HOME\.ssh\os_vps",
    [string]$Target = "$HOME\.ssh\os-communications-backup-tls.key"
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Target) { throw "$Target already exists; nothing was overwritten." }
& scp -i $SshKey "${Server}:/root/backup-tls.key" $Target
if ($LASTEXITCODE) { throw 'Copy failed; the key stays on the server.' }
$hash = (Get-FileHash $Target -Algorithm SHA256).Hash.ToLower()
& ssh -i $SshKey $Server "echo '$hash  /root/backup-tls.key' | sha256sum -c --quiet && shred -u /root/backup-tls.key && echo BACKUP_KEY_MOVED"
if ($LASTEXITCODE) { throw 'Checksum mismatch or erase failed; the server copy was kept.' }
"Saved to $Target. Copy it to a USB drive kept apart from this computer; never send it to anyone."
