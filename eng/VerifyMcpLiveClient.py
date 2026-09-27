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
paste the values here. The credential needs instance.read, records.read, records.write, structure.write,
views.manage, backups.read, backups.write, backups.export, admin.read and admin.manage.
It must NOT hold records.delete: two checks depend on a permission it lacks.

The base URL defaults to http://localhost:5098. Point it at a separately launched
published Release process on a *fresh* data root: the script installs a preset and
creates records, and its idempotency keys are fixed, so a second run against the same
data root will fail on replay rather than on anything real.

Checks cover contracts 1.29 to 1.37. A later revision should add to the end rather than
replace, so the evidence accumulates.
"""

import base64
import datetime
import hashlib
import json
import sys
import time
import urllib.error
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

    # The surface rate-limits, as a deployment exposed to the internet should. A client that gives up
    # on the first 429 is a client that cannot finish a long session, so this waits and tries again --
    # which is also the behaviour the contract asks of one. The script grew past the window when the
    # structure lifecycle was added, so this is load-bearing rather than defensive.
    for attempt in range(8):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                body = response.read().decode("utf-8")
            break
        except urllib.error.HTTPError as failure:
            if failure.code != 429 or attempt == 7:
                raise
            delay = failure.headers.get("Retry-After")
            time.sleep(float(delay) if delay and delay.isdigit() else 2.0 * (attempt + 1))

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
ok("115 tools registered", len(names) == 115, len(names))

info = structured(call("get_instance_info"), "get_instance_info")
ok("contract 1.37 reported", info["contractVersion"] == "1.37", info["contractVersion"])
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

print("Record-type lifecycle (1.32)")
spare = structured(call("create_record_type", {
    "domainId": domain, "name": "Gate spare", "symbol": "\u2699",
    "idempotencyKey": "77777777-7777-4777-8777-777777777777"}), "create_record_type")
spare_id = spare["items"][0]["id"]
spare_revision = spare["items"][0]["revision"]

renamed = structured(call("update_record_type", {
    "domainId": domain, "recordTypeId": spare_id, "expectedRevision": spare_revision,
    "name": "  Gate spare renamed  ", "symbol": "\u2699", "tagsEnabled": False,
    "idempotencyKey": "77777777-7777-4777-8777-777777777778"}), "update_record_type")
ok("a record type renames and reports a new revision",
   renamed["items"][0]["revision"] != spare_revision, renamed)
listed_types = structured(call("list_record_types", {"domainId": domain}), "list_record_types")
ok("the trimmed name is what the application stored",
   next(e["name"] for e in listed_types if e["id"] == spare_id) == "Gate spare renamed",
   [e["name"] for e in listed_types])

retirement = structured(call("preview_record_type_retirement", {
    "domainId": domain, "recordTypeId": spare_id}), "preview_record_type_retirement")
ok("an empty type previews as carrying nothing",
   retirement["recordCount"] == 0 and retirement["savedViewCount"] == 0, retirement)
ok("retiring a type that has moved on is refused as staleness, not as a bad request",
   error_code(call("retire_record_type", {
       "domainId": domain, "recordTypeId": spare_id, "expectedUsageRevision": "0" * 64,
       "idempotencyKey": "77777777-7777-4777-8777-777777777779"}), "stale retirement") == "stale_revision")
retired = structured(call("retire_record_type", {
    "domainId": domain, "recordTypeId": spare_id,
    "expectedUsageRevision": retirement["expectedUsageRevision"],
    "idempotencyKey": "77777777-7777-4777-8777-77777777777a"}), "retire_record_type")
ok("retirement reports itself as a retirement", retired["items"][0]["outcome"] == "retired", retired)

merge_source = structured(call("create_record_type", {
    "domainId": domain, "name": "Gate merge source",
    "idempotencyKey": "88888888-8888-4888-8888-888888888881"}), "create_record_type")
merge_source_id = merge_source["items"][0]["id"]
merge_target = structured(call("create_record_type", {
    "domainId": domain, "name": "Gate merge target",
    "idempotencyKey": "88888888-8888-4888-8888-888888888882"}), "create_record_type")
merge_target_id = merge_target["items"][0]["id"]
moved = structured(call("create_record", {
    "domainId": domain, "recordTypeId": merge_source_id, "displayName": "Moved by a merge",
    "values": [], "idempotencyKey": "88888888-8888-4888-8888-888888888883"}), "create_record")
moved_id = moved["items"][0]["id"]

merge_preview = structured(call("preview_record_type_merge", {
    "domainId": domain, "sourceRecordTypeId": merge_source_id, "targetRecordTypeId": merge_target_id},
), "preview_record_type_merge")
ok("the merge preview counts the record it would move", merge_preview["sourceRecordCount"] == 1, merge_preview)
merged = structured(call("merge_record_types", {
    "domainId": domain, "sourceRecordTypeId": merge_source_id, "targetRecordTypeId": merge_target_id,
    "expectedUsageRevision": merge_preview["expectedUsageRevision"],
    "idempotencyKey": "88888888-8888-4888-8888-888888888884"}), "merge_record_types")
ok("a merge retires the source and updates the target",
   [item["outcome"] for item in merged["items"]] == ["retired", "updated"], merged)
ok("and the record now belongs to the target",
   structured(call("get_record", {"domainId": domain, "id": moved_id}),
              "get_record")["record"]["recordTypeId"] == merge_target_id)

print("Field lifecycle (1.33)")
usage = structured(call("get_field_usage", {"domainId": domain, "fieldDefinitionId": city_id}), "get_field_usage")
ok("field usage counts the two values recorded against it", usage["valueCount"] == 2, usage)
ok("and the view that names it", usage["savedViewReferenceCount"] >= 1, usage)
ok("usage names no record and carries no value", "London" not in json.dumps(usage), json.dumps(usage))

field_revision = usage["definition"]["revision"]
renamed_field = structured(call("rename_field", {
    "domainId": domain, "fieldDefinitionId": city_id, "expectedFieldRevision": field_revision,
    "name": "  Gate city renamed  ", "idempotencyKey": "99999999-9999-4999-8999-999999999991"}), "rename_field")
ok("renaming a field reports a new revision",
   renamed_field["items"][0]["revision"] != field_revision, renamed_field)
ok("a superseded field revision is refused as staleness",
   error_code(call("rename_field", {
       "domainId": domain, "fieldDefinitionId": city_id, "expectedFieldRevision": field_revision,
       "name": "Never applied", "idempotencyKey": "99999999-9999-4999-8999-999999999992"}),
              "stale rename") == "stale_revision")

# A text field holding "London" and "Leeds" cannot become a number, which is exactly what a
# conversion preview is for: say so before anything is written.
unsafe = structured(call("preview_field_conversion", {
    "domainId": domain, "fieldDefinitionId": city_id, "name": "Gate city", "typeId": "number"},
), "preview_field_conversion")
ok("a conversion that would lose values says how many", unsafe["failedValueCount"] == 2, unsafe)
ok("and names them, because this credential can read records",
   unsafe["issueRecordsWithheld"] is False and len(unsafe["issues"]) == 2, unsafe)
ok("applying it is refused rather than dropping them",
   error_code(call("convert_field", {
       "domainId": domain, "fieldDefinitionId": city_id,
       "expectedUsageRevision": unsafe["expectedUsageRevision"], "name": "Gate city", "typeId": "number",
       "idempotencyKey": "99999999-9999-4999-8999-999999999993"}), "unsafe conversion") == "validation_failed")

# Multiline text takes any text, so this one can carry every value across.
safe = structured(call("preview_field_conversion", {
    "domainId": domain, "fieldDefinitionId": city_id, "name": "Gate city notes",
    "typeId": "multiline-text"}), "preview_field_conversion")
ok("a conversion that can carry every value says so", safe["failedValueCount"] == 0, safe)
converted = structured(call("convert_field", {
    "domainId": domain, "fieldDefinitionId": city_id,
    "expectedUsageRevision": safe["expectedUsageRevision"], "name": "Gate city notes",
    "typeId": "multiline-text", "idempotencyKey": "99999999-9999-4999-8999-999999999994"}), "convert_field")
ok("a conversion creates the new field and retires the original",
   [item["outcome"] for item in converted["items"]] == ["created", "retired"], converted)
converted_id = converted["items"][0]["id"]
ada_after = structured(call("get_record", {"domainId": domain, "id": ada_id}), "get_record")
ok("and the value came across under the new field, unchanged",
   any(value["fieldDefinitionId"] == converted_id and value["value"] == "London"
       and value["typeId"] == "multiline-text" for value in ada_after["values"]), ada_after["values"])

# Two fields of one type and configuration, so they can merge at all.
first_id = add_field("Gate nickname", "text", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1")
second_id = add_field("Gate alias", "text", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2")
field_merge = structured(call("preview_field_merge", {
    "domainId": domain, "sourceFieldDefinitionId": first_id, "targetFieldDefinitionId": second_id},
), "preview_field_merge")
ok("two fields of one type preview as compatible", field_merge["isCompatible"] is True, field_merge)
ok("a field merge's fingerprint is not one field's own",
   field_merge["expectedUsageRevision"] != structured(
       call("get_field_usage", {"domainId": domain, "fieldDefinitionId": first_id}),
       "get_field_usage")["expectedUsageRevision"])
field_merged = structured(call("merge_fields", {
    "domainId": domain, "sourceFieldDefinitionId": first_id, "targetFieldDefinitionId": second_id,
    "conflictResolution": "keepTarget", "expectedUsageRevision": field_merge["expectedUsageRevision"],
    "idempotencyKey": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa3"}), "merge_fields")
ok("a field merge retires the source and updates the target",
   [item["outcome"] for item in field_merged["items"]] == ["retired", "updated"], field_merged)

retire_me = add_field("Gate retired field", "text", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa4")
retire_revision = next(
    field["revision"] for field in
    structured(call("list_field_definitions", {"domainId": domain, "pageSize": 100}),
               "list_field_definitions")["items"] if field["id"] == retire_me)
retired_field = structured(call("retire_field", {
    "domainId": domain, "fieldDefinitionId": retire_me, "expectedFieldRevision": retire_revision,
    "idempotencyKey": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa5"}), "retire_field")
ok("retiring a field reports itself as a retirement",
   retired_field["items"][0]["outcome"] == "retired", retired_field)

print("Relationship-type lifecycle (1.34)")
link = structured(call("create_relationship_type", {
    "domainId": domain, "name": "Gate employs", "directionality": "directional",
    "inverseName": "Gate works for", "idempotencyKey": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1"},
), "create_relationship_type")
link_id = link["items"][0]["id"]
link_revision = link["items"][0]["revision"]
relabelled = structured(call("rename_relationship_type", {
    "domainId": domain, "typeId": link_id, "expectedRevision": link_revision,
    "name": "  Gate employer of  ", "inverseName": "  Gate employed by  ",
    "idempotencyKey": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2"}), "rename_relationship_type")
ok("relabelling a relationship type reports a new revision",
   relabelled["items"][0]["revision"] != link_revision, relabelled)
relationship_types = structured(call("list_relationship_types", {"domainId": domain}), "list_relationship_types")
current_link = next(entry for entry in relationship_types["items"] if entry["id"] == link_id)
ok("both labels were trimmed by the application",
   (current_link["name"], current_link["inverseName"]) == ("Gate employer of", "Gate employed by"), current_link)
ok("a directional type cannot lose its inverse label",
   error_code(call("rename_relationship_type", {
       "domainId": domain, "typeId": link_id, "expectedRevision": current_link["revision"],
       "name": "Gate employer of", "idempotencyKey": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb3"}),
              "inverse-less directional rename") == "validation_failed")
relationship_retired = structured(call("retire_relationship_type", {
    "domainId": domain, "typeId": link_id, "expectedRevision": current_link["revision"],
    "idempotencyKey": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb4"}), "retire_relationship_type")
ok("retiring a relationship type reports itself as a retirement",
   relationship_retired["items"][0]["outcome"] == "retired", relationship_retired)
ok("and retiring it twice is refused",
   error_code(call("retire_relationship_type", {
       "domainId": domain, "typeId": link_id,
       "expectedRevision": relationship_retired["items"][0]["revision"],
       "idempotencyKey": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb5"}), "second retirement") == "validation_failed")

print("Operational status and backups (1.35, 1.36)")
status = structured(call("get_operational_status"), "get_operational_status")
ok("the deployment reports itself ready", status["ready"] is True, status)
ok("and reports no host detail", "\\" not in json.dumps(status) and "/var/" not in json.dumps(status), status)
ok("the schedule is reported read-only", "frequency" in status["schedule"], status["schedule"])
before_count = status["backupCount"]

taken = structured(call("create_backup"), "create_backup")
ok("a backup reports an unambiguous completion", taken["byteLength"] > 0 and taken["id"], taken)
listed = structured(call("list_backups"), "list_backups")
ok("and appears in the list exactly once",
   len([b for b in listed if b["id"] == taken["id"]]) == 1, listed)
ok("the status count moved with it",
   structured(call("get_operational_status"), "get_operational_status")["backupCount"] == before_count + 1)

checked = structured(call("validate_backup", {"id": taken["id"]}), "validate_backup")
ok("validation opens the package and reports its schema",
   checked["applicationSchemaVersion"] == info["databaseSchemaVersion"], checked)
ok("validating a package that does not exist is not_found, not a retry suggestion",
   error_code(call("validate_backup", {"id": "00000000-0000-4000-8000-0000000000ff"}),
              "absent backup") == "not_found")

# Paged deliberately small, so the offsets are exercised rather than the package arriving at once.
rebuilt = bytearray()
offset, chunks, digest = 0, 0, None
while offset is not None:
    chunk = structured(call("read_backup", {"id": taken["id"], "offset": offset, "count": 8192,
                                            "includeDigest": chunks == 0}), "read_backup")
    if chunks == 0:
        digest = chunk["contentDigest"]
    rebuilt.extend(base64.b64decode(chunk["contentBase64"]))
    offset, chunks = chunk["nextOffset"], chunks + 1
ok("the package pages in more than one range", chunks > 1, chunks)
ok("the ranges reassemble to the reported length", len(rebuilt) == taken["byteLength"], len(rebuilt))
ok("and to the digest the first range reported",
   hashlib.sha256(bytes(rebuilt)).hexdigest().upper() == digest, digest)
ok("an offset past the end is refused",
   error_code(call("read_backup", {"id": taken["id"], "offset": taken["byteLength"] + 1, "count": 8192}),
              "past the end") == "validation_failed")

print("Remote administration (1.37)")
access = structured(call("get_remote_access_state"), "get_remote_access_state")
ok("remote access reports itself enabled and the MCP surface active",
   access["enabled"] is True and access["mcp"]["isActive"] is True, access)
ok("the credential is identified by suffix, not disclosed",
   access["mcp"]["credentialEnding"] and secret not in json.dumps(access))
ok("the endpoint this call arrived on is the one reported",
   access["mcp"]["endpointPath"] == endpoint, access["mcp"]["endpointPath"])

activity = structured(call("list_remote_activity", {"limit": 25}), "list_remote_activity")
ok("redacted activity is returned and bounded", activity["returned"] > 0 and activity["limit"] == 25)
ok("and carries no credential", secret not in json.dumps(activity))
ok("an unbounded limit is refused",
   error_code(call("list_remote_activity", {"limit": 500}), "unbounded activity") == "validation_failed")

held = sorted(access["mcp"]["scopes"])
ok("this credential does not hold records.delete, so the next check means something",
   "records.delete" not in held, held)
ok("a rotation cannot grant a permission this credential lacks",
   error_code(call("rotate_remote_credential",
                   {"surface": "mcp", "scopes": held + ["records.delete"]}),
              "widening rotation") == "permission_denied")
ok("and nothing was rotated by the refusal",
   sorted(structured(call("get_remote_access_state"), "get_remote_access_state")["mcp"]["scopes"]) == held)

# The API surface is a different connection, so acting on it must say so rather than warn about nothing.
elsewhere = structured(call("set_remote_activation", {"surface": "api", "active": False}), "set_remote_activation")
ok("changing the other surface reports no effect on this connection",
   elsewhere["affectsThisConnection"] is False, elsewhere["disclosure"])

# Last, because it replaces the credential this script is using. Narrowing is permitted, and the new
# secret has to work while the old one stops: that is the whole rotation contract in two calls.
rotated = structured(call("rotate_remote_credential", {"surface": "mcp", "scopes": held}), "rotate_remote_credential")
ok("rotating this surface discloses that it affects this connection",
   rotated["affectsThisConnection"] is True and "stop working" in rotated["disclosure"], rotated["disclosure"])
ok("and returns the new secret once", bool(rotated.get("credential")))

superseded, secret = secret, rotated["credential"]
ok("the new credential works", structured(call("get_operational_status"), "get_operational_status")["ready"] is True)
secret = superseded
try:
    replaced = call("get_operational_status")
    ok("the superseded credential no longer works", replaced.get("result", {}).get("isError") is True, replaced)
except urllib.error.HTTPError as rejected:
    ok("the superseded credential no longer works", rejected.code in (401, 403), rejected.code)
secret = rotated["credential"]

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
