#!/usr/bin/env pwsh
<#
.SYNOPSIS
Converts VS1053B .plg patch files to binary .bin files for TinyCLR resource embedding.

.DESCRIPTION
Parses .plg text files containing hex arrays and generates binary .bin files
with ushort[] data (little-endian). The .bin files contain only the parsed hex values,
suitable for embedding as binary resources in the project.

.PARAMETER InputFolder
Path to the patches folder containing .plg files (default: ./patches)

.EXAMPLE
.\Convert-PlgToBinary.ps1 -InputFolder ./patches
#>

param(
	[string]$InputFolder = "./patches"
)

function Parse-PlgFile {
	param([string]$FilePath)

	$content = Get-Content -Path $FilePath -Raw

	# Find "plugin[] = {" to skip sample code
	$pluginArrayMarker = "plugin[] = {"
	$pluginStartPos = $content.IndexOf($pluginArrayMarker)

	if ($pluginStartPos -lt 0) {
		Write-Warning "Invalid .plg format in $FilePath - 'plugin[] = {' not found"
		return @()
	}

	$contentStart = $pluginStartPos + $pluginArrayMarker.Length

	# Find the closing } after the array start
	$braceCount = 1
	$contentEnd = $contentStart
	while ($contentEnd -lt $content.Length -and $braceCount -gt 0) {
		if ($content[$contentEnd] -eq '{') {
			$braceCount++
		} elseif ($content[$contentEnd] -eq '}') {
			$braceCount--
		}
		$contentEnd++
	}

	if ($braceCount -ne 0) {
		Write-Warning "Invalid .plg format in $FilePath - mismatched braces"
		return @()
	}

	# Extract array content (between plugin[] = { and matching })
	$arrayContent = $content.Substring($contentStart, $contentEnd - $contentStart - 1)

	# Parse hex values using regex (matches 0x followed by hex digits)
	$hexPattern = '0x[0-9A-Fa-f]+'
	$matches = [regex]::Matches($arrayContent, $hexPattern)

	$values = @()
	foreach ($match in $matches) {
		$hexStr = $match.Value.Substring(2)  # Remove "0x" prefix
		$value = [Convert]::ToUInt16($hexStr, 16)
		$values += $value
	}

	return $values
}

function Write-BinaryFile {
	param(
		[string]$OutputPath,
		[System.UInt16[]]$Values
	)

	$memStream = New-Object System.IO.MemoryStream
	$binaryWriter = New-Object System.IO.BinaryWriter($memStream)

	foreach ($value in $Values) {
		$binaryWriter.Write($value)
	}

	$binaryData = $memStream.ToArray()
	$binaryWriter.Close()
	$memStream.Close()

	[System.IO.File]::WriteAllBytes($OutputPath, $binaryData)
	Write-Host "  -> Generated: $OutputPath ($(($binaryData.Length / 1024)) KB, $($Values.Length) words)"
}

# Main logic
Write-Host "Converting .plg files to binary .bin files..."

if (-not (Test-Path $InputFolder)) {
	Write-Error "Input folder '$InputFolder' not found!"
	exit 1
}

# Resolve to absolute path
$InputFolderAbsolute = (Resolve-Path $InputFolder).Path
Write-Host "Using input folder: $InputFolderAbsolute"

$plgFiles = Get-ChildItem -Path $InputFolderAbsolute -Filter "*.plg" -File
if ($plgFiles.Length -eq 0) {
	Write-Error "No .plg files found in '$InputFolderAbsolute'"
	exit 1
}

foreach ($plgFile in $plgFiles) {
	$baseName = [System.IO.Path]::GetFileNameWithoutExtension($plgFile.Name)
	$outputPath = Join-Path $InputFolderAbsolute "$baseName.bin"

	Write-Host "Processing $($plgFile.Name)..."

	$values = Parse-PlgFile -FilePath $plgFile.FullName
	if ($values.Length -gt 0) {
		Write-BinaryFile -OutputPath $outputPath -Values $values
	} else {
		Write-Warning "  No hex values parsed from $($plgFile.Name)"
	}
}

Write-Host "Done! Generated .bin files are ready to be added as binary resources."
