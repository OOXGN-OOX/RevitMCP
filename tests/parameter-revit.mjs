// Opt-in native validation in an explicitly selected isolated fixture project.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {callRevit,getRunningInstances} from '../server/src/revit-bridge.js';

const [file]=process.argv.slice(2);
if(!file) throw new Error('Usage: node tests/parameter-revit.mjs <fixture.json>');
const config=JSON.parse(await readFile(file,'utf8'));
if(!Number.isInteger(config.processId)||!path.isAbsolute(config.projectPath??'')||!Number.isInteger(config.elementId))
  throw new Error('Fixture requires processId, an absolute projectPath, elementId and a parameter selector.');
const call=(tool,args={})=>callRevit(tool,{...args,processId:config.processId});
assert.ok((await getRunningInstances()).instances.some(i=>i.processId===config.processId),'The selected Revit process must be available.');
const project=await call('get_project_info');
assert.equal(path.resolve(project.filePath).toLowerCase(),path.resolve(config.projectPath).toLowerCase(),'Only the selected fixture may be tested.');
const selector=Object.fromEntries(['parameterName','parameterId','parameterGuid','parameterScope'].filter(k=>config[k]!==undefined).map(k=>[k,config[k]]));
const options=()=>call('get_parameter_value_options',{elementId:config.elementId,...selector,limit:500});
const initial=await options();
assert.equal(initial.readOnly,false);
assert.equal(initial.hasMore,false,'Use a fixture with at most 500 options.');
const original=initial.current.elementIdText;
const target=initial.items.find(v=>v.elementIdText!==original);
assert.ok(target,'The fixture needs at least two applicable reference values.');
const stats=await call('get_project_stats');
const preview=await call('preview_set_parameter',{elementIds:[config.elementId],...selector,value:target.elementIdText});
assert.equal(preview.planCreated,true);
assert.equal(preview.modelChanged,false);
assert.equal(preview.preview.result.changedCount,1);
assert.equal(preview.preview.result.changes[0].after,target.elementIdText);
assert.equal((await options()).current.elementIdText,original,'Preview must roll back the value.');
assert.deepEqual(await call('get_project_stats'),stats,'Preview must roll back any nested component changes.');
const invalid=await call('preview_set_parameter',{elementIds:[config.elementId],...selector,elementIdValue:Number.MAX_SAFE_INTEGER});
assert.equal(invalid.planCreated,false,'A missing reference cannot produce an apply plan.');
assert.ok(invalid.skipped.length);
if(config.testApply===true) {
  // Native test writes are enabled explicitly in the fixture configuration only.
  try {
    assert.equal((await call('apply_parameter_plan',{planId:preview.planId})).applied,true);
    assert.equal((await options()).current.elementIdText,target.elementIdText);
  } finally {
    const restore=await call('preview_set_parameter',{elementIds:[config.elementId],...selector,value:original});
    assert.equal(restore.planCreated,true);
    assert.equal((await call('apply_parameter_plan',{planId:restore.planId})).applied,true);
    assert.equal((await options()).current.elementIdText,original);
  }
}
console.log(JSON.stringify({passed:true,previewRollback:true,applyChecked:config.testApply===true}));
