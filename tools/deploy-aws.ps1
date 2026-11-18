<#
.SYNOPSIS
  Builds the web game and the relay server and hosts them on AWS. Prints the link to play.

.DESCRIPTION
  Two ways to host, both on one EC2 t4g.nano relay server in -Region (default ap-south-1, Mumbai):

  -Direct (works on a new AWS account, which may not create CloudFront distributions yet):
    The relay server also serves the game, behind Caddy, which gets a free Let's Encrypt
    certificate for <elastic-ip-with-dashes>.sslip.io (a free DNS name that points at the IP), so
    the link is https://13-203-5-192.sslip.io/ for example, and the game reaches the relay at the
    same address (/relay/). Godot 4 web builds need https: browsers hide features they use
    (AudioWorklet, a "secure context") on plain http pages.

  Default (https; needs an AWS account that may create CloudFront distributions):
    The game is in a private S3 bucket behind CloudFront: https://<id>.cloudfront.net/. CloudFront
    sends /relay/* to the relay server, adding a secret header the relay checks, and the relay's
    security group only lets CloudFront in.

  Resources (created once, updated in place on later runs; all tagged Project=rangbaazi):
    - S3 bucket rangbaazi-artifacts-<account>-<region>: the relay binary and (direct) the game files.
    - S3 bucket rangbaazi-web-<account>-<region> (CloudFront mode): the game, private.
    - EC2 t4g.nano "rangbaazi-relay" (Amazon Linux 2023, ARM) with an Elastic IP, security group
      "rangbaazi-relay", IAM role/instance profile rangbaazi-relay-role/-profile (read the artifacts,
      SSM so this script can configure and update it). The relay runs as the systemd service
      rangbaazi-relay; /opt/rangbaazi/update.sh fetches the latest binary and game files.
    - SSM parameter /rangbaazi/relay-origin-secret (CloudFront mode).
    - CloudFront distribution with comment "rangbaazi" (CloudFront mode).
  Cost: about 7-8 USD a month for the relay (instance, public IPv4, disk); S3, data transfer and
  CloudFront are cents at playtest scale.

  Needs: AWS CLI v2 signed in (aws login; set AWS_PROFILE for a named profile), .NET SDK,
  standard Godot 4.7.2 (not .NET) with its export templates.

.EXAMPLE
  ./tools/deploy-aws.ps1 -Direct          # http link, today
  ./tools/deploy-aws.ps1                  # https via CloudFront (account must be verified for CloudFront)
  ./tools/deploy-aws.ps1 -Direct -SkipWebBuild
#>
param(
    [string]$Region = 'ap-south-1',
    [switch]$Direct,
    [switch]$SkipWebBuild,
    [string]$Godot = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$build = Join-Path $repo 'build'
$tag = 'Key=Project,Value=rangbaazi'
$utf8 = New-Object System.Text.UTF8Encoding $false
# The AWS CLI prints server output (e.g. systemd's arrows) that the Windows console code page can't encode.
$env:PYTHONIOENCODING = 'utf-8'

function Invoke-Aws {
    # Runs the AWS CLI, returns its text output, throws on failure. Not named Aws: PowerShell names
    # ignore case, so a function called "Aws" would call itself instead of aws.exe.
    # Windows PowerShell turns any stderr line of a native program into an error, which
    # $ErrorActionPreference = 'Stop' makes fatal; judge success by the exit code instead.
    # Quote arguments with commas ('Name=x,Values=y'): unquoted, PowerShell passes them to this
    # function as arrays.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & aws.exe @args --region $Region 2>&1 }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "aws $($args -join ' ') failed:`n$(($out | ForEach-Object { "$_" }) -join "`n")" }
    return ($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
}

function Invoke-AwsJson { return (Invoke-Aws @args --output json) | ConvertFrom-Json }

function Write-Json([string]$path, $object) {
    [IO.File]::WriteAllText($path, ($object | ConvertTo-Json -Depth 30), $utf8)
    return "file://$($path -replace '\\', '/')"
}

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- account
Step 'Checking AWS sign-in'
$account = (Invoke-Aws sts get-caller-identity --query Account --output text).Trim()
Write-Host "Account $account, region $Region, $(if ($Direct) { 'direct (http)' } else { 'CloudFront (https)' })"
$webBucket = "rangbaazi-web-$account-$Region"
$artifactBucket = "rangbaazi-artifacts-$account-$Region"
New-Item -ItemType Directory -Force -Path $build | Out-Null
$tmp = Join-Path $build 'aws'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Ensure-Bucket([string]$name) {
    $exists = $true
    try { Invoke-Aws s3api head-bucket --bucket $name | Out-Null } catch { $exists = $false }
    if (-not $exists) {
        Invoke-Aws s3api create-bucket --bucket $name --create-bucket-configuration "LocationConstraint=$Region" | Out-Null
        Invoke-Aws s3api put-bucket-tagging --bucket $name --tagging 'TagSet=[{Key=Project,Value=rangbaazi}]' | Out-Null
    }
    Invoke-Aws s3api put-public-access-block --bucket $name --public-access-block-configuration `
        'BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true' | Out-Null
}

# ---------------------------------------------------------------- web build
$web = Join-Path $build 'web'
if (-not $SkipWebBuild) {
    Step 'Exporting the web build with Godot'
    if (-not $Godot) {
        $Godot = Join-Path $env:LOCALAPPDATA 'Programs\Godot-4.7.2-standard\Godot_v4.7.2-stable_win64_console.exe'
    }
    if (-not (Test-Path $Godot)) { throw "Standard Godot 4.7.2 not found at $Godot (pass -Godot <path to *_console.exe>)." }
    New-Item -ItemType Directory -Force -Path $web | Out-Null
    Get-ChildItem $web -Force | Remove-Item -Recurse -Force # empty it (the folder itself may be open elsewhere)
    & $Godot --headless --path (Join-Path $repo 'client') --import | Out-Null
    & $Godot --headless --path (Join-Path $repo 'client') --export-release 'Web' (Join-Path $web 'index.html')
}
if (-not (Test-Path (Join-Path $web 'index.wasm'))) { throw "No web build in $web (the Godot export failed?)." }

# ---------------------------------------------------------------- relay binary
Step 'Publishing the relay server (linux-arm64)'
$relayOut = Join-Path $build 'relay'
& dotnet publish (Join-Path $repo 'server\Rangbaazi.Relay') -c Release -r linux-arm64 --self-contained `
    -p:PublishSingleFile=true -o $relayOut -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of the relay failed.' }
Ensure-Bucket $artifactBucket
Invoke-Aws s3 cp (Join-Path $relayOut 'Rangbaazi.Relay') "s3://$artifactBucket/relay/Rangbaazi.Relay" | Out-Null

# ---------------------------------------------------------------- game files
if ($Direct) {
    Step 'Uploading the game for the relay server to serve'
    # gzip copies of the big files; the relay sends them to browsers that accept gzip.
    $stage = Join-Path $build 'web-direct'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Get-ChildItem $stage -Force | Remove-Item -Recurse -Force
    Copy-Item (Join-Path $web '*') $stage -Recurse
    foreach ($f in Get-ChildItem $stage -File | Where-Object { $_.Extension -in '.wasm', '.js', '.pck' }) {
        $source = [IO.File]::OpenRead($f.FullName)
        $target = [IO.File]::Create("$($f.FullName).gz")
        $zip = New-Object IO.Compression.GZipStream($target, [IO.Compression.CompressionLevel]::Optimal)
        try { $source.CopyTo($zip) } finally { $zip.Dispose(); $target.Dispose(); $source.Dispose() }
    }
    Invoke-Aws s3 sync $stage "s3://$artifactBucket/web/" --delete | Out-Null
}
else {
    Step "Uploading the game to s3://$webBucket"
    Ensure-Bucket $webBucket
    Invoke-Aws s3 sync $web "s3://$webBucket/" --delete --cache-control 'public, max-age=300' --exclude '*.wasm' --exclude '*.pck' | Out-Null
    foreach ($f in Get-ChildItem $web -File | Where-Object { $_.Extension -in '.wasm', '.pck' }) {
        $type = if ($f.Extension -eq '.wasm') { 'application/wasm' } else { 'application/octet-stream' }
        Invoke-Aws s3 cp $f.FullName "s3://$webBucket/$($f.Name)" --content-type $type --cache-control 'public, max-age=300' | Out-Null
    }
    Invoke-Aws s3 cp (Join-Path $web 'index.html') "s3://$webBucket/index.html" --content-type 'text/html; charset=utf-8' --cache-control 'no-cache' | Out-Null
}

# ---------------------------------------------------------------- origin secret (CloudFront mode)
$secret = $null
if (-not $Direct) {
    try {
        $secret = (Invoke-Aws ssm get-parameter --name /rangbaazi/relay-origin-secret --query Parameter.Value --output text).Trim()
    }
    catch {
        $bytes = New-Object byte[] 24
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $secret = ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x')
        Invoke-Aws ssm put-parameter --name /rangbaazi/relay-origin-secret --type String --value $secret --tags $tag | Out-Null
    }
}

# ---------------------------------------------------------------- IAM
$roleExists = $true
try { Invoke-Aws iam get-role --role-name rangbaazi-relay-role | Out-Null } catch { $roleExists = $false }
if (-not $roleExists) {
    Step 'Creating the relay server role'
    $trust = Write-Json (Join-Path $tmp 'trust.json') @{
        Version = '2012-10-17'
        Statement = @(@{ Effect = 'Allow'; Principal = @{ Service = 'ec2.amazonaws.com' }; Action = 'sts:AssumeRole' })
    }
    Invoke-Aws iam create-role --role-name rangbaazi-relay-role --assume-role-policy-document $trust --tags $tag | Out-Null
    Invoke-Aws iam attach-role-policy --role-name rangbaazi-relay-role --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore | Out-Null
    Invoke-Aws iam create-instance-profile --instance-profile-name rangbaazi-relay-profile | Out-Null
    Invoke-Aws iam add-role-to-instance-profile --instance-profile-name rangbaazi-relay-profile --role-name rangbaazi-relay-role | Out-Null
    Start-Sleep -Seconds 10 # a new instance profile takes a moment to be usable
}
$policy = Write-Json (Join-Path $tmp 'relay-policy.json') @{
    Version = '2012-10-17'
    Statement = @(
        @{ Effect = 'Allow'; Action = 's3:GetObject'; Resource = "arn:aws:s3:::$artifactBucket/*" },
        @{ Effect = 'Allow'; Action = 's3:ListBucket'; Resource = "arn:aws:s3:::$artifactBucket" }
    )
}
Invoke-Aws iam put-role-policy --role-name rangbaazi-relay-role --policy-name read-relay-binary --policy-document $policy | Out-Null

# ---------------------------------------------------------------- network
$vpc = (Invoke-Aws ec2 describe-vpcs --filters 'Name=is-default,Values=true' --query 'Vpcs[0].VpcId' --output text).Trim()
if ($vpc -eq 'None' -or -not $vpc) { throw 'No default VPC in this region; create one (aws ec2 create-default-vpc) and run again.' }
$sg = (Invoke-Aws ec2 describe-security-groups --filters 'Name=group-name,Values=rangbaazi-relay' "Name=vpc-id,Values=$vpc" --query 'SecurityGroups[0].GroupId' --output text).Trim()
if ($sg -eq 'None' -or -not $sg) {
    $sg = (Invoke-Aws ec2 create-security-group --group-name rangbaazi-relay --description 'RANGBAAZI relay' --vpc-id $vpc --query GroupId --output text).Trim()
}
function Allow-Port80([string]$permission) {
    try { Invoke-Aws ec2 authorize-security-group-ingress --group-id $sg --ip-permissions $permission | Out-Null }
    catch { if ("$_" -notmatch 'InvalidPermission.Duplicate') { throw } }
}
if ($Direct) {
    # 80 for Let's Encrypt's challenge and the redirect to https, 443 for the game.
    Allow-Port80 'IpProtocol=tcp,FromPort=80,ToPort=80,IpRanges=[{CidrIp=0.0.0.0/0,Description=players}]'
    Allow-Port80 'IpProtocol=tcp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0,Description=players}]'
}
else {
    $prefixList = (Invoke-Aws ec2 describe-managed-prefix-lists --filters 'Name=prefix-list-name,Values=com.amazonaws.global.cloudfront.origin-facing' --query 'PrefixLists[0].PrefixListId' --output text).Trim()
    Allow-Port80 "IpProtocol=tcp,FromPort=80,ToPort=80,PrefixListIds=[{PrefixListId=$prefixList,Description=CloudFront}]"
}

# ---------------------------------------------------------------- instance
$instance = $null
$found = Invoke-AwsJson ec2 describe-instances --filters 'Name=tag:Name,Values=rangbaazi-relay' 'Name=instance-state-name,Values=pending,running,stopping,stopped'
if ($found.Reservations.Count -gt 0) { $instance = $found.Reservations[0].Instances[0] }
if ($instance -eq $null) {
    Step 'Creating the relay server (EC2 t4g.nano)'
    $ami = (Invoke-Aws ssm get-parameter --name /aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-arm64 --query Parameter.Value --output text).Trim()
    $launched = Invoke-AwsJson ec2 run-instances --image-id $ami --instance-type t4g.nano --count 1 `
        --iam-instance-profile Name=rangbaazi-relay-profile --security-group-ids $sg `
        --metadata-options 'HttpTokens=required,HttpEndpoint=enabled' `
        --block-device-mappings 'DeviceName=/dev/xvda,Ebs={VolumeSize=8,VolumeType=gp3,Encrypted=true}' `
        --tag-specifications 'ResourceType=instance,Tags=[{Key=Name,Value=rangbaazi-relay},{Key=Project,Value=rangbaazi}]'
    $instanceId = $launched.Instances[0].InstanceId
}
else {
    $instanceId = $instance.InstanceId
    if ($instance.State.Name -ne 'running') { Invoke-Aws ec2 start-instances --instance-ids $instanceId | Out-Null }
}
Invoke-Aws ec2 wait instance-running --instance-ids $instanceId | Out-Null

$eipIp = (Invoke-Aws ec2 describe-addresses --filters "Name=instance-id,Values=$instanceId" --query 'Addresses[0].PublicIp' --output text).Trim()
if ($eipIp -eq 'None' -or -not $eipIp) {
    $eip = Invoke-AwsJson ec2 allocate-address --domain vpc --tag-specifications 'ResourceType=elastic-ip,Tags=[{Key=Project,Value=rangbaazi}]'
    Invoke-Aws ec2 associate-address --instance-id $instanceId --allocation-id $eip.AllocationId | Out-Null
    $eipIp = $eip.PublicIp
}
# Built from the Elastic IP: describe-instances can briefly return the DNS name from before it was attached.
$suffix = if ($Region -eq 'us-east-1') { 'compute-1.amazonaws.com' } else { "$Region.compute.amazonaws.com" }
$relayDns = "ec2-$($eipIp -replace '\.', '-').$suffix"
$siteHost = "$($eipIp -replace '\.', '-').sslip.io"   # direct mode's https name

# ---------------------------------------------------------------- configure the server (SSM)
Step 'Configuring the relay server'
$deadline = (Get-Date).AddMinutes(5)
while ((Invoke-Aws ssm describe-instance-information --filters "Key=InstanceIds,Values=$instanceId" --query 'InstanceInformationList[0].PingStatus' --output text).Trim() -ne 'Online') {
    if ((Get-Date) -gt $deadline) { throw "The relay server didn't register with SSM within 5 minutes." }
    Start-Sleep -Seconds 10
}
if ($Direct) {
    # The relay serves the game too, on 8080 behind Caddy (8080 isn't open to the internet).
    $environment = @('Environment=PORT=8080', 'Environment=WEB_ROOT=/opt/rangbaazi/web')
    $healthCheck = 'curl -sf -o /dev/null http://127.0.0.1:8080/ && curl -sf http://127.0.0.1:8080/relay/health'
    $front = @"
if ! /usr/local/bin/caddy version >/dev/null 2>&1; then
  V=`$(curl -fsSL https://api.github.com/repos/caddyserver/caddy/releases/latest | sed -n 's/.*"tag_name": *"v\([^"]*\)".*/\1/p' | head -1)
  V=`${V:-2.11.9}
  cd /tmp
  curl -fsSLO https://github.com/caddyserver/caddy/releases/download/v`$V/caddy_`${V}_linux_arm64.tar.gz
  curl -fsSLO https://github.com/caddyserver/caddy/releases/download/v`$V/caddy_`${V}_checksums.txt
  grep " caddy_`${V}_linux_arm64.tar.gz`$" caddy_`${V}_checksums.txt | sha512sum -c -
  tar -xzf caddy_`${V}_linux_arm64.tar.gz caddy
  install -m 755 caddy /usr/local/bin/caddy
fi
id caddy >/dev/null 2>&1 || useradd --system --home-dir /var/lib/caddy --shell /sbin/nologin caddy
mkdir -p /etc/caddy
cat > /etc/caddy/Caddyfile <<'EOS'
$siteHost {
	reverse_proxy 127.0.0.1:8080
}
http:// {
	redir https://$siteHost{uri} permanent
}
EOS
cat > /etc/systemd/system/caddy.service <<'EOS'
[Unit]
Description=Caddy (https in front of the RANGBAAZI relay)
After=network-online.target rangbaazi-relay.service
Wants=network-online.target
StartLimitIntervalSec=0
[Service]
User=caddy
Group=caddy
Environment=XDG_DATA_HOME=/var/lib
Environment=XDG_CONFIG_HOME=/var/lib
StateDirectory=caddy
ExecStart=/usr/local/bin/caddy run --config /etc/caddy/Caddyfile
ExecReload=/usr/local/bin/caddy reload --config /etc/caddy/Caddyfile
Restart=on-failure
RestartSec=3
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
PrivateTmp=true
LimitNOFILE=65536
[Install]
WantedBy=multi-user.target
EOS
systemctl daemon-reload
systemctl enable caddy
systemctl restart caddy
"@
}
else {
    $environment = @('Environment=PORT=80', "Environment=RELAY_ORIGIN_SECRET=$secret")
    $healthCheck = "curl -sf -H 'X-Origin-Secret: $secret' http://127.0.0.1/relay/health"
    $front = 'systemctl disable --now caddy >/dev/null 2>&1 || true'
}
$script = @"
set -euo pipefail
id rangbaazi >/dev/null 2>&1 || useradd --system --shell /sbin/nologin rangbaazi
mkdir -p /opt/rangbaazi/web
cat > /opt/rangbaazi/update.sh <<'EOS'
#!/bin/bash
set -euo pipefail
aws s3 cp s3://$artifactBucket/relay/Rangbaazi.Relay /opt/rangbaazi/Rangbaazi.Relay.new --region $Region --only-show-errors
chmod 755 /opt/rangbaazi/Rangbaazi.Relay.new
mv -f /opt/rangbaazi/Rangbaazi.Relay.new /opt/rangbaazi/Rangbaazi.Relay
# --exact-timestamps: without it, sync skips a changed file that kept its size (a renamed fighter).
aws s3 sync s3://$artifactBucket/web/ /opt/rangbaazi/web/ --delete --exact-timestamps --region $Region --only-show-errors
chmod -R a+rX /opt/rangbaazi/web
systemctl restart rangbaazi-relay
EOS
chmod 755 /opt/rangbaazi/update.sh
cat > /etc/systemd/system/rangbaazi-relay.service <<'EOS'
[Unit]
Description=RANGBAAZI relay
After=network-online.target
Wants=network-online.target
[Service]
User=rangbaazi
$($environment -join "`n")
Environment=DOTNET_gcServer=0
ExecStart=/opt/rangbaazi/Rangbaazi.Relay
Restart=always
RestartSec=2
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
[Install]
WantedBy=multi-user.target
EOS
systemctl daemon-reload
systemctl enable rangbaazi-relay
/opt/rangbaazi/update.sh
$front
for i in 1 2 3 4 5 6 7 8 9 10; do if $healthCheck >/dev/null 2>&1; then echo relay-ok; exit 0; fi; sleep 1; done
journalctl -u rangbaazi-relay -n 20 --no-pager
exit 1
"@
$commands = Write-Json (Join-Path $tmp 'configure.json') @{ commands = @(($script -replace "`r`n", "`n") -split "`n") }
$commandId = (Invoke-Aws ssm send-command --instance-ids $instanceId --document-name AWS-RunShellScript `
        --comment 'rangbaazi relay configure' --parameters $commands --query Command.CommandId --output text).Trim()
$status = 'Pending'
while ($status -in 'Pending', 'InProgress', 'Delayed') {
    Start-Sleep -Seconds 3
    try { $status = (Invoke-Aws ssm get-command-invocation --command-id $commandId --instance-id $instanceId --query Status --output text).Trim() }
    catch { if ("$_" -notmatch 'InvocationDoesNotExist') { throw } }
}
$result = Invoke-AwsJson ssm get-command-invocation --command-id $commandId --instance-id $instanceId
if ($status -ne 'Success') { throw "Configuring the relay server failed ($status):`n$($result.StandardOutputContent)`n$($result.StandardErrorContent)" }

# ---------------------------------------------------------------- direct: done
if ($Direct) {
    Step "Waiting for the https certificate for $siteHost"
    $deadline = (Get-Date).AddMinutes(3)
    $ok = $false
    while (-not $ok -and (Get-Date) -lt $deadline) {
        try { $ok = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 "https://$siteHost/relay/health").Content -eq 'ok' } catch { Start-Sleep -Seconds 5 }
    }
    if (-not $ok) { Write-Warning "https://$siteHost/ isn't answering yet; Caddy may still be getting its certificate (check: journalctl -u caddy)." }
    # Android and desktop builds have no page address to derive the relay from; give them this one.
    [IO.File]::WriteAllText((Join-Path $repo 'client\net\relay_base_url.txt'), "wss://$siteHost`n", $utf8)
    Write-Host ''
    Write-Host "Play: https://$siteHost/" -ForegroundColor Green
    Write-Host "Online room link: https://$siteHost/?room=<CODE>"
    Write-Host "Relay health: https://$siteHost/relay/health"
    return
}

# ---------------------------------------------------------------- CloudFront
$cachingOptimized = '658327ea-f89d-4fab-a63d-7e88639e58f6'
$cachingDisabled = '4135ea2d-6df8-44a3-9df3-4b5a84be39ad'
$allViewerExceptHost = 'b689b0a8-53d0-40ab-baf2-68738e2966ac'

$dists = Invoke-AwsJson cloudfront list-distributions
$dist = $null
if ($dists.DistributionList.Items) { $dist = $dists.DistributionList.Items | Where-Object { $_.Comment -eq 'rangbaazi' } | Select-Object -First 1 }

if ($dist -eq $null) {
    Step 'Creating the CloudFront distribution (takes 5-15 minutes to deploy)'
    $oacs = Invoke-AwsJson cloudfront list-origin-access-controls
    $oac = $null
    if ($oacs.OriginAccessControlList.Items) { $oac = $oacs.OriginAccessControlList.Items | Where-Object { $_.Name -eq 'rangbaazi-web' } | Select-Object -First 1 }
    if ($oac -eq $null) {
        $oacConfig = Write-Json (Join-Path $tmp 'oac.json') @{
            Name = 'rangbaazi-web'; Description = 'RANGBAAZI web bucket'; SigningProtocol = 'sigv4'; SigningBehavior = 'always'; OriginAccessControlOriginType = 's3'
        }
        $oac = (Invoke-AwsJson cloudfront create-origin-access-control --origin-access-control-config $oacConfig).OriginAccessControl
    }
    $origins = @(
        @{
            Id = 'web'; DomainName = "$webBucket.s3.$Region.amazonaws.com"; OriginPath = ''
            CustomHeaders = @{ Quantity = 0 }
            S3OriginConfig = @{ OriginAccessIdentity = '' }
            OriginAccessControlId = $oac.Id
        },
        @{
            Id = 'relay'; DomainName = $relayDns; OriginPath = ''
            CustomHeaders = @{ Quantity = 1; Items = @(@{ HeaderName = 'X-Origin-Secret'; HeaderValue = $secret }) }
            CustomOriginConfig = @{
                HTTPPort = 80; HTTPSPort = 443; OriginProtocolPolicy = 'http-only'
                OriginSslProtocols = @{ Quantity = 1; Items = @('TLSv1.2') }
                OriginReadTimeout = 60; OriginKeepaliveTimeout = 60
            }
        }
    )
    $config = @{
        CallerReference = "rangbaazi-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
        Comment = 'rangbaazi'
        Enabled = $true
        DefaultRootObject = 'index.html'
        PriceClass = 'PriceClass_200'
        HttpVersion = 'http2and3'
        Origins = @{ Quantity = 2; Items = $origins }
        DefaultCacheBehavior = @{
            TargetOriginId = 'web'; ViewerProtocolPolicy = 'redirect-to-https'
            AllowedMethods = @{ Quantity = 2; Items = @('GET', 'HEAD'); CachedMethods = @{ Quantity = 2; Items = @('GET', 'HEAD') } }
            CachePolicyId = $cachingOptimized; Compress = $true
        }
        CacheBehaviors = @{
            Quantity = 1
            Items = @(@{
                PathPattern = '/relay/*'; TargetOriginId = 'relay'; ViewerProtocolPolicy = 'https-only'
                AllowedMethods = @{ Quantity = 2; Items = @('GET', 'HEAD'); CachedMethods = @{ Quantity = 2; Items = @('GET', 'HEAD') } }
                CachePolicyId = $cachingDisabled; OriginRequestPolicyId = $allViewerExceptHost; Compress = $false
            })
        }
    }
    # Through a file: Windows PowerShell mangles double quotes in arguments to native programs.
    $withTags = Write-Json (Join-Path $tmp 'distribution.json') @{
        DistributionConfig = $config
        Tags = @{ Items = @(@{ Key = 'Project'; Value = 'rangbaazi' }) }
    }
    $dist = (Invoke-AwsJson cloudfront create-distribution-with-tags --distribution-config-with-tags $withTags).Distribution
}
else {
    Step "Invalidating the CloudFront cache of $($dist.DomainName)"
    Invoke-Aws cloudfront create-invalidation --distribution-id $dist.Id --paths '/*' | Out-Null
}
$domain = $dist.DomainName

# Only this distribution may read the web bucket.
$bucketPolicy = Write-Json (Join-Path $tmp 'bucket-policy.json') @{
    Version = '2012-10-17'
    Statement = @(@{
        Sid = 'CloudFrontRead'; Effect = 'Allow'; Principal = @{ Service = 'cloudfront.amazonaws.com' }
        Action = 's3:GetObject'; Resource = "arn:aws:s3:::$webBucket/*"
        Condition = @{ StringEquals = @{ 'AWS:SourceArn' = $dist.ARN } }
    })
}
Invoke-Aws s3api put-bucket-policy --bucket $webBucket --policy $bucketPolicy | Out-Null

# Android and desktop builds have no page address to derive the relay from; give them this one.
[IO.File]::WriteAllText((Join-Path $repo 'client\net\relay_base_url.txt'), "wss://$domain`n", $utf8)

Step 'Waiting for CloudFront to finish deploying'
Invoke-Aws cloudfront wait distribution-deployed --id $dist.Id | Out-Null

Write-Host ''
Write-Host "Play: https://$domain/" -ForegroundColor Green
Write-Host "Online room link: https://$domain/?room=<CODE>"
Write-Host "Relay health: https://$domain/relay/health"
