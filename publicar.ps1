# Gera o pacote da Prancheta Database para distribuir (Windows, roda pelo .NET 10 instalado).
# Uso, na raiz do repositório:  .\publicar.ps1
# Resultado: dist\PranchetaDatabase.zip  (anexe esse arquivo numa Release do GitHub; não commite).
#
# Não gera .exe próprio de propósito: máquinas com política que bloqueia executável não assinado
# rodam a dll pelo dotnet.exe instalado, que é assinado pela Microsoft.

$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$dist = Join-Path $raiz 'dist'
$pasta = Join-Path $dist 'PranchetaDatabase'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish (Join-Path $raiz 'prancheta-api\PranchetaApi.csproj') -c Release -o $pasta --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }

# appsettings de desenvolvimento não vai no pacote.
Remove-Item (Join-Path $pasta 'appsettings.Development.json') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $raiz 'Abrir Prancheta.cmd') $pasta

$zip = Join-Path $dist 'PranchetaDatabase.zip'
Compress-Archive -Path $pasta -DestinationPath $zip
$z = Get-Item $zip
"{0}  {1:N1} MB" -f $z.FullName, ($z.Length / 1MB)
