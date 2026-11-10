# Gera o executável único da Prancheta Database (Windows x64, sem precisar instalar o .NET).
# Uso, na raiz do repositório:  .\publicar.ps1
# Resultado: dist\PranchetaDatabase.exe  (anexe esse arquivo numa Release do GitHub; não commite).

$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$dist = Join-Path $raiz 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish (Join-Path $raiz 'prancheta-api\PranchetaApi.csproj') -c Release -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }

# Mantém só o executável: appsettings e arquivos de depuração não são necessários para rodar.
Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'PranchetaDatabase.exe' } | Remove-Item -Force

$exe = Get-Item (Join-Path $dist 'PranchetaDatabase.exe')
"{0}  {1:N1} MB" -f $exe.FullName, ($exe.Length / 1MB)
