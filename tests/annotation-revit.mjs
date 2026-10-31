// Opt-in native family checks; no apply operation is invoked.
import assert from 'node:assert/strict';
import {readFile,stat} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {callRevit,getRunningInstances} from '../server/src/revit-bridge.js';
import {familyOperationCatalog} from '../server/src/family-tools.js';
const [file]=process.argv.slice(2);
if(!file)throw new Error('Usage: node tests/annotation-revit.mjs <fixture.json>');
const config=JSON.parse(await readFile(file,'utf8'));
if(!Number.isInteger(config.processId)||!path.isAbsolute(config.sourcePath??'')||!path.isAbsolute(config.outputPath??''))throw new Error('Fixture requires processId, sourcePath, outputPath and operations.');
assert.ok((await getRunningInstances()).instances.some(i=>i.processId===config.processId));
const call=(name,args={})=>callRevit(name,{...args,processId:config.processId});
await call('get_project_info');
const hash=async()=>createHash('sha256').update(await readFile(config.sourcePath)).digest('hex');
const original=await hash();
const read=await call('read_family_annotations',{path:config.sourcePath,limit:500});
assert.equal(read.annotations.errors.length,0);assert.equal(read.annotations.labelComposition.available,false);
assert.ok(Array.isArray(read.resources.textTypes));assert.ok(familyOperationCatalog({action:'annotationText'}).operations[0].schema);
const preview=await call('preview_build_family',{sourcePath:config.sourcePath,outputPath:config.outputPath,operations:config.operations});
assert.equal(preview.planCreated,true);assert.equal(preview.preview.verified.reopened,true);
for(const type of preview.preview.verified.family.types){assert.equal(type.annotations.errors.length,0);assert.equal(type.annotations.hasMore,false);}
assert.equal(await hash(),original,'Original RFA must remain unchanged.');
await assert.rejects(stat(config.outputPath),e=>e.code==='ENOENT','Preview must not publish an RFA.');
if(config.samples){
  const samples=await call('validate_annotation_family',{...config.samples,path:config.sourcePath});
  assert.ok(samples.images.length);assert.equal(samples.sourceUnchanged,true);assert.equal(samples.sampleProjectUnchanged,true);
  for(const image of samples.images)assert.ok((await stat(image)).size>0);
}
console.log(JSON.stringify({passed:true,reopened:true,originalUnchanged:true,compositionAvailable:false}));
