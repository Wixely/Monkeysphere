#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
    echo "Usage: $0 <published-directory>" >&2
    exit 2
fi

package_root=$(CDPATH= cd -- "$1" && pwd)
application="$package_root/Monkeysphere.Web.dll"
if [ ! -f "$application" ]; then
    echo "Published directory does not contain Monkeysphere.Web.dll: $package_root" >&2
    exit 2
fi

for command_name in dotnet curl sed grep mktemp; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command is unavailable: $command_name" >&2
        exit 2
    fi
done

working_root=$(mktemp -d -t monkeysphere-linux-verify-XXXXXX)
data_root="$working_root/data"
cookie_jar="$working_root/cookies.txt"
login_page="$working_root/login.html"
private_page="$working_root/private.html"
log_file="$working_root/monkeysphere.log"
port=${MONKEYSPHERE_VERIFY_PORT:-15082}
case "$port" in
    *[!0-9]*|'') echo "MONKEYSPHERE_VERIFY_PORT must be numeric." >&2; exit 2 ;;
esac
if [ "$port" -lt 1024 ] || [ "$port" -gt 65535 ]; then
    echo "MONKEYSPHERE_VERIFY_PORT must be between 1024 and 65535." >&2
    exit 2
fi
base_uri="http://127.0.0.1:$port"
application_pid=
mkdir -p "$data_root"

stop_application() {
    if [ -n "$application_pid" ] && kill -0 "$application_pid" 2>/dev/null; then
        kill "$application_pid"
        wait "$application_pid" || true
    fi
    application_pid=
}

cleanup() {
    stop_application
    case "$working_root" in
        /tmp/monkeysphere-linux-verify-*) rm -rf -- "$working_root" ;;
        *) echo "Refusing to remove unexpected verification path: $working_root" >&2 ;;
    esac
}
trap cleanup EXIT HUP INT TERM

start_application() {
    ASPNETCORE_ENVIRONMENT=Production \
    MONKEYSPHERE_DATA_ROOT="$data_root" \
    MONKEYSPHERE_ADMIN_USERNAME=admin \
    MONKEYSPHERE_ADMIN_PASSWORD=admin \
    dotnet "$application" --urls "$base_uri" >"$log_file" 2>&1 &
    application_pid=$!
}

smoke_http() {
    attempt=0
    until curl --fail --silent --show-error "$base_uri/health/ready" >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "$attempt" -ge 120 ] || ! kill -0 "$application_pid" 2>/dev/null; then
            cat "$log_file" >&2
            echo "Monkeysphere did not become ready at $base_uri." >&2
            exit 1
        fi
        sleep 0.5
    done

    curl --fail --silent --show-error --cookie-jar "$cookie_jar" "$base_uri/login" >"$login_page"
    token=$(sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' "$login_page" | head -n 1)
    if [ -z "$token" ]; then
        echo "The login page did not contain an antiforgery token." >&2
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
        echo "The administrator could not reach the authenticated setup surface." >&2
        exit 1
    fi
}

start_application
smoke_http
stop_application

if [ ! -s "$data_root/monkeysphere.db" ] || [ ! -s "$data_root/remote-access.db" ]; then
    echo "The Linux host did not create both managed SQLite databases." >&2
    exit 1
fi

start_application
smoke_http
stop_application

echo "Linux interactive restart and persistent-data smoke passed at $base_uri."
