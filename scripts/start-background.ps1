# Compatibility entry point: the hub and collector are separate .NET processes.
param([switch]$Collector)
& (Join-Path $PSScriptRoot 'start-dotnet.ps1') -Collector:$Collector
