# fish completion for the XISOSharp CLI.
#
# Install:
#   cp xisosharp.fish ~/.config/fish/completions/XISOSharp.fish
# or source it from config.fish.

function __xisosharp_complete
    set -l verbs validate rebuild build-image image-spec compress cso cci \
        decompress uncso decso uncci checksum split join joinsplit convert \
        hash verify-hashes verify-hash verify-dat verify-dats title-info title xbe-image xex-image \
        disc-info apply-skeleton file-hashes

    set -l short_flags -h -v -c -x -l -t -i -V -r -q -Q -s -D -m -y -n \
        -d -o -O -X -f

    set -l long_flags \
        --help --version --unpack --pack --ls --xex-info --xbe-info \
        --xbe-image --md5 --sha256 --validate --validate-strict \
        --validate-checksums --validate-report --repair --salvage \
        --repair-out --dry-run --dryrun --copy-out --copy-in --no-backup \
        --yes --no --skip-sectors --prepend-sectors --preserve-attrs \
        --file-time --filetime --get-filetime --set-filetime --layout \
        --sector-layout --ranges --is-optimized --checksum --silent --json \
        --batch --batch-recursive --skip-existing --continue-on-error \
        --name-policy --delete-source --jobs --policy --all --best \
        --compress --zar --video --random --seed --wipe --trim --petrify \
        --update --security-sectors --sectors --ciso-level --ciso-version \
        --ciso-split --cci-level --cci-split --level --split --format \
        --output-name --output --map --file --type --size --ignore-extra \
        --verify-hashes --xex-image --disc-info --apply-skeleton --file-hashes \
        --zar-level --deterministic --strict-names --symlink-mode --algorithm --algo --hash --hashes

    for cmd in XISOSharp xisosharp
        # Verbs.
        for verb in $verbs
            complete -c $cmd -n "not __fish_seen_subcommand_from $verbs" \
                -a $verb -d $verb
        end

        # Short and long flags.
        for flag in $short_flags
            complete -c $cmd -s (string sub -s 2 -- $flag)
        end
        for flag in $long_flags
            complete -c $cmd -l (string sub -s 3 -- $flag)
        end

        # Documented value sets.
        complete -c $cmd -l format -x -a "cso cci iso zar"
        complete -c $cmd -l ciso-version -x -a "1 2 auto"
        complete -c $cmd -l ciso-level -x -a "0 1 2 3 4 5 6 7 8 9"
        complete -c $cmd -l cci-level -x -a "0 1 2 3 4 5 6 7 8 9 10 11 12"
        complete -c $cmd -l level -x -a "0 1 2 3 4 5 6 7 8 9"
        complete -c $cmd -l name-policy -x -a "reject sanitize skip"
        complete -c $cmd -l symlink-mode -x -a "follow skip reject"
        complete -c $cmd -l policy -x -a "skip overwrite auto-rename"
        complete -c $cmd -l type -x -a "logo title save"
        complete -c $cmd -l size -x -a "half halves"
        complete -c $cmd -l jobs -x -a "1 2 4 8 16 32 64"
        complete -c $cmd -l algorithm -x -a "sha1 sha256 md5"
        complete -c $cmd -l algo -x -a "sha1 sha256 md5"
        complete -c $cmd -l zar-level -x -a "1 6 9 12 16 19 22"

        # File name completion after the first positional argument.
        complete -c $cmd -f -n "__fish_seen_subcommand_from $verbs" \
            -a "(__fish_complete_path)"
    end
end

__xisosharp_complete
