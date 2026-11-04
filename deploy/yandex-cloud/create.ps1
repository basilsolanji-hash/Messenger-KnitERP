# Creates the closed stand's network, security group, static IP and VM in Yandex Cloud (steps Я-3..Я-5 of README.md).
# Run on the operator's Windows computer AFTER `yc init`. Prints no tokens or keys. Safe to re-run: existing
# resources with the same names are reused, nothing is deleted.
#   powershell -ExecutionPolicy Bypass -File .\create.ps1            # shows the plan and asks before paid resources
param(
    [string]$Zone = 'ru-central1-a',
    [string]$SshPublicKey = "$HOME\.ssh\os_vps.pub",
    [int]$Cores = 2, [int]$MemoryGb = 2, [int]$DiskGb = 20
)
$ErrorActionPreference = 'Stop'
function Yc { $out = & yc @args --format json 2>&1; if ($LASTEXITCODE) { throw "yc $($args -join ' '): $out" }; if ($out) { $out | ConvertFrom-Json } }
function Find($kind, $name) { (Yc vpc $kind list) | Where-Object { $_.name -eq $name } | Select-Object -First 1 }

if (-not (Test-Path $SshPublicKey)) { throw "SSH public key not found: $SshPublicKey" }
$operatorIp = (curl.exe -s https://api.ipify.org).Trim()
if ($operatorIp -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw "Could not read this computer's external IPv4 ($operatorIp)" }
$folder = (& yc config get folder-id).Trim()
Write-Host "Folder: $folder  Zone: $Zone  Operator IP for SSH: $operatorIp"
Write-Host "VM: standard-v3, $Cores vCPU 100%, $MemoryGb GB RAM, $DiskGb GB network-ssd, Ubuntu 24.04, static public IPv4."
Write-Host "These resources are PAID while they exist. Check the price in the Yandex Cloud calculator."
if ((Read-Host 'Type YES to create them') -ne 'YES') { Write-Host 'Cancelled, nothing created.'; exit 1 }

$net = Find network os-chat-net
if (-not $net) { $net = Yc vpc network create --name os-chat-net }
$subnet = Find subnet os-chat-a
if (-not $subnet) { $subnet = Yc vpc subnet create --name os-chat-a --zone $Zone --range 10.130.0.0/24 --network-name os-chat-net }
$sg = (Yc vpc security-group list) | Where-Object { $_.name -eq 'os-chat-sg' } | Select-Object -First 1
if (-not $sg) {
    $sg = Yc vpc security-group create --name os-chat-sg --network-name os-chat-net `
        --rule "description=https,direction=ingress,port=443,protocol=tcp,v4-cidrs=[0.0.0.0/0]" `
        --rule "description=ssh-operator,direction=ingress,port=22,protocol=tcp,v4-cidrs=[$operatorIp/32]" `
        --rule "description=wireguard,direction=ingress,port=51820,protocol=udp,v4-cidrs=[0.0.0.0/0]" `
        --rule "description=egress,direction=egress,protocol=any,v4-cidrs=[0.0.0.0/0]"
}
$addr = (Yc vpc address list) | Where-Object { $_.name -eq 'os-chat-ip' } | Select-Object -First 1
if (-not $addr) { $addr = Yc vpc address create --name os-chat-ip --external-ipv4 "zone=$Zone" }
$ip = $addr.external_ipv4_address.address

$vm = (Yc compute instance list) | Where-Object { $_.name -eq 'os-chat' } | Select-Object -First 1
if (-not $vm) {
    $vm = Yc compute instance create --name os-chat --zone $Zone --platform standard-v3 `
        --cores $Cores --core-fraction 100 --memory $MemoryGb `
        --create-boot-disk "image-folder-id=standard-images,image-family=ubuntu-2404-lts,size=$DiskGb,type=network-ssd" `
        --network-interface "subnet-name=os-chat-a,nat-ip-version=ipv4,nat-address=$ip,security-group-ids=$($sg.id)" `
        --ssh-key $SshPublicKey
}
Write-Host ""
Write-Host "CREATE_OK vm=$($vm.name) status=$($vm.status) ip=$ip security_group=$($sg.id) ssh_from=$operatorIp/32"
Write-Host "Next: ssh -i `"$HOME\.ssh\os_vps`" yc-user@$ip   (then run setup-vm.sh, README step Я-6..Я-8)"
