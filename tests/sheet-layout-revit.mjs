// Opt-in Revit integration checks. Run only against an explicitly selected isolated fixture.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { callRevit } from '../server/src/revit-bridge.js';

const [configPath]=process.argv.slice(2);
if(!configPath) throw new Error('Usage: node tests/sheet-layout-revit.mjs <fixture.json>');
const config=JSON.parse(await readFile(configPath,'utf8'));
for(const field of ['processId','sheetId','unplacedViewId','placedViewportId','viewportTypeId'])
  assert.ok(Number.isSafeInteger(config[field])&&config[field]>0,`Fixture requires ${field}.`);
assert.ok(config.projectPath&&path.isAbsolute(config.projectPath),'Fixture requires an absolute projectPath.');
const call=(name,args={})=>callRevit(name,{...args,processId:config.processId});
const project=await call('get_project_info');
assert.equal(path.resolve(project.filePath).toLowerCase(),path.resolve(config.projectPath).toLowerCase(),
  'Only the explicitly selected isolated fixture may be tested.');
const resources=()=>call('list_sheet_resources',{sheetId:config.sheetId});
const before=await resources(), stats=await call('get_project_stats');
const sheet=before.sheets[0];
assert.equal(sheet.id,config.sheetId);
assert.ok(sheet.titleBlockId>0);
assert.ok(Math.abs(sheet.widthMillimeters-(sheet.maxXmm-sheet.minXmm))<1e-6);
assert.ok(Math.abs(sheet.heightMillimeters-(sheet.maxYmm-sheet.minYmm))<1e-6);
if(config.requireNegativeX) assert.ok(sheet.minXmm<0,'Fixture must have a sheet in negative X.');
const unplaced=before.unplacedViews.find(v=>v.id===config.unplacedViewId);
assert.equal(unplaced?.canAddToSheet,true);
assert.ok(unplaced.paperBoundsMm.widthMillimeters>0);
assert.ok(unplaced.paperBoundsMm.heightMillimeters>0);
assert.ok(before.viewportTypes.some(t=>t.id===config.viewportTypeId));
const existing=sheet.viewports.find(v=>v.viewportId===config.placedViewportId);
assert.ok(existing,'Fixture requires a viewport on the selected sheet.');
const close=(a,b)=>assert.ok(Math.abs(a-b)<.01,`${a} != ${b}`);
const bounds=box=>{
  assert.ok(box);
  for(const field of ['minXmm','minYmm','maxXmm','maxYmm']) assert.ok(Number.isFinite(box[field]));
  assert.ok(box.maxXmm>=box.minXmm&&box.maxYmm>=box.minYmm);
};
const placement={sheetId:sheet.id,viewId:unplaced.id,viewportTypeId:config.viewportTypeId,
  centerXmm:(sheet.minXmm+sheet.maxXmm)/2,centerYmm:(sheet.minYmm+sheet.maxYmm)/2};
const placed=await call('preview_place_views_on_sheets',{placements:[placement]});
assert.equal(placed.planCreated,true); assert.equal(placed.modelChanged,false);
const result=placed.preview.result.placements[0];
assert.equal(result.viewId,unplaced.id); assert.ok(result.viewportId>0);
assert.equal(result.viewportTypeId,config.viewportTypeId); assert.equal(result.scale,unplaced.scale);
close(result.centerXmm,placement.centerXmm); close(result.centerYmm,placement.centerYmm);
bounds(result.boundsMm); if(result.labelBoundsMm) bounds(result.labelBoundsMm);
// Failure after a viewport was created must roll back every earlier placement too.
await assert.rejects(()=>call('preview_place_views_on_sheets',{placements:[placement,{...placement,viewId:Number.MAX_SAFE_INTEGER}]}));
await assert.rejects(()=>call('preview_place_views_on_sheets',{placements:[{...placement,viewportTypeId:config.sheetId}]}));
const finish=await call('preview_finish_sheet',{sheetId:sheet.id,viewports:[{
  viewportId:existing.viewportId,viewportTypeId:config.viewportTypeId,titleOnSheet:'В1',
  labelOffsetXmm:80,labelOffsetYmm:340,labelLineLengthMm:0
}]});
assert.equal(finish.planCreated,true); assert.equal(finish.modelChanged,false);
const finished=finish.preview.result;
assert.deepEqual(finished.textNoteIds,[]);
const viewport=finished.viewports[0];
assert.equal(viewport.titleOnSheet,'В1'); assert.equal(viewport.viewportTypeId,config.viewportTypeId);
assert.equal(viewport.scale,existing.scale);
close(viewport.centerXmm,existing.centerXmm); close(viewport.centerYmm,existing.centerYmm);
close(viewport.labelOffsetXmm,80); close(viewport.labelOffsetYmm,340); close(viewport.labelLineLengthMm,0);
bounds(viewport.boundsMm); if(viewport.labelBoundsMm) bounds(viewport.labelBoundsMm);
assert.deepEqual(await resources(),before,'Preview must restore source views, labels, viewports and resource availability.');
assert.deepEqual(await call('get_project_stats'),stats,'Preview must retain all model counts.');
console.log(JSON.stringify({passed:true,negativeX:sheet.minXmm<0,previewCount:2}));
