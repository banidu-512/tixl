# DepthAnything V2 Model Download Script
# Downloads the required ONNX models for the DepthAnything operator

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "DepthAnything V2 Model Download Script" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$AssetsDir = Join-Path $PSScriptRoot "..\Assets"
$BaseUrl = "https://huggingface.co/onnx-community"

# Models to download. model_fp16.onnx is the real fp16 export - model.onnx is
# fp32 despite the half-size naming in older revisions of this script.
# ExpectedMb guards against the earlier mislabeled fp32 downloads.
$Models = @(
    @{Name="depth-anything-v2-small-fp16.onnx"; Url="$BaseUrl/depth-anything-v2-small/resolve/main/onnx/model_fp16.onnx"; ExpectedMb=50; Required=$true},
    @{Name="depth-anything-v2-base-fp16.onnx"; Url="$BaseUrl/depth-anything-v2-base/resolve/main/onnx/model_fp16.onnx"; ExpectedMb=194; Required=$false},
    @{Name="depth-anything-v2-large-fp16.onnx"; Url="$BaseUrl/depth-anything-v2-large/resolve/main/onnx/model_fp16.onnx"; ExpectedMb=672; Required=$false}
)

# Create Assets directory
if (-not (Test-Path $AssetsDir)) {
    New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null
    Write-Host "Created Assets directory: $AssetsDir" -ForegroundColor Green
}

Write-Host "Target directory: $AssetsDir" -ForegroundColor Yellow
Write-Host ""

# Download function
function Download-Model {
    param(
        [string]$Name,
        [string]$Url,
        [double]$ExpectedMb,
        [bool]$Required
    )

    $OutputPath = Join-Path $AssetsDir $Name

    if (Test-Path $OutputPath) {
        $SizeMb = (Get-Item $OutputPath).Length / 1MB
        # fp16 files are roughly half their fp32 counterparts - a size far off
        # the expectation means a mislabeled download; replace it
        if ([math]::Abs($SizeMb - $ExpectedMb) / $ExpectedMb -lt 0.25) {
            Write-Host "✓ $Name already exists (skipping)" -ForegroundColor Gray
            return $true
        }
        Write-Host "! $Name exists with unexpected size $([math]::Round($SizeMb, 1)) MB (expected ~$ExpectedMb MB) - re-downloading" -ForegroundColor Yellow
    }

    Write-Host "Downloading: $Name (~$ExpectedMb MB)..." -ForegroundColor Yellow

    try {
        # Use Invoke-WebRequest with progress tracking
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $OutputPath -UseBasicParsing

        $FileSize = (Get-Item $OutputPath).Length / 1MB
        Write-Host "✓ Downloaded: $Name ($([math]::Round($FileSize, 2)) MB)" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "✗ Failed to download: $Name" -ForegroundColor Red
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red

        if (-not $Required) {
            Write-Host "  Note: This model is optional. The operator will work without it." -ForegroundColor Yellow
        }

        # Remove partial download
        if (Test-Path $OutputPath) {
            Remove-Item $OutputPath -Force
        }

        return $false
    }
}

# Download models
$SuccessCount = 0
$FailedCount = 0

Write-Host "Starting downloads..." -ForegroundColor Cyan
Write-Host ""

foreach ($Model in $Models) {
    if (Download-Model -Name $Model.Name -Url $Model.Url -ExpectedMb $Model.ExpectedMb -Required $Model.Required) {
        $SuccessCount++
    } else {
        $FailedCount++
    }
    Write-Host ""
}

# Summary
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "Download Summary" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "Successful: $SuccessCount" -ForegroundColor Green
$FailColor = if ($FailedCount -eq 0) { "Green" } else { "Red" }
Write-Host "Failed: $FailedCount" -ForegroundColor $FailColor
Write-Host ""

# Check required models
$RequiredModels = $Models | Where-Object { $_.Required -eq $true }
$AllRequiredPresent = $true

foreach ($Model in $RequiredModels) {
    $ModelPath = Join-Path $AssetsDir $Model.Name
    if (-not (Test-Path $ModelPath)) {
        Write-Host "✗ Missing required model: $($Model.Name)" -ForegroundColor Red
        $AllRequiredPresent = $false
    }
}

if ($AllRequiredPresent) {
    Write-Host "All required models are present!" -ForegroundColor Green
    Write-Host ""
    Write-Host "You can now build and use the DepthAnything operator." -ForegroundColor Cyan
    Write-Host ""
    exit 0
} else {
    Write-Host ""
    Write-Host "Warning: Some required models are missing." -ForegroundColor Yellow
    Write-Host "Please check the errors above and try again." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Alternative: Download manually from:" -ForegroundColor Cyan
    Write-Host "https://huggingface.co/onnx-community" -ForegroundColor Cyan
    Write-Host ""
    exit 1
}
