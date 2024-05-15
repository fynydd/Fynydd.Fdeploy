# Delete all build files and restore dependencies from nuget servers
# ------------------------------------------------------------------

rm -r Fynydd.Fdeploy/bin
rm -r Fynydd.Fdeploy/obj

dotnet restore Fynydd.Fdeploy/Fynydd.Fdeploy.csproj
