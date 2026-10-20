import { readFile, access } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
const repo = fileURLToPath(new URL('../', import.meta.url));
const read = name => readFile(path.join(repo, name), 'utf8');
const { version } = JSON.parse(await read('server/package.json'));
assert.match(version, /^\d+\.\d+\.\d+$/);
for (const file of ['plugin/RevitMCP.csproj', 'loader/RevitMCP.Loader.csproj', 'updates/RevitMCP.Updates.csproj', 'contracts/RevitMCP.Contracts.csproj', 'installer/RevitMCP.Setup.csproj']) {
  const xml = await read(file);
  assert.equal(xml.match(/<Version>(.*?)<\/Version>/)?.[1], version, file);
  assert.equal(xml.match(/<Company>(.*?)<\/Company>/)?.[1], 'OOXGN', file);
}
const deployment = await read('installer/Deployment.cs');
assert.equal(deployment.match(/const string Version = "(.*?)"/)?.[1], version);
assert.equal((await read('server/src/index.js')).match(/version: '(.*?)'/)?.[1], version);
assert.equal((await read('installer/app.manifest')).match(/assemblyIdentity version="(.*?)"/)?.[1], version + '.0');
const manual = await read('install.ps1');
const id = deployment.match(/const string AddinId = "(.*?)"/)?.[1];
assert.match(id, /^[0-9A-F-]{36}$/);
assert.ok(manual.includes(id));
for (const text of [manual, deployment]) {
  assert.ok(text.includes('<FullClassName>RevitMCP.Loader.PluginApplication</FullClassName>'));
  assert.ok(text.includes('<VendorId>OOXG</VendorId>'));
}
assert.ok((await read('loader/BridgeService.cs')).includes('TcpListener(IPAddress.Loopback, 0)'));
assert.ok((await read('server/src/revit-bridge.js')).includes('discoverInstances'));
for (const name of ['connection', 'auto-confirm', 'update'])
  for (const size of [16, 32]) await access(path.join(repo, `assets/icons/${name}-${size}.png`));
await access(path.join(repo, 'assets/revit-mcp.ico'));
await access(path.join(repo, 'NOTICE.md'));
console.log(`PASS: Revit MCP ${version} metadata and package contracts`);
