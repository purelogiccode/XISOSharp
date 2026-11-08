# bash completion for the XISOSharp CLI.
#
# Install:
#   source /path/to/xisosharp.bash
# or copy it to one of:
#   /etc/bash_completion.d/xisosharp
#   ~/.local/share/bash-completion/completions/XISOSharp
#
# Also completes the lowercase `xisosharp` command alias used by package
# managers.

# shellcheck shell=bash
# shellcheck disable=SC2207

_xisosharp_verbs=(
    validate rebuild build-image image-spec
    compress cso cci decompress uncso decso uncci
    checksum split join joinsplit convert hash
    verify-hashes verify-hash verify-dat verify-dats title-info title xbe-image
    xex-image disc-info apply-skeleton file-hashes
)

_xisosharp_flags=(
    -h --help -v --version
    -c -x --unpack --pack -l -t -i --ls
    --xex-info --xbe-info --xbe-image --md5 --sha256
    -V --validate --validate-strict --validate-checksums --validate-report
    --repair --salvage --repair-out --dry-run --dryrun
    --copy-out --copy-in --no-backup
    -r -q -Q -s -D -m -y --yes -n --no
    -d -o -O -X -f
    --skip-sectors --prepend-sectors --preserve-attrs
    --file-time --filetime --get-filetime --set-filetime
    --layout --sector-layout --ranges --is-optimized
    --checksum --silent --json
    --batch --batch-recursive --skip-existing --continue-on-error
    --name-policy --delete-source --jobs
    --policy --all --best --compress --zar
    --video --random --seed --wipe --trim --petrify --update
    --security-sectors --sectors
    --ciso-level --ciso-version --ciso-split
    --cci-level --cci-split --level
    --format --output-name --output --map --file --type --size --ignore-extra
    --split --verify-hashes
    --xex-image --disc-info --apply-skeleton --file-hashes
    --zar-level --deterministic --strict-names --symlink-mode --algorithm --algo --hash --hashes
)

_xisosharp_word() {
    printf '%s\n' "$1"
}

_xisosharp_complete_from() {
    local word="$1" candidate
    shift
    for candidate in "$@"; do
        if [[ $candidate == "$word"* ]]; then
            _xisosharp_word "$candidate"
        fi
    done
}

_xisosharp_value_completions() {
    case "$1" in
        --format)       _xisosharp_complete_from "$2" cso cci iso zar ;;
        --ciso-version) _xisosharp_complete_from "$2" 1 2 auto ;;
        --ciso-level|--level)
                        _xisosharp_complete_from "$2" 0 1 2 3 4 5 6 7 8 9 ;;
        --cci-level)    _xisosharp_complete_from "$2" 0 1 2 3 4 5 6 7 8 9 10 11 12 ;;
        --name-policy)  _xisosharp_complete_from "$2" reject sanitize skip ;;
        --symlink-mode) _xisosharp_complete_from "$2" follow skip reject ;;
        --policy)       _xisosharp_complete_from "$2" skip overwrite auto-rename ;;
        --type)         _xisosharp_complete_from "$2" logo title save ;;
        --size)         _xisosharp_complete_from "$2" half halves ;;
        --jobs)         _xisosharp_complete_from "$2" 1 2 4 8 16 32 64 ;;
        --algorithm|--algo)
                        _xisosharp_complete_from "$2" sha1 sha256 md5 ;;
        --zar-level)    _xisosharp_complete_from "$2" 1 6 9 12 16 19 22 ;;
    esac
}

_xisosharp() {
    local cur prev
    cur="${COMP_WORDS[COMP_CWORD]}"
    prev=""
    if (( COMP_CWORD > 0 )); then
        prev="${COMP_WORDS[COMP_CWORD - 1]}"
    fi

    case "$prev" in
        --format|--ciso-version|--ciso-level|--cci-level|--level|--name-policy|--symlink-mode|--policy|--type|--size|--jobs|--algorithm|--algo|--zar-level)
            local values
            values=$(_xisosharp_value_completions "$prev" "$cur")
            if [[ -n $values ]]; then
                COMPREPLY=( $(compgen -W "$values" -- "$cur") )
                return 0
            fi
            ;;
    esac

    # Flags are only valid before the first positional token.
    local positional=""
    local i
    for (( i = 1; i < COMP_CWORD; i++ )); do
        case "${COMP_WORDS[i]}" in
            -*) ;;
            *) positional=1; break ;;
        esac
    done

    if [[ $cur == -* ]]; then
        COMPREPLY=( $(compgen -W "${_xisosharp_flags[*]}" -- "$cur") )
        return 0
    fi

    if [[ -z $positional ]]; then
        COMPREPLY=( $(compgen -W "${_xisosharp_verbs[*]}" -- "$cur") )
        if (( ${#COMPREPLY[@]} > 0 )); then
            return 0
        fi
    fi

    # Fall back to the default file completion (`-o default`).
    return 0
}

complete -o default -F _xisosharp XISOSharp xisosharp
