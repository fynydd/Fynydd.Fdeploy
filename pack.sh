rm -r Fynydd.Fdeploy/nupkg
source clean.sh
cd Fynydd.Fdeploy
dotnet pack --configuration Release
cd ..
