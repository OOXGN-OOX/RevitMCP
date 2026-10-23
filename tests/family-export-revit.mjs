// Opt-in integration check against a matching installed build and an isolated fixture project.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { callRevit } from '../server/src/revit-bridge.js';

const [configPath]=process.argv.slice(2);
if(!configPath) throw new Error('Usage: node tests/family-export-revit.mjs <fixture.json>');
const config=JSON.parse(await readFile(configPath,'utf8'));
if(!Number.isInteger(config.processId) || !config.projectPath || !path.isAbsolute(config.projectPath)
  || !config.outputDirectory || !path.isAbsolute(config.outputDirectory) || !config.familyId || !config.nestedFamilyName)
  throw new Error('Fixture requires processId, projectPath, outputDirectory, familyId and nestedFamilyName.');
const call=(name,args={})=>callRevit(name,{...args,processId:config.processId});
const project=await call('get_project_info');
assert.equal(path.resolve(project.filePath).toLowerCase(),path.resolve(config.projectPath).toLowerCase(),'Select the isolated fixture, not a production project.');
await mkdir(config.outputDirectory); // Must be a new directory, without user files.
const hash=async file=>createHash('sha256').update(await readFile(file)).digest('hex');
const missing=async file=>{try{await stat(file);return false;}catch(e){if(e.code==='ENOENT')return true;throw e;}};
const types=await call('list_family_types',{limit:500});
const family=types.types?.find(t=>t.familyId===config.familyId);
assert.ok(family,'Fixture family must have a listed loaded type.');
const output=path.join(config.outputDirectory,family.family+'.rfa');
const request={familyId:config.familyId,outputPath:output};
const preview=await call('preview_export_family',request);
assert.equal(preview.preview.roundTripPassed,true);
assert.equal(preview.outputCreated,false);
assert.ok(await missing(output),'Preview must not publish an RFA');
const saved=await call('apply_export_family',{planId:preview.planId});
assert.equal(saved.applied,true,'Confirm exporting the test fixture in Revit');
assert.equal(saved.result.roundTripPassed,true);
const originalHash=await hash(output);
await assert.rejects(()=>call('preview_export_family',request),/new RFA|new file/i);
for(const parameterPolicy of ['preserve','overwrite']) {
  const reload=await call('preview_load_family',{path:output,parameterPolicy,sharedFamilySource:'project'});
  assert.equal(reload.preview.result.preservationPassed,true);
  assert.equal(reload.preview.result.verificationStage,'afterCommit');
}
const copy=path.join(config.outputDirectory,'neutral-nested.rfa');
const nestedRequest={sourcePath:output,nestedFamilyName:config.nestedFamilyName,outputPath:copy};
const nested=await call('preview_export_nested_family',nestedRequest);
assert.equal(nested.preview.roundTripPassed,true);
assert.ok(await missing(copy));
const extracted=await call('apply_export_nested_family',{planId:nested.planId});
assert.equal(extracted.applied,true);
assert.equal(await hash(output),originalHash,'Parent RFA must remain unchanged');
const read=await call('read_family_file',{path:copy});
assert.deepEqual(read.types.map(t=>t.name).sort(),extracted.result.verified.family.types.map(t=>t.name).sort());
const changedOutput=path.join(config.outputDirectory,'changed-source-rejected.rfa');
const guarded=await call('preview_export_nested_family',{...nestedRequest,outputPath:changedOutput});
const original=await readFile(output);
try {
  await writeFile(output,Buffer.concat([original,Buffer.from('changed fixture input')]));
  await assert.rejects(()=>call('apply_export_nested_family',{planId:guarded.planId}),/changed after preview/);
  assert.ok(await missing(changedOutput));
} finally { await writeFile(output,original); }
assert.equal(await hash(output),originalHash);
const after=await call('get_project_info');
assert.equal(after.documentRevision,project.documentRevision,'Temporary documents and rollback must not invalidate the project revision');
assert.deepEqual(after.activeView,project.activeView);
assert.deepEqual(after.selectedElementIds,project.selectedElementIds);
console.log(JSON.stringify({passed:true,output,copy,sourceSha256:originalHash}));
