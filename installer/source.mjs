import { execFileSync } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export async function archiveSources(repo, destination) {
  const git = args => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' });
  if (git(['status', '--porcelain=v1', '--untracked-files=no']).trim()) {
    throw new Error('Commit tracked changes before packaging corresponding sources.');
  }
  const commit = git(['rev-parse', '--verify', 'HEAD']).trim();
  const files = git(['ls-tree', '-r', '--name-only', '-z', commit]).split('\0').filter(Boolean);
  for (const file of files) {
    if (/(^|\/)(bin|obj|node_modules|dist|artifacts|work|\.agents|\.git)(\/|$)/i.test(file)
      || /(^|\/)(AGENTS\.md|bridge\.token|settings\.json|RevitAPI(UI)?\.dll)$/i.test(file)
      || /\.(rvt|rfa|rte|rft|token|zip|exe|pdb|log)$/i.test(file)
      || /(^|\/)\.env($|\.(?!example$))/i.test(file)) {
      throw new Error(`File is not distributable in the source archive: ${file}`);
    }
  }
  await mkdir(path.dirname(path.resolve(destination)), { recursive: true });
  git(['archive', '--format=zip', '--output', path.resolve(destination), commit]);
  return commit;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [repo, destination] = process.argv.slice(2);
  if (!repo || !destination) throw new Error('Usage: source.mjs repo destination.zip');
  console.log(await archiveSources(repo, destination));
}
