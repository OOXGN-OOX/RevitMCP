// Opt-in native checks. The fixture path must match the open isolated test project.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { callRevit } from '../server/src/revit-bridge.js';

const [configPath]=process.argv.slice(2);
if(!configPath) throw new Error('Usage: node tests/model-revit.mjs <fixture.json>');
const config=JSON.parse(await readFile(configPath,'utf8'));
if(!config.projectPath || !path.isAbsolute(config.projectPath) || !Number.isInteger(config.processId))
  throw new Error('Fixture requires an absolute projectPath and explicit processId.');
const call=(tool,args={})=>callRevit(tool,{...args,processId:config.processId});
const project=await call('get_project_info');
assert.equal(path.resolve(project.filePath).toLowerCase(),path.resolve(config.projectPath).toLowerCase(),'Only the explicitly selected fixture may be tested.');
const before=await call('get_project_stats');
for(const kind of ['all','templates','schedules']) assert.ok(Array.isArray((await call('list_views',{kind})).items));
await call('list_sheet_collections');
await call('get_fill_and_line_patterns');
for(const id of config.elementIds??[]) {
  const full=await call('get_element_properties',{elementIds:[id],parameterLimit:500});
  assert.equal(full.foundCount,1);
  const first=await call('get_element_properties',{elementIds:[id],parameterLimit:1});
  assert.equal(first.elements[0].parameterCount,full.elements[0].parameterCount);
  assert.deepEqual(first.elements[0].parameters,full.elements[0].parameters.slice(0,1));
}
// Tests list exact preview names/arguments. No apply, export, save or UI commands are accepted.
for(const request of config.previews??[]) {
  if(!['preview_move_elements','preview_rotate_elements','preview_change_element_types','preview_create_schedule',
    'preview_modify_schedule','preview_edit_sheet','preview_override_graphics','preview_isolate_annotations',
    'preview_tag_elements','preview_create_sections','preview_create_sheets'].includes(request.tool))
    throw new Error(`Not an allowed preview: ${request.tool}`);
  const result=await call(request.tool,request.arguments);
  assert.equal(result.planCreated,true,request.tool);
  assert.equal(result.modelChanged,false,request.tool);
}
const after=await call('get_project_stats');
assert.deepEqual(after,before,'Preview rollbacks must retain model counts.');
console.log(JSON.stringify({passed:true,previewCount:config.previews?.length??0}));
