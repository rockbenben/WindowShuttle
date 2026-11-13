param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Releases
)
$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v\d+\.\d+\.\d+$') { throw 'Invalid release tag' }
$candidate = [version]$Tag.Substring(1)
foreach ($release in $Releases) {
    if (-not $release.draft -and $release.tag_name -eq $Tag) {
        throw 'This tag is already published; use a new version'
    }
    if (-not $release.draft -and -not $release.prerelease -and $release.tag_name -match '^v(\d+\.\d+\.\d+)$') {
        if ([version]$Matches[1] -ge $candidate) { throw 'A same or newer version is already published' }
    }
}
