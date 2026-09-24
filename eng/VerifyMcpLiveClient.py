"""Drives a deployed Monkeysphere MCP surface as an external client.

This is the cross-cutting live-client gate the MCP management plan requires for each
contract revision, and it exists as a script rather than a test because the point is
to prove the surface works from outside: it imports nothing from Monkeysphere and
speaks only the transport docs/mcp-contract.md documents -- Streamable HTTP, revision
2026-07-28, the Mcp-Method and Mcp-Name headers, the _meta block. A test host proves
the tools; only this proves the deployment.

Usage:

    python eng/VerifyMcpLiveClient.py <credential-file> [base-url]

The credential file is two lines: the endpoint path, then the bearer secret. Both come
from the Remote access page when an administrator activates the MCP surface and selects
permissions -- which is also the interactive half of the gate, so do that first and
paste the values here. The credential needs instance.read, records.read, records.write,
structure.write and views.manage.

The base URL defaults to http://localhost:5098. Point it at a separately launched
published Release process on a *fresh* data root: the script installs a preset and
creates records, and its idempotency keys are fixed, so a second run against the same
data root will fail on replay rather than on anything real.

Checks cover contract 1.29. A later revision should add to the end rather than replace,
so the evidence accumulates.
"""

import base64
import datetime
import hashlib
import json
import sys
import urllib.request

BASE = sys.argv[2] if len(sys.argv) > 2 else "http://localhost:5098"
PROTOCOL = "2026-07-28"

with open(sys.argv[1], encoding="utf-8") as handle:
    endpoint = handle.readline().strip()
    secret = handle.readline().strip()

_id = [0]
failures = []


def call(tool, arguments=None, method="tools/call"):
    _id[0] += 1
    payload = {
        "jsonrpc": "2.0",
        "id": _id[0],
        "method": method,
        "params": {
            "_meta": {
                "io.modelcontextprotocol/protocolVersion": PROTOCOL,
                "io.modelcontextprotocol/clientCapabilities": {},
            },
        },
    }
    if method == "tools/call":
        payload["params"]["name"] = tool
        payload["params"]["arguments"] = arguments or {}

    headers = {
        "Authorization": "Bearer " + secret,
        "MCP-Protocol-Version": PROTOCOL,
        "Mcp-Method": method,
        "Accept": "application/json, text/event-stream",
        "Content-Type": "application/json",
    }
    if method == "tools/call":
        headers["Mcp-Name"] = tool

    request = urllib.request.Request(
        BASE + endpoint, data=json.dumps(payload).encode("utf-8"), headers=headers, method="POST")
    with urllib.request.urlopen(request, timeout=60) as response:
        body = response.read().decode("utf-8")

    # A streamed response arrives as an event-stream frame; read its data line.
    if body.lstrip().startswith("event:") or body.lstrip().startswith("data:"):
        for line in body.splitlines():
            if line.startswith("data:"):
                body = line[len("data:"):].strip()
                break
    return json.loads(body)


def ok(label, condition, detail=""):
    mark = "PASS" if condition else "FAIL"
    if not condition:
        failures.append(label)
    print(f"  [{mark}] {label}{(' -- ' + str(detail)) if detail and not condition else ''}")


def structured(response, label):
    result = response.get("result", {})
    if result.get("isError"):
        raise AssertionError(f"{label} failed: {json.dumps(result.get('structuredContent'))}")
    if "structuredContent" not in result:
        raise AssertionError(f"{label} returned no structured content: {json.dumps(result)[:400]}")
    return result["structuredContent"]


def error_code(response, label):
    result = response.get("result", {})
    if not result.get("isError"):
        raise AssertionError(f"{label} was expected to fail and did not: {json.dumps(result)[:300]}")
    return result["structuredContent"]["error"]["code"]


print("Transport and discovery")
listed = call(None, method="tools/list")
names = sorted(tool["name"] for tool in listed["result"]["tools"])
ok("tools/list succeeds over the randomized endpoint", len(names) > 0)
ok("90 tools registered", len(names) == 90, len(names))

info = structured(call("get_instance_info"), "get_instance_info")
ok("contract 1.29 reported", info["contractVersion"] == "1.29", info["contractVersion"])
ok("no host paths in instance info", "C:\\" not in json.dumps(info), json.dumps(info))

caps = structured(call("get_capabilities"), "get_capabilities")
reported = {tool["name"] for tool in caps["tools"]}
missing = sorted(set(names) - reported)
ok("every registered tool appears in get_capabilities", not missing, missing)
ok("savedViewLimits reported", caps.get("savedViewLimits", {}).get("maximumColumns") == 25)
ok("graphViewLimits reported", caps.get("graphViewLimits", {}).get("maximumDepth") == 3)
ok("calendarLimits reported", caps.get("calendarLimits", {}).get("maximumDaysInRange") == 367)
ok("reminderLimits reported", caps.get("reminderLimits", {}).get("maximumLeadDays") == 3650)
ok("mapLimits reported", caps.get("mapLimits", {}).get("maximumPageSize") == 500)
ok("views.manage granted", "views.manage" in caps["grantedScopes"])
domain = caps["defaultDomainId"]

print("Structure and records to view")
setup = structured(call("get_setup_state", {"domainId": domain}), "get_setup_state")
structured(call("install_preset", {
    "domainId": domain, "presetKey": "monkeysphere.person",
    "expectedRevision": setup["revision"], "expectedCatalogRevision": setup["catalogRevision"],
    "idempotencyKey": "00000000-0000-4000-8000-000000000001"}), "install_preset")
types = structured(call("list_record_types", {"domainId": domain}), "list_record_types")
person = next(entry for entry in types if entry["name"] == "Person")
type_id = person["id"]

# The preset's own Birthday, which declares that it comes round every year. A field created here
# would not, and the calendar's repeat projection would have nothing to do.
preset_birthday = next(field["id"] for field in person["fields"] if field["name"] == "Birthday")
def add_field(name, type_id_value, key):
    """Each attachment moves the type's revision on, so it is re-read rather than remembered."""
    current = structured(call("get_record_type", {"domainId": domain, "id": type_id}), "get_record_type")
    created = structured(call("create_and_attach_field", {
        "domainId": domain, "recordTypeId": type_id, "expectedRevision": current["revision"],
        "name": name, "typeId": type_id_value, "idempotencyKey": key}), "create_and_attach_field")
    return created["items"][0]["id"]


city_id = add_field("Gate city", "text", "22222222-2222-4222-8222-222222222222")

ada = structured(call("create_record", {
    "domainId": domain, "recordTypeId": type_id, "displayName": "Ada",
    "values": [{"fieldDefinitionId": city_id, "scalarValue": "London"},
               {"fieldDefinitionId": preset_birthday,
                "temporal": {"value": "1990-06-15", "precision": "day"}}],
    "tags": ["work", "london"],
    "idempotencyKey": "44444444-4444-4444-8444-444444444444"}), "create_record")
ada_id = ada["items"][0]["id"]
structured(call("create_record", {
    "domainId": domain, "recordTypeId": type_id, "displayName": "Grace",
    "values": [{"fieldDefinitionId": city_id, "scalarValue": "Leeds"}],
    "tags": ["work"],
    "idempotencyKey": "55555555-5555-4555-8555-555555555555"}), "create_record")

print("Saved views (1.25)")
view = structured(call("create_saved_view", {
    "domainId": domain, "name": "Working Londoners", "recordTypeId": type_id,
    "columnFieldDefinitionIds": [city_id],
    "filters": [{"fieldDefinitionId": city_id, "operator": "equals", "value": "London"}],
    "tags": ["  Work  ", "WORK", "london"], "showTags": True}), "create_saved_view")
ok("tags normalized by the application, not the caller", view["tags"] == ["Work", "london"], view["tags"])
fetched = structured(call("get_saved_view", {"domainId": domain, "id": view["id"]}), "get_saved_view")
ok("filter read back in the spelling it must be sent as",
   fetched["filters"][0]["operator"] == "equals", fetched["filters"])
ran = structured(call("run_saved_view", {"domainId": domain, "id": view["id"]}), "run_saved_view")
ok("view selects only the record carrying every tag", ran["totalCount"] == 1, ran["totalCount"])
ok("row carries the column value the view asked for",
   ran["items"][0]["values"][0]["value"] == "London", ran["items"][0]["values"])
ok("row carries the record's universal tags", sorted(ran["items"][0]["tags"]) == ["london", "work"],
   ran["items"][0]["tags"])
ok("a page of rows with values is bounded",
   error_code(call("run_saved_view", {"domainId": domain, "id": view["id"], "pageSize": 51}),
              "oversized run") == "validation_failed")

print("Graph views and queries (1.26)")
graph_view = structured(call("create_graph_view", {
    "domainId": domain, "name": "Arranged", "recordTypeIds": [type_id],
    "nodePositions": [{"recordId": ada_id, "x": -120.5, "y": 40}],
    "viewport": {"panX": 12, "panY": -34, "zoom": 1.5}}), "create_graph_view")
reopened = structured(call("get_graph_view", {"domainId": domain, "id": graph_view["id"]}), "get_graph_view")
ok("a hand-made arrangement survives exactly",
   reopened["nodePositions"][0]["x"] == -120.5 and reopened["viewport"]["zoom"] == 1.5,
   reopened)
drawn = structured(call("query_graph", {"domainId": domain}), "query_graph")
ok("an omitted record-type filter draws every active type", len(drawn["nodes"]) == 2, drawn["nodes"])
ok("the applied limits travel with the graph", drawn["appliedNodeLimit"] > 0, drawn)
ok("an explicitly empty record-type filter is refused",
   error_code(call("query_graph", {"domainId": domain, "recordTypeIds": []}), "empty types")
   == "validation_failed")
ok("a connected graph with nothing to be relative to is refused",
   error_code(call("query_graph", {"domainId": domain, "displayMode": "connected"}), "unanchored")
   == "validation_failed")
ok("a view and an explicit selection together are refused",
   error_code(call("query_graph", {"domainId": domain, "graphViewId": graph_view["id"],
                                   "displayMode": "all"}), "two selections") == "validation_failed")

print("Calendar and export (1.27)")
year = datetime.datetime.now(datetime.timezone.utc).year
span = {"domainId": domain, "from": f"{year}-06-01", "to": f"{year}-06-30"}
cal = structured(call("query_calendar", span), "query_calendar")
entry = next((item for item in cal["entries"] if item["recordDisplayName"] == "Ada"), None)
ok(f"the 1990 birthday appears in {year} as a repeat",
   entry is not None and entry["isRepeat"] and entry["yearsSince"] == year - 1990, cal)
ok("a range wider than 367 days is refused",
   error_code(call("query_calendar", {"domainId": domain, "from": f"{year}-01-01", "to": f"{year + 1}-01-04"}),
              "wide range") == "validation_failed")

first = structured(call("export_calendar", {
    "domainId": domain, "from": f"{year}-06-01", "to": f"{year}-06-30", "count": 64}), "export_calendar")
assembled = bytearray(base64.b64decode(first["contentBase64"]))
offset = first["nextOffset"]
stable = True
while offset is not None:
    page = structured(call("export_calendar", {
        "domainId": domain, "from": f"{year}-06-01", "to": f"{year}-06-30", "offset": offset, "count": 64,
        "generatedAtUtc": first["generatedAtUtc"]}), "export_calendar page")
    stable = stable and page["contentDigest"] == first["contentDigest"]
    assembled += base64.b64decode(page["contentBase64"])
    offset = page["nextOffset"]
ok("the digest holds across every page when the stamp is pinned", stable)
ok("the paged document reassembles to its own digest",
   hashlib.sha256(bytes(assembled)).hexdigest().upper() == first["contentDigest"],
   hashlib.sha256(bytes(assembled)).hexdigest().upper())
document = bytes(assembled).decode("utf-8")
ok("the document is iCalendar and names the day",
   document.startswith("BEGIN:VCALENDAR") and f"DTSTART;VALUE=DATE:{year}0615" in document,
   document[:200])

print("Reminders (1.28)")
reminder = structured(call("create_reminder", {
    "domainId": domain, "fieldValueId": entry["fieldValueId"], "leadDays": 7}), "create_reminder")
ok("the reminder says what it is for", reminder["entry"]["recordDisplayName"] == "Ada", reminder["entry"])
ok("the same value and lead time cannot be scheduled twice",
   error_code(call("create_reminder", {"domainId": domain, "fieldValueId": entry["fieldValueId"],
                                       "leadDays": 7}), "duplicate") == "validation_failed")
listed_reminders = structured(call("list_reminders", {"domainId": domain}), "list_reminders")
ok("the reminder is listed", len(listed_reminders) == 1, listed_reminders)
structured(call("dismiss_reminder", {"domainId": domain, "id": reminder["id"]}), "dismiss_reminder")
ok("dismissing twice reports not_found",
   error_code(call("dismiss_reminder", {"domainId": domain, "id": reminder["id"]}), "second dismiss")
   == "not_found")

print("Map, spatial and dashboard (1.29)")
settings = structured(call("get_map_settings", {"domainId": domain}), "get_map_settings")
ok("external tiles are off by default", settings["externalTilesEnabled"] is False)
ok("the disclosure is readable before the decision", "IP address" in settings["disclosure"])
ok("enabling without acknowledgement is refused",
   error_code(call("set_map_settings", {"domainId": domain, "externalTilesEnabled": True}),
              "unacknowledged") == "validation_failed")
enabled = structured(call("set_map_settings", {
    "domainId": domain, "externalTilesEnabled": True, "acknowledgeExternalRequests": True}),
    "set_map_settings")
ok("acknowledged enabling succeeds", enabled["externalTilesEnabled"] is True)
structured(call("set_map_settings", {"domainId": domain, "externalTilesEnabled": False}), "disable tiles")

pins = structured(call("query_map", {"domainId": domain}), "query_map")
ok("a map with no located records is empty rather than an error", pins["totalCount"] == 0, pins)

dashboard = structured(call("get_dashboard_settings", {"domainId": domain}), "get_dashboard_settings")
ok("the dashboard reports its default look-ahead and bounds",
   dashboard["upcomingDays"] == dashboard["defaultUpcomingDays"] and dashboard["maximumUpcomingDays"] == 366,
   dashboard)
narrowed = structured(call("set_dashboard_settings", {"domainId": domain, "upcomingDays": 30}),
                      "set_dashboard_settings")
ok("changing the look-ahead keeps the fields nobody mentioned",
   narrowed["recurringFieldDefinitionIds"] == dashboard["recurringFieldDefinitionIds"], narrowed)
upcoming = structured(call("list_upcoming_dates", {"domainId": domain}), "list_upcoming_dates")
ok("list_upcoming_dates answers without error", isinstance(upcoming, list), upcoming)

print("Grant separation on a live credential")
ok("delete_saved_view needs views.manage and this credential has it",
   next(t for t in caps["tools"] if t["name"] == "delete_saved_view")["allowed"] is True)
ok("a tool outside this credential's grants is reported as not allowed",
   next(t for t in caps["tools"] if t["name"] == "delete_record")["allowed"] is False)
ok("and calling it is refused",
   error_code(call("delete_record", {"domainId": domain, "id": ada_id, "expectedRevision": "x",
                                     "idempotencyKey": "66666666-6666-4666-8666-666666666666"}),
              "ungranted delete") == "permission_denied")

print()
if failures:
    print(f"FAILURES ({len(failures)}): " + "; ".join(failures))
    sys.exit(1)
print("All live-gate checks passed against the published process.")
