if (Test-Path ".\Fynydd.Fdeploy\nupkg") { Remove-Item ".\Fynydd.Fdeploy\nupkg" -Recurse -Force }
. ./clean.ps1
Set-Location Fynydd.Fdeploy
dotnet pack --configuration Release
Set-Location ..
