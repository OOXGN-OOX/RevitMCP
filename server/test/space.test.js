import test from 'node:test';
import assert from 'node:assert/strict';
import {registerSpaceTools} from '../src/space-tools.js';
const tools=new Map();
registerSpaceTools((name,description,schema,annotations)=>tools.set(name,{schema,annotations}));
const parse=(name,args)=>tools.get(name).schema.safeParse(args);
const preview={linkInstanceId:100,roomIds:[10,20],hostPhaseId:5,
  levelMap:[{sourceLevelId:1,hostLevelId:2}],
  verticalExtents:[{roomId:10,lowerOffsetMeters:0,upperLevelId:3,upperOffsetMeters:0},{roomId:20,lowerOffsetMeters:-.1,upperLevelId:2,upperOffsetMeters:3}]};
test('Space preview requires explicit source, phase, level mapping and per-room vertical extents',()=>{
  const result=parse('preview_create_spaces_from_link',preview);
  assert.ok(result.success);assert.equal(result.data.copyNames,true);assert.equal(result.data.copyNumbers,true);
  assert.equal(result.data.existingPolicy,'skip');assert.equal(result.data.enableRoomBounding,false);
  for(const policy of ['skip','updateNamesAndNumbers','error']) assert.ok(parse('preview_create_spaces_from_link',{...preview,existingPolicy:policy}).success);
  for(const changes of [{linkInstanceId:0},{roomIds:[]},{roomIds:[10,10]},{roomIds:[10,20,30]},
    {hostPhaseId:undefined},{levelMap:[]},{levelMap:[preview.levelMap[0],preview.levelMap[0]]},
    {verticalExtents:[]},{verticalExtents:[preview.verticalExtents[0]]},
    {verticalExtents:[preview.verticalExtents[0],preview.verticalExtents[0]]},
    {verticalExtents:[{...preview.verticalExtents[0],roomId:30},preview.verticalExtents[1]]},
    {verticalExtents:[{...preview.verticalExtents[0],upperOffsetMeters:Infinity},preview.verticalExtents[1]]},
    {existingPolicy:'overwrite'},{copyNames:'true'},{enableRoomBounding:1}])
    assert.equal(parse('preview_create_spaces_from_link',{...preview,...changes}).success,false,JSON.stringify(changes));
});
test('Space apply is plan-only and readers remain read-only',()=>{
  assert.ok(parse('apply_create_spaces_from_link',{planId:'checked-plan'}).success);
  for(const args of [{},{planId:''},{planId:'p',...preview}]) assert.equal(parse('apply_create_spaces_from_link',args).success,false);
  assert.ok(parse('list_revit_links',{}).success);assert.ok(parse('list_spaces',{phaseId:5,levelId:2,offset:0,limit:500}).success);
  assert.equal(parse('list_spaces',{limit:501}).success,false);
  for(const name of ['preview_create_spaces_from_link','apply_create_spaces_from_link']) assert.equal(tools.get(name).annotations.readOnlyHint,false);
});
