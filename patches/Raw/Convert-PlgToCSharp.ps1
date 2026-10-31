#!/usr/bin/env pwsh
<#
.SYNOPSIS
Converts VS1053B .plg patch files to C# ushort[] arrays for TinyCLR embedding.

.DESCRIPTION
Parses .plg text files containing hex arrays and generates a single PatchData.cs
file with static ushort[] arrays for each patch.

.PARAMETER InputFolder
Path to the patches folder containing .plg files (default: ./patches)

.PARAMETER OutputFile
Path to the generated PatchData.cs file (default: ./PatchData.cs)

.EXAMPLE
.\Convert-PlgToCSharp.ps1 -InputFolder ./patches -OutputFile ./PatchData.cs
#>

param(
	[string]$InputFolder = "./patches",
	[string]$OutputFile = "./PatchData.cs"
)

function Parse-PlgFile {
	param([string]$FilePath)

	$content = Get-Content -Path $FilePath -Raw

	# Find array boundaries
	$startIdx = $content.IndexOf('{')
	$endIdx = $content.LastIndexOf('}')

	if ($startIdx -lt 0 -or $endIdx -lt 0) {
		Write-Warning "Invalid .plg format in $FilePath - no array boundaries found"
		return @()
	}

	# Extract array content
	$arrayContent = $content.Substring($startIdx + 1, $endIdx - $startIdx - 1)

	# Parse hex values using regex
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

function Generate-CSharpArray {
	param(
		[string]$ArrayName,
		[ushort[]]$Values
	)

	if ($Values.Length -eq 0) {
		return ""
	}

	$sb = New-Object System.Text.StringBuilder
	$sb.AppendLine("        /// <summary>") | Out-Null
	$sb.AppendLine("        /// Parsed patch data from $ArrayName") | Out-Null
	$sb.AppendLine("        /// </summary>") | Out-Null
	$sb.AppendLine("        public static readonly ushort[] $ArrayName = new ushort[]") | Out-Null
	$sb.AppendLine("        {") | Out-Null

	# Format values in lines of 16 per line
	for ($i = 0; $i -lt $Values.Length; $i += 16) {
		$lineValues = @()
		for ($j = 0; $j -lt 16 -and $i + $j -lt $Values.Length; $j++) {
			$lineValues += ("0x{0:X4}" -f $Values[$i + $j])
		}
		$line = "            " + ($lineValues -join ", ")
		if ($i + 16 -lt $Values.Length) {
			$line += ","
		}
		$sb.AppendLine($line) | Out-Null
	}

	$sb.AppendLine("        };") | Out-Null
	$sb.AppendLine() | Out-Null

	return $sb.ToString()
}

# Main logic
Write-Host "Converting .plg files to C# arrays..."

if (-not (Test-Path $InputFolder)) {
	Write-Error "Input folder '$InputFolder' not found!"
	exit 1
}

$plgFiles = Get-ChildItem -Path $InputFolder -Filter "*.plg" -File
if ($plgFiles.Length -eq 0) {
	Write-Error "No .plg files found in '$InputFolder'"
	exit 1
}

# Generate C# file
$csharpContent = @"
using System;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Pre-parsed patch data for VS1053B codec patches.
	/// This file is auto-generated from .plg files.
	/// DO NOT EDIT MANUALLY - run Convert-PlgToCSharp.ps1 to regenerate.
	/// </summary>
	public static class PatchData
	{
"@

foreach ($plgFile in $plgFiles) {
	$baseName = [System.IO.Path]::GetFileNameWithoutExtension($plgFile.Name)
	$arrayName = "Patch_" + ($baseName -replace "-", "_")

	Write-Host "Processing $($plgFile.Name) -> $arrayName"

	$values = Parse-PlgFile -FilePath $plgFile.FullName
	if ($values.Length -gt 0) {
		$csharpContent += (Generate-CSharpArray -ArrayName $arrayName -Values $values)
	}
}

$csharpContent += @"
	}
}
"@

# Write output file
Set-Content -Path $OutputFile -Value $csharpContent -Encoding UTF8
Write-Host "Generated $OutputFile with $(($plgFiles | Measure-Object).Count) patch arrays"
Write-Host "File size: $([System.IO.FileInfo]::New($OutputFile).Length) bytes"
