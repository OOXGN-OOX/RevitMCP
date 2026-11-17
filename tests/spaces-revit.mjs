// Native checks are opt-in and must target an explicitly selected isolated RVT.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {callRevit} from '../server/src/revit-bridge.js';
const [file]=process.argv.slice(2);
if(!file) throw new Error('Usage: node tests/spaces-revit.mjs <fixture.json>');
const config=JSON.parse(await readFile(file,'utf8'));
assert.ok(Number.isSafeInteger(config.processId)&&path.isAbsolute(config.projectPath??'')&&config.preview);
const call=(name,args={})=>callRevit(name,{...args,processId:config.processId});
const before=await call('get_project_info');
assert.equal(path.resolve(before.filePath).toLowerCase(),path.resolve(config.projectPath).toLowerCase(),'Only the explicitly selected isolated fixture may be tested.');
const links=(await call('list_revit_links')).links;
const link=links.find(l=>l.id===config.preview.linkInstanceId);
assert.ok(link?.available);
assert.ok(link.uniqueId&&link.typeUniqueId&&link.phaseMap);
const rooms=await call('list_rooms',{linkInstanceId:link.id,limit:500});
const source=new Map(rooms.rooms.map(r=>[r.id,r]));
for(const id of config.preview.roomIds) assert.ok(source.get(id)?.uniqueId);
const stats=await call('get_project_stats');
const spaces=await call('list_spaces',{limit:500});
const preview=await call('preview_create_spaces_from_link',config.preview);
assert.equal(preview.planCreated,true,JSON.stringify(preview));
assert.equal(preview.modelChanged,false);
for(const row of preview.preview.result.rows) {
  assert.notEqual(row.action,'error'); assert.ok(row.spaceAreaSquareMeters>0);
  assert.equal(row.space.name,source.get(row.roomId).name);
  assert.equal(row.space.number,source.get(row.roomId).number);
  assert.ok(Number.isFinite(row.areaDifferenceSquareMeters));
}
assert.deepEqual(await call('get_project_stats'),stats,'Preview must roll back all model elements.');
assert.deepEqual(await call('list_spaces',{limit:500}),spaces,'Preview must restore all Space fields and source associations.');
assert.deepEqual((await call('list_revit_links')).links,links,'Preview must restore Room Bounding.');
const failed=await call('preview_create_spaces_from_link',{...config.preview,verticalExtents:config.preview.verticalExtents.map((v,i)=>i? v : {...v,lowerOffsetMeters:999,upperOffsetMeters:-999})});
assert.equal(failed.planCreated,false);assert.equal(failed.modelChanged,false);
assert.deepEqual(await call('get_project_stats'),stats,'A failed batch must also roll back all prior rows.');
if(config.testApply===true) {
  assert.equal(link.roomBounding,true,'Apply fixture must start with Room Bounding enabled.');
  assert.ok(preview.preview.result.rows.every(r=>r.action==='create'),'Apply fixture must contain no existing Spaces for the requested rooms.');
  let created=[];
  try {
    const applied=await call('apply_create_spaces_from_link',{planId:preview.planId});
    assert.equal(applied.applied,true); created=applied.result.rows.map(r=>r.spaceId);
    const again=await call('preview_create_spaces_from_link',{...config.preview,existingPolicy:'skip'});
    assert.equal(again.planCreated,true,JSON.stringify(again));
    assert.ok(again.preview.result.rows.every(r=>r.action==='skip'),'Second run must not create duplicates.');
    const updated=await call('preview_create_spaces_from_link',{...config.preview,existingPolicy:'updateNamesAndNumbers'});
    assert.equal(updated.planCreated,true,JSON.stringify(updated));
    const current=await call('list_spaces',{limit:500});
    assert.equal(new Set(current.spaces.map(s=>s.uniqueId)).size,current.spaces.length);
  } finally {
    if(created.length) {
      const deletion=await call('preview_delete_elements',{elementIds:created});
      assert.equal((await call('apply_delete_plan',{planId:deletion.planId,allowDependentDeletion:false})).applied,true);
    }
  }
  assert.deepEqual(await call('get_project_stats'),stats,'Test-created Spaces must be removed.');
}
console.log(JSON.stringify({passed:true,rooms:config.preview.roomIds.length,applyChecked:config.testApply===true}));
