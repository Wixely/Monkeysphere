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

DnaX remote MCP support depends on the official Model Context Protocol C# SDK, licensed under Apache-2.0.

## Other direct dependencies

- Dapper — Apache-2.0, <https://github.com/DapperLib/Dapper>
- Microsoft ASP.NET Core, Microsoft.Data.Sqlite, and Microsoft.Extensions hosting packages — MIT, <https://github.com/dotnet/aspnetcore> and <https://github.com/dotnet/runtime>
- SQLite — public domain, <https://www.sqlite.org/copyright.html>
- SkiaSharp — MIT, <https://github.com/mono/SkiaSharp>
- xUnit.net and runner — Apache-2.0, <https://github.com/xunit/xunit>
- Microsoft.NET.Test.Sdk — MIT, <https://github.com/microsoft/vstest>
- coverlet.collector — MIT, <https://github.com/coverlet-coverage/coverlet>
