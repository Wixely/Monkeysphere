#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
engine=${CONTAINER_ENGINE:-podman}
port=${MONKEYSPHERE_VERIFY_PORT:-15086}
verification_id=$$
image_name="monkeysphere-verification:$verification_id"
container_name="monkeysphere-verification-$verification_id"
volume_name="monkeysphere-verification-$verification_id-data"

case "$engine" in
    docker|podman) ;;
    *) echo "CONTAINER_ENGINE must be docker or podman." >&2; exit 2 ;;
esac
case "$port" in
    *[!0-9]*|'') echo "MONKEYSPHERE_VERIFY_PORT must be numeric." >&2; exit 2 ;;
esac
if [ "$port" -lt 1024 ] || [ "$port" -gt 65535 ]; then
    echo "MONKEYSPHERE_VERIFY_PORT must be between 1024 and 65535." >&2
    exit 2
fi
for command_name in "$engine" curl sed grep mktemp; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command is unavailable: $command_name" >&2
        exit 2
    fi
done

working_root=$(mktemp -d -t monkeysphere-oci-verify-XXXXXX)
cookie_jar="$working_root/cookies.txt"
login_page="$working_root/login.html"
private_page="$working_root/private.html"
base_uri="http://127.0.0.1:$port"

cleanup() {
    "$engine" rm --force "$container_name" >/dev/null 2>&1 || true
    "$engine" volume rm --force "$volume_name" >/dev/null 2>&1 || true
    "$engine" image rm --force "$image_name" >/dev/null 2>&1 || true
    case "$working_root" in
        /tmp/monkeysphere-oci-verify-*) rm -rf -- "$working_root" ;;
        *) echo "Refusing to remove unexpected verification path: $working_root" >&2 ;;
    esac
}
trap cleanup EXIT HUP INT TERM

start_container() {
    "$engine" run --detach \
        --name "$container_name" \
        --publish "$port:8080" \
        --volume "$volume_name:/data" \
        --env MONKEYSPHERE_ADMIN_USERNAME=admin \
        --env MONKEYSPHERE_ADMIN_PASSWORD=admin \
        --env DnaX__RemoteAccess__Enabled=false \
        "$image_name" >/dev/null
}

smoke_http() {
    attempt=0
    until curl --fail --silent --show-error "$base_uri/health/ready" >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "$attempt" -ge 180 ] || [ "$("$engine" inspect --format '{{.State.Running}}' "$container_name" 2>/dev/null || true)" != true ]; then
            "$engine" logs "$container_name" >&2 || true
            echo "The OCI container did not become ready at $base_uri." >&2
            exit 1
        fi
        sleep 1
    done

    curl --fail --silent --show-error --cookie-jar "$cookie_jar" "$base_uri/login" >"$login_page"
    token=$(sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' "$login_page" | head -n 1)
    if [ -z "$token" ]; then
        echo "The container login page did not contain an antiforgery token." >&2
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
        "$engine" logs "$container_name" >&2 || true
        echo "The container administrator could not reach the authenticated setup surface." >&2
        exit 1
    fi
}

"$engine" build --tag "$image_name" "$repository_root"
"$engine" volume create "$volume_name" >/dev/null
start_container
smoke_http

if [ "$("$engine" exec "$container_name" id -u)" != 1654 ]; then
    echo "The runtime container is not running as the expected non-root user 1654." >&2
    exit 1
fi
"$engine" exec "$container_name" sh -c 'test -s /data/monkeysphere.db && test -s /data/remote-access.db'

"$engine" rm --force "$container_name" >/dev/null
start_container
smoke_http
"$engine" exec "$container_name" sh -c 'test -s /data/monkeysphere.db && test -s /data/remote-access.db'

echo "$engine OCI image build, non-root runtime, login, recreation, and persistent-volume smoke passed."
