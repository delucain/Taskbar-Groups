$ErrorActionPreference = 'Stop'

$Root =
    'C:\code\_Scripts\Taskbar-Groups'

$GroupsRoot =
    Join-Path $Root 'Groups'

$AllowedExtensions = @(
    '.lnk',
    '.url',
    '.exe',
    '.bat',
    '.cmd',
    '.ps1',
    '.separator',
    '.heading'
)

if (-not (Test-Path -LiteralPath $GroupsRoot)) {
    throw "Groups directory not found: $GroupsRoot"
}

function Remove-SortPrefix {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    # Remove any existing numeric sort prefix:
    #
    #   1 - Name
    #   01 - Name
    #   010 - Name
    #   025 - Name
    #
    # Also tolerates the older punctuation forms supported
    # by Taskbar-GroupMenu.

    return (
        $Name `
            -replace `
            '^\s*\d+\s*[-._)]\s*',
            ''
    )
}

$Groups =
    @(
        Get-ChildItem `
            -LiteralPath $GroupsRoot `
            -Directory |
        Sort-Object Name
    )

Write-Host ''
Write-Host 'Taskbar Groups Renumber'
Write-Host '======================='
Write-Host ''

foreach ($Group in $Groups) {

    Write-Host "[$($Group.Name)]"
    Write-Host ''

    $Items =
        @(
            Get-ChildItem `
                -LiteralPath $Group.FullName `
                -File |
            Where-Object {
                $AllowedExtensions -contains
                    $_.Extension.ToLowerInvariant()
            } |
            ForEach-Object {

                $CleanBaseName =
                    Remove-SortPrefix `
                        -Name $_.BaseName

                [PSCustomObject]@{
                    File =
                        $_

                    CleanBaseName =
                        $CleanBaseName

                    Extension =
                        $_.Extension

                    TemporaryPath =
                        $null

                    FinalPath =
                        $null
                }
            } |
            Sort-Object `
                CleanBaseName,
                Extension
        )

    if ($Items.Count -eq 0) {
        Write-Host '  No items.'
        Write-Host ''
        continue
    }

    # ========================================================
    # First pass:
    #
    # Rename everything to unique temporary filenames.
    #
    # This avoids collisions such as:
    #
    #   030 - Foo.lnk
    #
    # needing to become:
    #
    #   020 - Foo.lnk
    #
    # while another existing item already owns that filename.
    # ========================================================

    foreach ($Item in $Items) {

        $TemporaryName =
            '__REN_' +
            [Guid]::NewGuid().ToString('N') +
            $Item.Extension

        $TemporaryPath =
            Join-Path `
                $Group.FullName `
                $TemporaryName

        Rename-Item `
            -LiteralPath $Item.File.FullName `
            -NewName $TemporaryName

        $Item.TemporaryPath =
            $TemporaryPath
    }

    # ========================================================
    # Second pass:
    #
    # Assign:
    #
    #   010
    #   020
    #   030
    #   ...
    #
    # based on alphabetical order of the display names.
    # ========================================================

    for (
        $Index = 0;
        $Index -lt $Items.Count;
        $Index++
    ) {

        $Item =
            $Items[$Index]

        $Number =
            ($Index + 1) * 10

        # D3 means:
        #
        #   10  -> 010
        #   20  -> 020
        #   90  -> 090
        #   100 -> 100
        #
        # It therefore remains usable even if a group eventually
        # exceeds 99 entries.

        $Prefix =
            $Number.ToString('D3')

        $FinalName =
            "$Prefix - " +
            $Item.CleanBaseName +
            $Item.Extension

        $FinalPath =
            Join-Path `
                $Group.FullName `
                $FinalName

        Rename-Item `
            -LiteralPath $Item.TemporaryPath `
            -NewName $FinalName

        $Item.FinalPath =
            $FinalPath

        Write-Host "  $FinalName"
    }

    Write-Host ''
}

Write-Host '======================='
Write-Host 'Renumbering complete.'
Write-Host ''