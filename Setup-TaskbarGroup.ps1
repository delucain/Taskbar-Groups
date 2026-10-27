param(
    [Parameter(Mandatory = $true)]
    [string]$Group,

    [string]$Icon
)

$ErrorActionPreference = 'Stop'

$Root =
    'C:\code\_Scripts\Taskbar-Groups'

$LauncherExe =
    Join-Path $Root 'Taskbar-GroupMenu.exe'

$GroupsRoot =
    Join-Path $Root 'Groups'

$PinFolder =
    Join-Path $Root 'Pin to Taskbar'

$IconLibrary =
    Join-Path $Root 'Icon Library'

$GroupFolder =
    Join-Path $GroupsRoot $Group

$ShortcutPath =
    Join-Path $PinFolder ($Group + '.lnk')

if (-not (
    Test-Path -LiteralPath $LauncherExe
)) {
    throw "Launcher not found: $LauncherExe"
}

New-Item `
    -ItemType Directory `
    -Force `
    -Path `
        $GroupsRoot,
        $GroupFolder,
        $PinFolder,
        $IconLibrary |
    Out-Null

if (-not $Icon) {

    $namedIcon =
        Join-Path `
            $IconLibrary `
            ($Group + '.ico')

    if (
        Test-Path -LiteralPath $namedIcon
    ) {
        $Icon = $namedIcon
    }
}

if (-not $Icon) {

    Add-Type `
        -AssemblyName System.Windows.Forms

    Add-Type `
        -AssemblyName System.Drawing

    $form =
        New-Object `
            System.Windows.Forms.Form

    $form.Text =
        "Choose icon - $Group"

    $form.Width = 760
    $form.Height = 560

    $form.StartPosition =
        'CenterScreen'

    $form.MinimizeBox = $false
    $form.MaximizeBox = $false

    $list =
        New-Object `
            System.Windows.Forms.ListView

    $list.Dock = 'Fill'
    $list.View = 'LargeIcon'
    $list.MultiSelect = $false
    $list.HideSelection = $false

    $images =
        New-Object `
            System.Windows.Forms.ImageList

    $images.ImageSize =
        New-Object `
            System.Drawing.Size(
                48,
                48
            )

    $images.ColorDepth =
        'Depth32Bit'

    $list.LargeImageList =
        $images

    $files = @(
        Get-ChildItem `
            -LiteralPath $IconLibrary `
            -Filter '*.ico' |
        Sort-Object BaseName
    )

    for (
        $i = 0;
        $i -lt $files.Count;
        $i++
    ) {
        $ico =
            New-Object `
                System.Drawing.Icon(
                    $files[$i].FullName,
                    48,
                    48
                )

        $images.Images.Add(
            $ico.ToBitmap()
        ) | Out-Null

        $ico.Dispose()

        $item =
            New-Object `
                System.Windows.Forms.ListViewItem(
                    $files[$i].BaseName,
                    $i
                )

        $item.Tag =
            $files[$i].FullName

        $list.Items.Add(
            $item
        ) | Out-Null
    }

    $panel =
        New-Object `
            System.Windows.Forms.FlowLayoutPanel

    $panel.Dock = 'Bottom'
    $panel.Height = 52
    $panel.FlowDirection =
        'RightToLeft'

    $ok =
        New-Object `
            System.Windows.Forms.Button

    $ok.Text = 'Use Selected'
    $ok.Width = 110
    $ok.DialogResult = 'OK'

    $browse =
        New-Object `
            System.Windows.Forms.Button

    $browse.Text = 'Browse...'
    $browse.Width = 90

    $cancel =
        New-Object `
            System.Windows.Forms.Button

    $cancel.Text = 'Cancel'
    $cancel.Width = 90
    $cancel.DialogResult =
        'Cancel'

    $panel.Controls.AddRange(
        @(
            $ok,
            $browse,
            $cancel
        )
    )

    $form.Controls.Add($list)
    $form.Controls.Add($panel)

    $form.AcceptButton = $ok
    $form.CancelButton = $cancel

    $browse.Add_Click({

        $dialog =
            New-Object `
                System.Windows.Forms.OpenFileDialog

        $dialog.Filter =
            'Icon files (*.ico)|*.ico|All files (*.*)|*.*'

        if (
            $dialog.ShowDialog() -eq
            'OK'
        ) {
            $script:Icon =
                $dialog.FileName

            $form.DialogResult =
                'OK'

            $form.Close()
        }
    })

    $list.Add_DoubleClick({

        if (
            $list.SelectedItems.Count
        ) {
            $form.DialogResult =
                'OK'

            $form.Close()
        }
    })

    if (
        $form.ShowDialog() -eq
        'OK'
    ) {
        if (
            -not $Icon -and
            $list.SelectedItems.Count
        ) {
            $Icon =
                [string]
                $list.SelectedItems[0].Tag
        }
    }
    else {
        return
    }
}

$ws =
    New-Object `
        -ComObject WScript.Shell

$shortcut =
    $ws.CreateShortcut(
        $ShortcutPath
    )

$shortcut.TargetPath =
    $LauncherExe

$shortcut.Arguments =
    '"' +
    $Group.Replace(
        '"',
        '""'
    ) +
    '"'

$shortcut.WorkingDirectory =
    $Root

$shortcut.Description =
    "$Group taskbar group"

if ($Icon) {
    $shortcut.IconLocation =
        $Icon
}
else {
    $shortcut.IconLocation =
        "$env:SystemRoot\System32\shell32.dll,168"
}

$shortcut.Save()

Write-Host ''
Write-Host "Created/updated:"
Write-Host "  $ShortcutPath"
Write-Host ''
Write-Host "Group folder:"
Write-Host "  $GroupFolder"
Write-Host ''
Write-Host "Icon:"
Write-Host "  $Icon"
