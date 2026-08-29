#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
port=${MONKEYSPHERE_VERIFY_PORT:-15083}
project_name=${MONKEYSPHERE_VERIFY_PROJECT:-monkeysphere-verify-$$}

case "$project_name" in
    monkeysphere-verify-?*) ;;
    *) echo "Refusing unsafe Docker Compose project name: $project_name" >&2; exit 2 ;;
esac
case "$project_name" in
    *[!a-zA-Z0-9_-]*) echo "Refusing unsafe Docker Compose project name: $project_name" >&2; exit 2 ;;
esac
case "$port" in
    *[!0-9]*|'') echo "MONKEYSPHERE_VERIFY_PORT must be numeric." >&2; exit 2 ;;
esac
if [ "$port" -lt 1024 ] || [ "$port" -gt 65535 ]; then
    echo "MONKEYSPHERE_VERIFY_PORT must be between 1024 and 65535." >&2
    exit 2
fi

for command_name in docker curl sed grep mktemp; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command is unavailable: $command_name" >&2
        exit 2
    fi
done
docker compose version >/dev/null

working_root=$(mktemp -d -t monkeysphere-docker-verify-XXXXXX)
cookie_jar="$working_root/cookies.txt"
login_page="$working_root/login.html"
private_page="$working_root/private.html"
base_uri="http://127.0.0.1:$port"

compose() {
    MONKEYSPHERE_PORT="$port" docker compose --project-directory "$repository_root" -p "$project_name" "$@"
}

cleanup() {
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    case "$working_root" in
        /tmp/monkeysphere-docker-verify-*) rm -rf -- "$working_root" ;;
        *) echo "Refusing to remove unexpected verification path: $working_root" >&2 ;;
    esac
}
trap cleanup EXIT HUP INT TERM

smoke_http() {
    attempt=0
    until curl --fail --silent --show-error "$base_uri/health/ready" >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "$attempt" -ge 180 ]; then
            compose logs --no-color >&2
            echo "The Docker deployment did not become ready at $base_uri." >&2
            exit 1
        fi
        sleep 1
    done

    curl --fail --silent --show-error --cookie-jar "$cookie_jar" "$base_uri/login" >"$login_page"
    token=$(sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' "$login_page" | head -n 1)
    if [ -z "$token" ]; then
        echo "The Docker login page did not contain an antiforgery token." >&2
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
        compose logs --no-color >&2
        echo "The Docker administrator could not reach the authenticated setup surface." >&2
        exit 1
    fi
}

compose config --quiet
compose up --build --detach
smoke_http
compose exec -T monkeysphere sh -c 'test -s /data/monkeysphere.db && test -s /data/remote-access.db'
compose stop
compose start
smoke_http
compose exec -T monkeysphere sh -c 'test -s /data/monkeysphere.db && test -s /data/remote-access.db'

echo "Docker restart and persistent-volume smoke passed at $base_uri."
