import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { once } from 'node:events';
import { registerModelTools, parameterFilter } from '../src/model-tools.js';
import { discoverInstances, createDiscoveredClient } from '../src/revit-bridge.js';
import { accessMode, isModelWriteTool } from '../src/tool-access.js';

const tools = new Map();
registerModelTools((name,description,schema) => tools.set(name,schema));
const accepts = (name,args) => tools.get(name).safeParse(args).success;

test('model changes reject ambiguous targets and degenerate transformations', () => {
  assert.ok(accepts('preview_move_elements',{elementIds:[1,2],translation:{x:0,y:0,z:1}}));
  assert.equal(accepts('preview_move_elements',{elementIds:[1,1],translation:{x:0,y:0,z:1}}),false);
  assert.equal(accepts('preview_rotate_elements',{elementIds:[1],axisStart:{x:0,y:0,z:0},axisEnd:{x:0,y:0,z:0},angleDegrees:90}),false);
  assert.equal(accepts('preview_override_graphics',{viewId:1,elementIds:[2],categoryIds:[-2000011],transparency:20}),false);
  assert.equal(accepts('preview_override_graphics',{viewId:1,elementIds:[2]}),false);
  assert.ok(accepts('preview_override_graphics',{viewId:1,categoryIds:[-2000011],projectionLineWeight:3}));
  assert.equal(accepts('preview_change_element_types',{elementIds:[1],typeId:0}),false);
});

test('parameter queries require an unambiguous parameter and comparison value', () => {
  assert.ok(parameterFilter.safeParse({parameterName:'Расход',operator:'greaterThan',value:120,unitId:'autodesk.unit.unit:litersPerSecond-1.0.1'}).success);
  assert.ok(parameterFilter.safeParse({parameterId:-100,operator:'hasNoValue'}).success);
  for(const value of [{operator:'equals',value:1},{parameterName:'X',parameterId:1,operator:'equals',value:1},{parameterName:'X',operator:'equals'}])
    assert.equal(parameterFilter.safeParse(value).success,false);
});

test('schedule replacement, export scopes and isolation have explicit contracts', () => {
  assert.equal(accepts('preview_modify_schedule',{scheduleId:1}),false);
  assert.ok(accepts('preview_modify_schedule',{scheduleId:1,filters:[],sortGroups:[]}));
  assert.equal(accepts('preview_modify_schedule',{scheduleId:1,filters:[{fieldId:0,operator:'Equal',value:3}]}),false);
  assert.ok(accepts('export_views',{directory:'C:/exports',format:'csv',viewIds:[1]}));
  assert.equal(accepts('export_views',{directory:'C:/exports',format:'csv',scope:'visibleRegion'}),false);
  assert.equal(accepts('export_views',{directory:'C:/exports',format:'png',scope:'activeView',viewIds:[1]}),false);
  assert.equal(accepts('preview_isolate_annotations',{viewId:1,reset:true,categoryIds:[-1]}),false);
  assert.ok(accepts('preview_isolate_annotations',{viewId:1,reset:true}));
});

test('read mode excludes preview and write operations independently of update channel', () => {
  assert.equal(accessMode('read'),'read');
  assert.throws(()=>accessMode('beta'));
  for(const name of ['preview_move_elements','apply_move_elements','export_model','validate_family_file'])
    assert.ok(isModelWriteTool(name),name);
  for(const name of ['show_elements','get_element_properties','list_views','export_views'])
    assert.equal(isModelWriteTool(name),false,name);
});

test('discovery authenticates endpoints and discards stale or mismatched process records', async t => {
  const directory=await mkdtemp(path.join(tmpdir(),'revit-discovery-'));
  t.after(()=>rm(directory,{recursive:true,force:true}));
  const instanceId='a'.repeat(32), token='test-discovery-token-with-enough-characters';
  const server=net.createServer(socket=>{
    let buffer='';
    socket.on('data',chunk=>{
      buffer+=chunk;
      if(!buffer.includes('\n')) return;
      const request=JSON.parse(buffer);
      assert.equal(request.token,token);
      socket.end(JSON.stringify({id:request.id,ok:true,result:{processId:123,instanceId,documentName:'Fixture'}})+'\n');
    });
  });
  server.listen(0,'127.0.0.1'); await once(server,'listening');
  t.after(()=>server.close());
  const port=server.address().port;
  await writeFile(path.join(directory,'123.json'),JSON.stringify({processId:123,instanceId,token,port}));
  await writeFile(path.join(directory,'124.json'),JSON.stringify({processId:124,instanceId,token,port}));
  await writeFile(path.join(directory,'125.json'),JSON.stringify({processId:125,instanceId:'b'.repeat(32),token,port}));
  await writeFile(path.join(directory,'126.json'),'{broken');
  const found=await discoverInstances(directory);
  assert.deepEqual(found.map(({processId,documentName})=>({processId,documentName})),[{processId:123,documentName:'Fixture'}]);
});

test('routing requires an explicit PID for multiple instances and never falls back to another process', async t => {
  const token='test-routing-token-with-enough-characters';
  const server=net.createServer(socket=>{
    socket.once('data',chunk=>{
      const request=JSON.parse(chunk);
      assert.equal(request.arguments.processId,undefined,'Routing metadata is not a Revit argument');
      assert.equal(request.token,token);
      socket.end(JSON.stringify({id:request.id,ok:true,result:{routed:true}})+'\n');
    });
  });
  server.listen(0,'127.0.0.1');await once(server,'listening');t.after(()=>server.close());
  const port=server.address().port;
  const call=createDiscoveredClient(async()=>[{processId:1,port,token},{processId:2,port,token}]);
  await assert.rejects(call('get_project_info'),/Several Revit/);
  await assert.rejects(call('apply_move_elements',{processId:3,planId:'x'}),/not available/);
  assert.deepEqual(await call('get_project_info',{processId:2}),{routed:true});
  const controller=new AbortController();controller.abort();
  await assert.rejects(createDiscoveredClient(()=>{throw new Error('Discovery must not run');})('read',{}, {signal:controller.signal}),/abort/i);
});
