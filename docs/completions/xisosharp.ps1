# PowerShell tab completion for the XISOSharp CLI.
#
# Load it from a profile or dot-source it:
#     . /path/to/xisosharp.ps1
#
# Requires PowerShell 7 (`Register-ArgumentCompleter -Native`). The completions
# are deliberately context-light: flags are offered before the first file name,
# value flags offer their documented values, and file names fall back to the
# standard path completer.

using namespace System.Management.Automation

$global:XisoSharpVerbs = @(
    'validate', 'rebuild', 'build-image', 'image-spec',
    'compress', 'cso', 'cci', 'decompress', 'uncso', 'decso', 'uncci',
    'checksum', 'split', 'join', 'joinsplit', 'convert', 'hash',
    'verify-hashes', 'verify-hash', 'title-info', 'title', 'xbe-image'
)

$global:XisoSharpFlags = @(
    '-h', '--help', '-v', '--version',
    '-c', '-x', '--unpack', '--pack', '-l', '-t', '-i', '--ls',
    '--xex-info', '--xbe-info', '--xbe-image', '--md5', '--sha256',
    '-V', '--validate', '--validate-strict', '--validate-checksums',
    '--validate-report', '--repair', '--salvage', '--repair-out',
    '--dry-run', '--dryrun', '--copy-out', '--copy-in', '--no-backup',
    '-r', '-q', '-Q', '-s', '-D', '-m', '-y', '--yes', '-n', '--no',
    '-d', '-o', '-O', '-X', '-f',
    '--skip-sectors', '--prepend-sectors', '--preserve-attrs',
    '--file-time', '--filetime', '--get-filetime', '--set-filetime',
    '--layout', '--sector-layout', '--ranges', '--is-optimized',
    '--checksum', '--silent', '--json',
    '--batch', '--batch-recursive', '--skip-existing', '--continue-on-error',
    '--name-policy', '--delete-source', '--jobs',
    '--policy', '--all', '--best', '--compress', '--zar',
    '--video', '--random', '--seed', '--wipe', '--trim', '--petrify',
    '--update', '--security-sectors', '--sectors',
    '--ciso-level', '--ciso-version', '--ciso-split',
    '--cci-level', '--cci-split', '--level',
    '--format', '--output-name', '--output', '--map', '--file',
    '--type', '--size', '--ignore-extra', '--split', '--verify-hashes',
    '--skip-sectors', '--prepend-sectors'
) | Select-Object -Unique

$global:XisoSharpValueCompletions = @{
    '--format'          = @('cso', 'cci', 'iso', 'zar')
    '--ciso-version'    = @('1', '2', 'auto')
    '--ciso-level'      = @('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')
    '--cci-level'       = @('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12')
    '--level'           = @('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')
    '--name-policy'     = @('reject', 'sanitize', 'skip')
    '--policy'          = @('skip', 'overwrite', 'auto-rename')
    '--type'            = @('logo', 'title', 'save')
    '--size'            = @('half', 'halves')
    '--jobs'            = @('1', '2', '4', '8', '16', '32', '64')
}

function Register-XisoSharpCompletion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $CommandName
    )

    Register-ArgumentCompleter -Native -CommandName $CommandName -ScriptBlock {
        param($wordToComplete, $commandAst, $cursorPosition)

        $elements = @($commandAst.CommandElements)
        $currentToken = $wordToComplete
        $previousToken = $null
        if ($elements.Count -ge 1) {
            $lastToken = $elements[$elements.Count - 1].Extent.Text
            if ($currentToken -ne '' -and $lastToken -eq $currentToken) {
                if ($elements.Count -ge 2) {
                    $previousToken = $elements[$elements.Count - 2].Extent.Text
                }
            }
            else {
                $previousToken = $lastToken
            }
        }

        # Value completions follow the flag that expects a value.
        if ($null -ne $previousToken -and $global:XisoSharpValueCompletions.ContainsKey($previousToken)) {
            foreach ($value in $global:XisoSharpValueCompletions[$previousToken]) {
                if ($value.StartsWith($currentToken, [StringComparison]::OrdinalIgnoreCase)) {
                    [CompletionResult]::new($value, $value, [CompletionResultType]::ParameterValue, $value)
                }
            }
            return
        }

        if ($currentToken.StartsWith('-')) {
            foreach ($flag in $global:XisoSharpFlags) {
                if ($flag.StartsWith($currentToken, [StringComparison]::OrdinalIgnoreCase)) {
                    [CompletionResult]::new($flag, $flag, [CompletionResultType]::ParameterName, $flag)
                }
            }
            return
        }

        # Before the first positional token, offer verbs and flags. PowerShell
        # treats the command name itself as element 0, so count the remaining
        # non-flag tokens to decide whether positionals have started.
        $positionals = @($elements | Select-Object -Skip 1 | Where-Object {
            $text = $_.Extent.Text
            -not $text.StartsWith('-') -and $text -ne $currentToken
        })

        if ($positionals.Count -eq 0) {
            foreach ($verb in $global:XisoSharpVerbs) {
                if ($verb.StartsWith($currentToken, [StringComparison]::OrdinalIgnoreCase)) {
                    [CompletionResult]::new($verb, $verb, [CompletionResultType]::Command, $verb)
                }
            }
        }

        [System.Management.Automation.CompletionCompleters]::CompleteFilename($currentToken)
    }
}

Register-XisoSharpCompletion -CommandName 'XISOSharp'
Register-XisoSharpCompletion -CommandName 'xisosharp'
