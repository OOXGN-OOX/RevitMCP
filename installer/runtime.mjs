import { mkdir, cp, readFile, writeFile, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
const [repo, stage, buildId = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), sequence = '0'] = process.argv.slice(2);
if (!repo || !stage || !/^[a-f0-9]{40}$/.test(buildId) || !/^\d+$/.test(sequence)) throw new Error('Usage: runtime.mjs repo stage [commit sequence]');
const { version } = JSON.parse(await readFile(path.join(repo, 'server/package.json'), 'utf8'));
await mkdir(path.join(stage, 'core'), { recursive: true });
await cp(path.join(repo, 'plugin/bin/Release/net8.0-windows/RevitMCP.Core.dll'), path.join(stage, 'core/RevitMCP.Core.dll'));
await cp(path.join(repo, 'assets/icons'), path.join(stage, 'assets'), { recursive: true });
const files = {};
for (const folder of ['core', 'assets']) for (const file of (await readdir(path.join(stage, folder))).sort()) {
  const name = folder + '/' + file;
  files[name] = createHash('sha256').update(await readFile(path.join(stage, name))).digest('hex');
}
await writeFile(path.join(stage, 'manifest.json'), JSON.stringify({ schemaVersion: 1, protocolVersion: 1, version, minimumLoaderVersion: '0.0.2', buildId, sequence: Number(sequence), files }, null, 2));
