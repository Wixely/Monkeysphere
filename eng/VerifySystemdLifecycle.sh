#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
    echo "Usage: $0 <published-directory>" >&2
    exit 2
fi

package_root=$(CDPATH= cd -- "$1" && pwd)
application_dll="$package_root/Monkeysphere.Web.dll"
application_host="$package_root/Monkeysphere.Web"
if [ ! -f "$application_dll" ]; then
    echo "Published directory does not contain Monkeysphere.Web.dll: $package_root" >&2
    exit 2
fi

for command_name in systemctl systemd-run curl sed grep mktemp; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command is unavailable: $command_name" >&2
        exit 2
    fi
done
if ! systemctl --user is-system-running >/dev/null 2>&1; then
    echo "The per-user systemd manager is not running." >&2
    exit 2
fi

if command -v dotnet >/dev/null 2>&1; then
    launch_kind=framework-dependent
elif [ -x "$application_host" ]; then
    launch_kind=self-contained
else
    echo "Neither a .NET runtime nor an executable self-contained Monkeysphere host is available." >&2
    exit 2
fi

runtime_parent=${XDG_RUNTIME_DIR:-}
if [ -z "$runtime_parent" ] || [ ! -d "$runtime_parent" ]; then
    echo "XDG_RUNTIME_DIR is unavailable for the private-temporary-directory service check." >&2
    exit 2
fi
working_root=$(mktemp -d "$runtime_parent/monkeysphere-systemd-lifecycle-XXXXXX")
data_root="$working_root/data"
password_file="$working_root/admin-password"
cookie_jar="$working_root/cookies.txt"
login_page="$working_root/login.html"
private_page="$working_root/private.html"
unit_name="monkeysphere-verification-$$"
port=${MONKEYSPHERE_VERIFY_PORT:-15085}
case "$port" in
    *[!0-9]*|'') echo "MONKEYSPHERE_VERIFY_PORT must be numeric." >&2; exit 2 ;;
esac
if [ "$port" -lt 1024 ] || [ "$port" -gt 65535 ]; then
    echo "MONKEYSPHERE_VERIFY_PORT must be between 1024 and 65535." >&2
    exit 2
fi
base_uri="http://127.0.0.1:$port"
mkdir -p "$data_root"
printf '%s\n' admin >"$password_file"
chmod 0600 "$password_file"

cleanup() {
    systemctl --user stop "$unit_name.service" >/dev/null 2>&1 || true
    systemctl --user reset-failed "$unit_name.service" >/dev/null 2>&1 || true
    case "$working_root" in
        "$runtime_parent"/monkeysphere-systemd-lifecycle-*) rm -rf -- "$working_root" ;;
        *) echo "Refusing to remove unexpected verification path: $working_root" >&2 ;;
    esac
}
trap cleanup EXIT HUP INT TERM

start_unit() {
    if [ "$launch_kind" = framework-dependent ]; then
        systemd-run --user --unit "$unit_name" \
            --property Type=notify \
            --property NoNewPrivileges=true \
            --property PrivateTmp=true \
            --property "WorkingDirectory=$package_root" \
            --setenv ASPNETCORE_ENVIRONMENT=Production \
            --setenv "MONKEYSPHERE_DATA_ROOT=$data_root" \
            --setenv MONKEYSPHERE_ADMIN_USERNAME=admin \
            --setenv "MONKEYSPHERE_ADMIN_PASSWORD_FILE=$password_file" \
            dotnet "$application_dll" --urls "$base_uri"
    else
        systemd-run --user --unit "$unit_name" \
            --property Type=notify \
            --property NoNewPrivileges=true \
            --property PrivateTmp=true \
            --property "WorkingDirectory=$package_root" \
            --setenv ASPNETCORE_ENVIRONMENT=Production \
            --setenv "MONKEYSPHERE_DATA_ROOT=$data_root" \
            --setenv MONKEYSPHERE_ADMIN_USERNAME=admin \
            --setenv "MONKEYSPHERE_ADMIN_PASSWORD_FILE=$password_file" \
            "$application_host" --urls "$base_uri"
    fi
}

smoke_http() {
    attempt=0
    until curl --fail --silent --show-error "$base_uri/health/ready" >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "$attempt" -ge 120 ] || ! systemctl --user is-active --quiet "$unit_name.service"; then
            journalctl --user-unit "$unit_name.service" --no-pager -n 100 >&2 || true
            echo "The systemd service did not become ready at $base_uri." >&2
            exit 1
        fi
        sleep 0.5
    done

    curl --fail --silent --show-error --cookie-jar "$cookie_jar" "$base_uri/login" >"$login_page"
    token=$(sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' "$login_page" | head -n 1)
    if [ -z "$token" ]; then
        echo "The systemd-hosted login page did not contain an antiforgery token." >&2
        exit 1
    fi
    curl --fail --silent --show-error --location \
        --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
        --data-urlencode "__RequestVerificationToken=$token" \
        --data-urlencode 'username=admin' \
        --data-urlencode 'password=admin' \
        --data-urlencode 'returnUrl=/setup' \
        "$base_uri/auth/login" >"$private_page"
    if ! grep -Eq 'What would you like to remember\?|Setup is complete' "$private_page"; then
        echo "The systemd-hosted administrator could not reach the authenticated setup surface." >&2
        exit 1
    fi
}

start_unit
smoke_http
systemctl --user stop "$unit_name.service"

if [ ! -s "$data_root/monkeysphere.db" ] || [ ! -s "$data_root/remote-access.db" ]; then
    echo "The systemd-hosted application did not create both managed SQLite databases." >&2
    exit 1
fi

start_unit
smoke_http
systemctl --user show "$unit_name.service" --property Type --property ActiveState --property MainPID --no-pager

echo "systemd $launch_kind start, stop, restart, notify-readiness, login, and persistent-data smoke passed."
