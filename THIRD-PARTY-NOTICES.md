# Third-party notices

Monkeysphere is licensed under the MIT License. Direct runtime and test dependencies retain their own licenses.

## DnaX 10.0.0-alpha.3

The repository-local packages in `eng/packages` were built without modification from public tag `v10.0.0-alpha.3`, commit `af960fa02bb1e7e0ff22850426d899f6559cf64f`, in <https://github.com/Wixely/DnaX>. DnaX is MIT licensed. The packages are vendored because this release is not indexed on NuGet.org.

| Package | SHA-256 |
| --- | --- |
| `DnaX.Data.Migrations` | `550D81AFB35A3BC1F5911EE06025E89BD93C352DAF33645CFF04547C17B48573` |
| `DnaX.Data.Migrations.Sqlite` | `C68C42DFB0F06DFBC5783717F38D5D691ECC1EFAF38BAA138C87DC97EE9BB048` |
| `DnaX.Data.Migrations.Sqlite.Testing` | `C1A7525B749CBC0FE6EEC11C96F35476F76121A0643FB3B7909D704E54006557` |
| `DnaX.Hosting` | `CE127CBF95184EFB0151414F15D4159C5BB4A9A5054D0CCF718C5B01D7AFBE40` |
| `DnaX.RemoteAccess` | `43D960E17800AE267D04915C8B032E60214F5E904DC87E6CA5215A49BA9CEA2F` |
| `DnaX.RemoteAccess.Mcp` | `79486D17D036A4B8BF7E5B7F53E78F39899175DEB78F00DED95848B904744732` |
| `DnaX.RemoteAccess.Sqlite` | `C80245B991659C83B82DF22D6A7A211062A4D3DE8C952D6DBCE75BA5CCBBE553` |

DnaX remote MCP support depends on the official Model Context Protocol C# SDK, licensed under Apache-2.0, and Microsoft.Extensions.AI.Abstractions, licensed under MIT.

## Other direct dependencies

- Dapper — Apache-2.0, <https://github.com/DapperLib/Dapper>
- Microsoft ASP.NET Core, Microsoft.Data.Sqlite, and Microsoft.Extensions hosting packages — MIT, <https://github.com/dotnet/aspnetcore> and <https://github.com/dotnet/runtime>
- SQLite — public domain, <https://www.sqlite.org/copyright.html>
- SQLitePCLRaw managed and bundling packages — Apache-2.0; the bundled SQLite native library remains public domain, <https://github.com/ericsink/SQLitePCL.raw>
- SkiaSharp — MIT, <https://github.com/mono/SkiaSharp>
- xUnit.net and runner — Apache-2.0, <https://github.com/xunit/xunit>
- Microsoft.NET.Test.Sdk — MIT, <https://github.com/microsoft/vstest>
- coverlet.collector — MIT, <https://github.com/coverlet-coverage/coverlet>

The transitive runtime package graph was reviewed from restored NuGet package metadata on 2026-08-29. It contains only the DnaX, Dapper, Microsoft .NET/ASP.NET Core, Model Context Protocol, SQLitePCLRaw/SQLite, and SkiaSharp families listed above. The transitive test graph adds Microsoft Test Platform (MIT) and xUnit.net components (Apache-2.0). See `docs/dependencies.md` for the repeatable hash and advisory checks.

## Vendored browser assets

OpenLayers 10.10.0 is vendored from the official release package at <https://github.com/openlayers/openlayers/releases/tag/v10.10.0> and is licensed under BSD-2-Clause. Its license is retained beside the assets in `src/Monkeysphere.Web/wwwroot/vendor/openlayers/10.10.0/LICENSE.md`. Monkeysphere uses the full hosted build without GeoTIFF or mapbox-style integrations and does not use an npm or Node.js build step.

| File | SHA-256 |
| --- | --- |
| `ol.js` | `B89AF8EC3B76F564D515FD07FED3EC414AECF8F33F685B77B607451CB0C2029F` |
| `ol.css` | `ABC8AFD72CC10BD29CC143F443BAE4A6804BD3CB3FB262E6B6A6BC6C924EA34F` |
| `LICENSE.md` | `6C4347B83A8C9FEEF18D57B18E3B6C44CF901B3C344A4A1FBD837E421555AB8E` |

Cytoscape.js 3.34.0 is vendored from the official release tag at <https://github.com/cytoscape/cytoscape.js/releases/tag/v3.34.0> and is MIT licensed. Its license is retained beside the browser build in `src/Monkeysphere.Web/wwwroot/vendor/cytoscape/3.34.0/LICENSE`. Monkeysphere consumes the published minified build directly and does not use an npm or Node.js build step.

| File | SHA-256 |
| --- | --- |
| `cytoscape.min.js` | `9C2A3BF2592E0B14A1F7BEC07C03A54F16DEDF32AF9CD0AF155C716AA6C87BC3` |
| `LICENSE` | `EB319C6E6F233607F71E8E2F450391751883CFC0EEB3CA7EF574C13D1D9C2203` |
