import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, rm, access } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { archiveSources } from './source.mjs';

async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'revit-source-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const repo = path.join(root, 'repository with spaces');
  await mkdir(repo);
  const git = args => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' });
  git(['init', '--quiet']);
  git(['config', 'user.name', 'Source archive test']);
  git(['config', 'user.email', 'source-test@example.invalid']);
  git(['config', 'commit.gpgsign', 'false']);
  git(['config', 'core.autocrlf', 'false']);
  await writeFile(path.join(repo, 'source.txt'), 'Committed source\n');
  git(['add', 'source.txt']);
  git(['commit', '--quiet', '-m', 'Initial sources']);
  return { repo, git, destination: path.join(root, 'output with spaces', 'source.zip') };
}

test('archives the exact commit and excludes untracked working files', async t => {
  const { repo, git, destination } = await fixture(t);
  await writeFile(path.join(repo, 'session-notes.md'), 'Private working notes');
  const commit = await archiveSources(repo, destination);
  assert.equal(commit, git(['rev-parse', 'HEAD']).trim());
  const archive = await readFile(destination);
  assert.deepEqual(archive, execFileSync('git', ['-C', repo, 'archive', '--format=zip', commit]));
  assert.ok(archive.includes(Buffer.from('source.txt')));
  assert.ok(!archive.includes(Buffer.from('session-notes.md')));
});

for (const staged of [false, true]) {
  test(`rejects ${staged ? 'staged' : 'unstaged'} source changes before writing an archive`, async t => {
    const { repo, git, destination } = await fixture(t);
    await writeFile(path.join(repo, 'source.txt'), 'Changed source\n');
    if (staged) git(['add', 'source.txt']);
    await assert.rejects(archiveSources(repo, destination), /Commit tracked changes/);
    await assert.rejects(access(destination), { code: 'ENOENT' });
  });
}

test('rejects private files even if they were committed', async t => {
  const { repo, git, destination } = await fixture(t);
  await mkdir(path.join(repo, '.agents'));
  await writeFile(path.join(repo, '.agents', 'notes.md'), 'Private working notes');
  git(['add', '.agents/notes.md']);
  git(['commit', '--quiet', '-m', 'Add private fixture']);
  await assert.rejects(archiveSources(repo, destination), /not distributable/);
  await assert.rejects(access(destination), { code: 'ENOENT' });
});
