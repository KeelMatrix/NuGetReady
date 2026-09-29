[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1'
$guardPath = $PSCommandPath

function Get-ParsedCommandRecords(
    [string]$Text,
    [string]$Path,
    [System.Management.Automation.Language.Ast]$InitialAst
) {
    $pending = [System.Collections.Generic.Queue[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending.Enqueue([pscustomobject]@{
            Text = $Text
            Ast = $InitialAst
            BaseLine = 0
            Embedded = $false
        })

    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        if ([string]::IsNullOrWhiteSpace($item.Text) -or -not $seen.Add($item.Text)) {
            continue
        }

        $ast = $item.Ast
        if ($null -eq $ast) {
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                $item.Text,
                [ref]$tokens,
                [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) {
                continue
            }
        }

        foreach ($command in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))) {
            [pscustomobject]@{
                Path = $Path
                BaseLine = $item.BaseLine
                Command = $command
            }
        }

        foreach ($stringAst in @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]
                }, $true))) {
            $value = [string]$stringAst.Value
            $isHereString = $stringAst -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                ([string]$stringAst.StringConstantType -match 'HereString')
            $isScriptLike = $value -match '(?i)(?:\r?\n|(?:^|\s)(?:Start-Process|pwsh(?:\.exe)?|powershell(?:\.exe)?|Invoke-Expression)\b\s+\S)'
            if (-not ($isHereString -or ($item.Embedded -and $isScriptLike))) {
                continue
            }

            $pending.Enqueue([pscustomobject]@{
                    Text = $value
                    Ast = $null
                    BaseLine = $item.BaseLine + $stringAst.Extent.StartLineNumber - 1
                    Embedded = $true
                })
        }
    }
}

function Get-CSharpLaunchViolations([string]$Path) {
    $lines = [IO.File]::ReadAllLines($Path)
    $violations = [System.Collections.Generic.List[string]]::new()
    $safeVariables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $isInitializer = $lines[$index] -match '(?<![\w:])new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\b' -or
            $lines[$index] -match '\bProcessStartInfo\s*\{'
        if ($isInitializer) {
            $end = -1
            for ($candidate = $index; $candidate -lt [Math]::Min($lines.Count, $index + 161); $candidate++) {
                if ($lines[$candidate] -match '}\s*\)?!?\s*;') {
                    $end = $candidate
                    break
                }
            }

            if ($end -lt 0) {
                [void]$violations.Add(('{0}:{1}: ProcessStartInfo initializer could not be inspected' -f $Path, ($index + 1)))
                continue
            }

            $initializer = $lines[$index..$end] -join [Environment]::NewLine
            $hasUseShellExecuteFalse = $initializer -match '(?im)\bUseShellExecute\s*=\s*false\b'
            $hasHiddenContainment = $initializer -match '(?im)\bCreateNoWindow\s*=\s*true\b|\bWindowStyle\s*=\s*(?:ProcessWindowStyle\.)?Hidden\b'
            $variableMatches = [regex]::Matches($initializer, '(?im)\b(?<name>[A-Za-z_]\w*)\s*=\s*new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\b')
            if ($hasUseShellExecuteFalse -and $hasHiddenContainment) {
                foreach ($match in $variableMatches) {
                    [void]$safeVariables.Add($match.Groups['name'].Value)
                }
            }
            else {
                [void]$violations.Add(('{0}:{1}: ProcessStartInfo requires UseShellExecute = false and hidden containment' -f $Path, ($index + 1)))
            }
        }

        if ($lines[$index] -cmatch '\bProcess\.Start\s*\(' -and
            $lines[$index] -cnotmatch 'new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\b') {
            $startCall = [regex]::Match($lines[$index], '\bProcess\.Start\s*\(\s*(?<argument>[^,)]+)')
            $argument = if ($startCall.Success) { $startCall.Groups['argument'].Value.Trim() } else { '' }
            if ($argument -notmatch '^[A-Za-z_]\w*$' -or -not $safeVariables.Contains($argument)) {
                [void]$violations.Add(('{0}:{1}: Process.Start requires a contained ProcessStartInfo' -f $Path, ($index + 1)))
            }
        }

        if ($lines[$index] -match '(?i)ProcessStartInfo\s*\]\s*::\s*new|ProcessStartInfo\s*::\s*new|\[System\.Diagnostics\.Process\]\s*::\s*Start\s*\(') {
            $embeddedEnd = [Math]::Min($lines.Count - 1, $index + 8)
            $embedded = $lines[$index..$embeddedEnd] -join ' '
            if ($embedded -notmatch '(?i)\bCreateNoWindow\s*=\s*\$true\b') {
                [void]$violations.Add(('{0}:{1}: embedded .NET process launch lacks CreateNoWindow = $true' -f $Path, ($index + 1)))
            }
        }
    }

    return $violations.ToArray()
}

function Get-LaunchViolations([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        return @('{0} contains PowerShell parse errors.' -f $Path)
    }

    $violations = [System.Collections.Generic.List[string]]::new()
    $commands = @(Get-ParsedCommandRecords -Text ([IO.File]::ReadAllText($Path)) -Path $Path -InitialAst $ast)
    $assignments = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst]
            }, $true))
    foreach ($record in $commands) {
        $command = $record.Command
        $lineNumber = $record.BaseLine + $command.Extent.StartLineNumber
        $nameAst = $command.CommandElements[0]
        $commandName = if ($nameAst -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
            $nameAst.Value
        }
        elseif ($nameAst -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
            $nameAst.Value
        }
        else {
            $null
        }

        if ($commandName -match '^(?i:pwsh|powershell)(?:\.exe)?$') {
            [void]$violations.Add(('{0}:{1}: direct nested PowerShell launch' -f $Path, $lineNumber))
            continue
        }

        $literalArguments = @($command.CommandElements | Select-Object -Skip 1 | Where-Object {
                $_ -is [System.Management.Automation.Language.StringConstantExpressionAst]
            } | ForEach-Object { $_.Value })
        if ($commandName -notmatch '^(?i:Invoke-NestedPwsh|Invoke-NestedProcess)$' -and
            $literalArguments | Where-Object { $_ -match '^(?i:pwsh|powershell)(?:\.exe)?$' }) {
            [void]$violations.Add(('{0}:{1}: nested PowerShell executable passed to {2}' -f $Path, $lineNumber, $commandName))
        }

        $hasHiddenContainment = $command.Extent.Text -match '(?i)(?:-\s*WindowStyle\s*(?:=|\s)\s*[''"]?Hidden[''"]?(?=\s|$)|(?<!\w)-NoNewWindow(?=\s|$))'
        if (-not $hasHiddenContainment -and $commandName -eq 'Start-Process') {
            foreach ($splat in @($command.CommandElements | Where-Object {
                        $_ -is [System.Management.Automation.Language.VariableExpressionAst] -and $_.Splatted
                    })) {
                $variableName = $splat.VariablePath.UserPath
                $basePattern = '^\$' + [regex]::Escape($variableName) + '$'
                $stylePattern = '^\$' + [regex]::Escape($variableName) + '(?:\.(?:WindowStyle|NoNewWindow)|\[[''"](?:WindowStyle|NoNewWindow)[''"]\])$'
                $latestBase = @($assignments | Where-Object {
                            $_.Extent.StartOffset -lt $command.Extent.StartOffset -and
                            $_.Left.Extent.Text -match $basePattern
                        } | Sort-Object { $_.Extent.StartOffset } | Select-Object -Last 1)
                $latestStyle = @($assignments | Where-Object {
                            $_.Extent.StartOffset -lt $command.Extent.StartOffset -and
                            $_.Left.Extent.Text -match $stylePattern
                        } | Sort-Object { $_.Extent.StartOffset } | Select-Object -Last 1)
                if (($latestBase.Count -gt 0 -and $latestBase[0].Right.Extent.Text -match '(?i)(?:WindowStyle\s*=\s*[''"]Hidden[''"]|NoNewWindow\s*=\s*\$true)') -or
                    ($latestStyle.Count -gt 0 -and $latestStyle[0].Right.Extent.Text -match '(?i)^(?:[''"]Hidden[''"]|\$true)$')) {
                    $hasHiddenContainment = $true
                    break
                }
            }
        }
        if ($commandName -eq 'Start-Process' -and -not $hasHiddenContainment) {
            [void]$violations.Add(('{0}:{1}: Start-Process lacks hidden-window containment' -f $Path, $lineNumber))
        }
    }

    return $violations.ToArray()
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) "nested-pwsh-guard-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $selfTestRoot -Force | Out-Null
    try {
        $directPath = Join-Path $selfTestRoot 'direct.ps1'
        $processPath = Join-Path $selfTestRoot 'process.ps1'
        $embeddedPath = Join-Path $selfTestRoot 'embedded.ps1'
        $embeddedSafePath = Join-Path $selfTestRoot 'embedded-safe.ps1'
        $splatSafePath = Join-Path $selfTestRoot 'splat-safe.ps1'
        $splatNoNewWindowPath = Join-Path $selfTestRoot 'splat-nonewwindow-safe.ps1'
        $safePath = Join-Path $selfTestRoot 'safe.ps1'
        $csharpVisiblePath = Join-Path $selfTestRoot 'visible.cs'
        $csharpSafePath = Join-Path $selfTestRoot 'safe.cs'
        $csharpDirectPath = Join-Path $selfTestRoot 'direct.cs'
        $embeddedDotNetPath = Join-Path $selfTestRoot 'embedded-dotnet.cs'
        $embeddedDotNetSafePath = Join-Path $selfTestRoot 'embedded-dotnet-safe.cs'
        [IO.File]::WriteAllText($directPath, '& pwsh -NoProfile')
        [IO.File]::WriteAllText($processPath, "Start-Process 'example.exe'")
        [IO.File]::WriteAllText($embeddedPath, @'
$nested = @"
Start-Process -FilePath 'example.exe'
"@
Invoke-NestedPwsh -ArgumentList $nested
'@)
        [IO.File]::WriteAllText($embeddedSafePath, @'
$nested = @"
Start-Process -FilePath 'example.exe' -WindowStyle Hidden
"@
Invoke-NestedPwsh -ArgumentList $nested
'@)
        [IO.File]::WriteAllText($splatSafePath, @'
$parameters = @{ FilePath = 'example.exe' }
$parameters.WindowStyle = 'Hidden'
Start-Process @parameters
'@)
        [IO.File]::WriteAllText($splatNoNewWindowPath, @'
$parameters = @{ FilePath = 'example.exe' }
$parameters.NoNewWindow = $true
Start-Process @parameters
'@)
        [IO.File]::WriteAllText($safePath, "Invoke-NestedPwsh -ArgumentList @('-NoProfile')")
        [IO.File]::WriteAllText($csharpVisiblePath, 'new ProcessStartInfo { UseShellExecute = false };')
        [IO.File]::WriteAllText($csharpSafePath, @'
var startInfo = new ProcessStartInfo
{
    UseShellExecute = false,
    CreateNoWindow = true
};
Process.Start(startInfo);
'@)
        [IO.File]::WriteAllText($csharpDirectPath, 'Process.Start("example.exe");')
        [IO.File]::WriteAllText($embeddedDotNetPath, 'var script = "$psi = [System.Diagnostics.ProcessStartInfo]::new(); $psi.UseShellExecute = $false; [System.Diagnostics.Process]::Start($psi);";')
        [IO.File]::WriteAllText($embeddedDotNetSafePath, 'var script = "$psi = [System.Diagnostics.ProcessStartInfo]::new(); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; [System.Diagnostics.Process]::Start($psi);";')
        if (@(Get-LaunchViolations $directPath).Count -eq 0) {
            throw 'The guard self-test did not reject a direct nested PowerShell launch.'
        }
        if (@(Get-LaunchViolations $processPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch.'
        }
        if (@(Get-LaunchViolations $embeddedPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch embedded in a here-string.'
        }
        if (@(Get-LaunchViolations $embeddedSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a hidden Start-Process launch embedded in a here-string.'
        }
        if (@(Get-LaunchViolations $splatSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a hidden Start-Process launch supplied through a splatted parameter set.'
        }
        if (@(Get-LaunchViolations $splatNoNewWindowPath).Count -ne 0) {
            throw 'The guard self-test rejected a no-new-window Start-Process launch supplied through a splatted parameter set.'
        }
        if (@(Get-LaunchViolations $safePath).Count -ne 0) {
            throw 'The guard self-test rejected a helper-mediated launch.'
        }
        if (@(Get-CSharpLaunchViolations $csharpVisiblePath).Count -eq 0) {
            throw 'The guard self-test did not reject an uncontained C# ProcessStartInfo initializer.'
        }
        if (@(Get-CSharpLaunchViolations $csharpSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a contained C# ProcessStartInfo initializer.'
        }
        if (@(Get-CSharpLaunchViolations $csharpDirectPath).Count -eq 0) {
            throw 'The guard self-test did not reject a direct C# Process.Start launch.'
        }
        if (@(Get-CSharpLaunchViolations $embeddedDotNetPath).Count -eq 0) {
            throw 'The guard self-test did not reject an embedded .NET process launch.'
        }
        if (@(Get-CSharpLaunchViolations $embeddedDotNetSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a contained embedded .NET process launch.'
        }
    }
    finally {
        Remove-Item -LiteralPath $selfTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Output 'Nested PowerShell launch guard self-test passed.'
    exit 0
}

if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Shared nested PowerShell launch helper is missing: $helperPath"
}

$scriptFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File |
    Where-Object {
        $_.Extension -in @('.ps1', '.psm1', '.psd1') -and
        $_.FullName -notin @($helperPath, $guardPath) -and
        $_.FullName -notmatch '[\\/]((\.git)|(bin)|(obj)|(artifacts)|_probe[\\/]corpus)([\\/]|$)'
    }
$violations = @($scriptFiles | ForEach-Object { Get-LaunchViolations $_.FullName })
$csharpFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.cs' |
    Where-Object {
        $_.FullName -notmatch '[\\/]((\.git)|(bin)|(obj)|(artifacts)|_probe[\\/]corpus)([\\/]|$)'
    }
$violations += @($csharpFiles | ForEach-Object { Get-CSharpLaunchViolations $_.FullName })
if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw "Visible child process launch sites must use hidden containment."
}

Write-Output 'Nested PowerShell and .NET process launch guard passed.'
