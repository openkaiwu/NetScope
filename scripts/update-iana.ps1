param(
    [string]$Destination = "$PSScriptRoot\..\src\NetScope.Windows\Data\service-names-port-numbers.csv"
)

$ErrorActionPreference = 'Stop'
$uri = 'https://www.iana.org/assignments/service-names-port-numbers/service-names-port-numbers.csv'
$temporary = "$Destination.tmp"
Invoke-WebRequest -Uri $uri -OutFile $temporary -UseBasicParsing
if ((Get-Item -LiteralPath $temporary).Length -lt 500000) {
    Remove-Item -LiteralPath $temporary -Force
    throw 'Downloaded IANA registry is unexpectedly small.'
}
Move-Item -LiteralPath $temporary -Destination $Destination -Force
Write-Output "Updated IANA registry: $Destination"
