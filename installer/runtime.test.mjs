import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import os from 'node:os';

const script = fileURLToPath(new URL('./runtime.mjs', import.meta.url));
test('runtime packages identify the source branch, channel and required loader', async t => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'revit-runtime-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const dir of ['server', 'loader', 'plugin/bin/Release/net8.0-windows', 'assets/icons'])
    await mkdir(path.join(root, dir), { recursive: true });
  await writeFile(path.join(root, 'server/package.json'), JSON.stringify({ version: '0.0.2' }));
  await writeFile(path.join(root, 'loader/RevitMCP.Loader.csproj'), '<Project><PropertyGroup><AssemblyVersion>0.0.2.1</AssemblyVersion></PropertyGroup></Project>');
  await writeFile(path.join(root, 'plugin/bin/Release/net8.0-windows/RevitMCP.Core.dll'), 'fixture core');
  await writeFile(path.join(root, 'assets/icons/connection-16.png'), 'fixture icon');
  for (const branch of ['main', '0.0.2']) {
    const output = path.join(root, 'package-' + branch);
    execFileSync(process.execPath, [script, root, output, 'a'.repeat(40), '7'], {
      env: { ...process.env, GITHUB_REF_NAME: branch }, windowsHide: true
    });
    const manifest = JSON.parse(await readFile(path.join(output, 'manifest.json'), 'utf8'));
    assert.equal(manifest.channel, branch === 'main' ? 'stable' : 'beta');
    assert.equal(manifest.sourceBranch, branch);
    assert.equal(manifest.minimumLoaderVersion, '0.0.2.1');
    assert.equal(manifest.buildId, 'a'.repeat(40));
    assert.equal(manifest.sequence, 7);
    for (const [name, hash] of Object.entries(manifest.files))
      assert.equal(hash, createHash('sha256').update(await readFile(path.join(output, name))).digest('hex'));
  }
});
