#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
source_unit="$repository_root/deploy/systemd/monkeysphere.service"
if ! command -v systemd-analyze >/dev/null 2>&1; then
    echo "Required command is unavailable: systemd-analyze" >&2
    exit 2
fi

working_root=$(mktemp -d -t monkeysphere-systemd-verify-XXXXXX)
runtime="$working_root/dotnet"
application_root="$working_root/app"
data_root="$working_root/data"
password_file="$working_root/admin-password"
verified_unit="$working_root/monkeysphere.service"

cleanup() {
    case "$working_root" in
        /tmp/monkeysphere-systemd-verify-*) rm -rf -- "$working_root" ;;
        *) echo "Refusing to remove unexpected verification path: $working_root" >&2 ;;
    esac
}
trap cleanup EXIT HUP INT TERM

mkdir -p "$application_root" "$data_root"
: >"$runtime"
: >"$application_root/Monkeysphere.Web.dll"
: >"$password_file"
chmod 0755 "$runtime"

current_user=$(id -un)
current_group=$(id -gn)
sed \
    -e "s|^WorkingDirectory=.*|WorkingDirectory=$application_root|" \
    -e "s|^ExecStart=.*|ExecStart=$runtime $application_root/Monkeysphere.Web.dll|" \
    -e "s|^Environment=MONKEYSPHERE_DATA_ROOT=.*|Environment=MONKEYSPHERE_DATA_ROOT=$data_root|" \
    -e "s|^Environment=MONKEYSPHERE_ADMIN_PASSWORD_FILE=.*|Environment=MONKEYSPHERE_ADMIN_PASSWORD_FILE=$password_file|" \
    -e "s|^User=.*|User=$current_user|" \
    -e "s|^Group=.*|Group=$current_group|" \
    -e "s|^ReadWritePaths=.*|ReadWritePaths=$data_root|" \
    "$source_unit" >"$verified_unit"
chmod 0644 "$verified_unit"

systemd-analyze verify "$verified_unit"
grep -q '^Type=notify$' "$source_unit"
grep -q '^NoNewPrivileges=true$' "$source_unit"
grep -q '^PrivateTmp=true$' "$source_unit"
grep -q '^ProtectSystem=strict$' "$source_unit"

echo "systemd unit syntax, executable paths, and baseline hardening verification passed."
