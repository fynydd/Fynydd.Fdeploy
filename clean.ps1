# Delete all build files and restore dependencies from nuget servers
# ------------------------------------------------------------------

Remove-Item -Path "Fynydd.Fdeploy\bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "Fynydd.Fdeploy\obj" -Recurse -Force -ErrorAction SilentlyContinue

dotnet restore Fynydd.Fdeploy\Fynydd.Fdeploy.csproj
