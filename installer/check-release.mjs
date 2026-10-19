import { readFile, access } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';

const repo = fileURLToPath(new URL('../', import.meta.url));
const read = name => readFile(path.join(repo, name), 'utf8');
const pkg = JSON.parse(await read('server/package.json'));
const version = pkg.version;
assert.match(version, /^\d+\.\d+\.\d+$/);
for (const file of ['plugin/RevitMCP.csproj', 'installer/RevitMCP.Setup.csproj']) {
  const xml = await read(file);
  assert.equal(xml.match(/<Version>(.*?)<\/Version>/)?.[1], version, file);
  assert.equal(xml.match(/<Company>(.*?)<\/Company>/)?.[1], 'OOXGN', file);
  assert.equal(xml.match(/<Product>(.*?)<\/Product>/)?.[1], 'Revit MCP', file);
}
const deployment = await read('installer/Deployment.cs');
assert.equal(deployment.match(/const string Version = "(.*?)"/)?.[1], version);
const server = await read('server/src/index.js');
assert.equal(server.match(/version: '(.*?)'/)?.[1], version);
assert.equal(server.match(/name: '(.*?)'/)?.[1], 'revit-mcp');
const fallback = await read('build-fallback/AssemblyInfo.cs');
for (const property of ['AssemblyVersion', 'AssemblyFileVersion'])
  assert.equal(fallback.match(new RegExp(`${property}\\("(.*?)"\\)`))?.[1], version + '.0');
assert.equal(fallback.match(/AssemblyInformationalVersion\("(.*?)"\)/)?.[1], version);
assert.equal((await read('installer/app.manifest')).match(/assemblyIdentity version="(.*?)"/)?.[1], version + '.0');
const manual = await read('install.ps1');
const addinId = manual.match(/<AddInId>(.*?)<\/AddInId>/)?.[1];
assert.match(addinId, /^[0-9A-F-]{36}$/);
assert.equal(deployment.match(/const string AddinId = "(.*?)"/)?.[1], addinId);
for (const text of [manual, deployment]) {
  assert.ok(text.includes('<VendorId>OOXG</VendorId>'));
  assert.ok(text.includes('<Name>Revit MCP</Name>'));
}
const csharpPort = (await read('plugin/Bridge/BridgeService.cs')).match(/const int Port = (\d+)/)?.[1];
const nodePort = (await read('server/src/revit-bridge.js')).match(/const PORT = (\d+)/)?.[1];
assert.equal(csharpPort, nodePort);
assert.equal(csharpPort, '37652');
const config = await read('installer/configure-mcp.mjs');
assert.ok(config.includes("const name = 'revit_mcp'"));
assert.ok((await read('server/codex-mcp.example.toml')).includes('[mcp_servers.revit_mcp]'));
await access(path.join(repo, 'plugin/Assets/ooxgn-monogram.svg'));
await access(path.join(repo, 'NOTICE.md'));
await access(path.join(repo, `docs/releases/${version}.md`));
process.stdout.write(`PASS: Revit MCP ${version} release identity and version contracts\n`);
