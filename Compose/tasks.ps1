param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("up", "down", "logs", "build", "save", "load", "push")]
    [string]$Command,

    [string]$EnvFile,

    [string]$ImageName,
    [string]$ImageTag,
    [string]$ImageRegistry,

    [string]$TarPath,

    [string]$RegistryUsername,
    [string]$RegistryPassword,

    [switch]$NoBuild,
    [switch]$Detach
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $scriptRoot ".env" }

function Import-DotEnv {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
        if ($trimmed.StartsWith("#")) { continue }

        $idx = $trimmed.IndexOf("=")
        if ($idx -le 0) { continue }

        $name = $trimmed.Substring(0, $idx).Trim()
        $value = $trimmed.Substring($idx + 1).Trim()

        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        $existing = $null
        try { $existing = (Get-Item -LiteralPath "Env:$name").Value } catch { }
        if ([string]::IsNullOrWhiteSpace($existing)) {
            Set-Item -LiteralPath "Env:$name" -Value $value
        }
    }
}

function Normalize-RegistryPrefix {
    param([string]$Registry)

    if ([string]::IsNullOrWhiteSpace($Registry)) { return "" }
    $reg = $Registry.Trim()
    if (-not $reg.EndsWith("/")) { $reg = "$reg/" }
    return $reg
}

function Get-ImageRef {
    param(
        [string]$RegistryPrefix,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    $prefix = Normalize-RegistryPrefix $RegistryPrefix
    return "$prefix$Name`:$Tag"
}

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Args)

    & docker @Args
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: docker $($Args -join ' ')" }
}

function Test-DockerImageExists {
    param([Parameter(Mandatory = $true)][string]$Ref)

    & docker image inspect $Ref *> $null
    return ($LASTEXITCODE -eq 0)
}

Import-DotEnv -Path $EnvFile

$resolvedImageName = if ($ImageName) { $ImageName } elseif ($env:IMAGE_NAME) { $env:IMAGE_NAME } else { "discordmusicbot" }
$resolvedImageTag = if ($ImageTag) { $ImageTag } elseif ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { "local" }
$resolvedImageRegistry = if ($ImageRegistry) { $ImageRegistry } elseif ($env:IMAGE_REGISTRY) { $env:IMAGE_REGISTRY } else { "" }

$composeFile = Join-Path $scriptRoot "docker-compose.yml"
$projectDir = $scriptRoot
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..") | Select-Object -ExpandProperty Path

$localRef = Get-ImageRef -RegistryPrefix "" -Name $resolvedImageName -Tag $resolvedImageTag
$remoteRef = Get-ImageRef -RegistryPrefix $resolvedImageRegistry -Name $resolvedImageName -Tag $resolvedImageTag

switch ($Command) {
    "up" {
        $args = @("compose", "--project-directory", $projectDir, "-f", $composeFile)
        if (Test-Path -LiteralPath $EnvFile) { $args += @("--env-file", $EnvFile) }
        $args += @("up")
        if (-not $NoBuild) { $args += @("--build") }
        if ($Detach -or -not $PSBoundParameters.ContainsKey("Detach")) { $args += @("-d") }
        Invoke-Docker -Args $args
    }
    "down" {
        $args = @("compose", "--project-directory", $projectDir, "-f", $composeFile)
        if (Test-Path -LiteralPath $EnvFile) { $args += @("--env-file", $EnvFile) }
        $args += @("down")
        Invoke-Docker -Args $args
    }
    "logs" {
        $args = @("compose", "--project-directory", $projectDir, "-f", $composeFile)
        if (Test-Path -LiteralPath $EnvFile) { $args += @("--env-file", $EnvFile) }
        $args += @("logs", "-f")
        Invoke-Docker -Args $args
    }
    "build" {
        $dockerfile = Join-Path $scriptRoot "Dockerfile"
        Invoke-Docker -Args @("build", "-f", $dockerfile, "-t", $remoteRef, $repoRoot)
        if ($remoteRef -ne $localRef) {
            Invoke-Docker -Args @("tag", $remoteRef, $localRef)
        }
    }
    "save" {
        $distDir = Join-Path $scriptRoot "dist"
        if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

        $outPath = $TarPath
        if ([string]::IsNullOrWhiteSpace($outPath)) {
            $safeName = $resolvedImageName.Replace("/", "_")
            $outPath = Join-Path $distDir "$safeName`_$resolvedImageTag.tar"
        }

        $refToSave = $remoteRef
        if (-not (Test-DockerImageExists -Ref $refToSave) -and (Test-DockerImageExists -Ref $localRef)) {
            $refToSave = $localRef
        }
        if (-not (Test-DockerImageExists -Ref $refToSave)) {
            throw "Docker image not found: $remoteRef (or $localRef). Build it first."
        }

        Invoke-Docker -Args @("save", "-o", $outPath, $refToSave)
        Write-Host $outPath
    }
    "load" {
        if ([string]::IsNullOrWhiteSpace($TarPath)) { throw "TarPath is required for 'load'." }
        Invoke-Docker -Args @("load", "-i", $TarPath)
    }
    "push" {
        if (-not [string]::IsNullOrWhiteSpace($RegistryUsername) -and -not [string]::IsNullOrWhiteSpace($RegistryPassword)) {
            $registryHost = $resolvedImageRegistry
            if ($registryHost.Contains("/")) { $registryHost = $registryHost.Split("/")[0] }
            if ([string]::IsNullOrWhiteSpace($registryHost)) { throw "ImageRegistry (or IMAGE_REGISTRY) is required when using RegistryUsername/RegistryPassword." }
            Invoke-Docker -Args @("login", $registryHost, "-u", $RegistryUsername, "-p", $RegistryPassword)
        }

        if (-not (Test-DockerImageExists -Ref $remoteRef)) {
            if (-not (Test-DockerImageExists -Ref $localRef)) {
                throw "Docker image not found: $remoteRef (or $localRef). Build it first."
            }
            if ($remoteRef -ne $localRef) {
                Invoke-Docker -Args @("tag", $localRef, $remoteRef)
            }
        }
        Invoke-Docker -Args @("push", $remoteRef)
    }
    default { throw "Unknown command: $Command" }
}
