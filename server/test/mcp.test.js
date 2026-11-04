import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { registerHostedTools } from '../src/hosted-tools.js';
import { registerAnnotationTools } from '../src/annotation-tools.js';
import { registerGraphicsTools } from '../src/graphics-tools.js';
import { registerSheetTools } from '../src/sheet-tools.js';
import { registerRoofTools } from '../src/roof-tools.js';
import { registerMepTools } from '../src/mep-tools.js';
import { registerStructureTools } from '../src/structure-tools.js';

test('MCP initialize/list and C# dispatcher expose matching tool contracts', async t => {
  const child = spawn(process.execPath, [fileURLToPath(new URL('../src/index.js', import.meta.url))], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  t.after(() => child.kill());
  let buffer = '';
  const pending = new Map();
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => { stderr += chunk; });
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buffer += chunk;
    while (buffer.includes('\n')) {
      const newline = buffer.indexOf('\n');
      const message = JSON.parse(buffer.slice(0, newline));
      buffer = buffer.slice(newline + 1);
      pending.get(message.id)?.(message);
    }
  });
  let nextId = 1;
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    const timeout = setTimeout(() => reject(new Error(`MCP ${method} timed out: ${stderr}`)), 5000);
    pending.set(id, response => { clearTimeout(timeout); pending.delete(id); resolve(response); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const initialized = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'revit-mcp-tests', version: '1' } });
  assert.equal(initialized.result.serverInfo.version, '0.0.2');
  assert.equal(initialized.result.serverInfo.name, 'revit-mcp');
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const listed = await rpc('tools/list', {});
  const tools = listed.result.tools;
  const names = tools.map(tool => tool.name);
  assert.equal(new Set(names).size, names.length);
  const catalog=await rpc('tools/call',{name:'get_family_operation_catalog',arguments:{action:'annotationText'}});
  assert.ok(!catalog.result?.isError);const catalogResult=JSON.parse(catalog.result.content[0].text);
  assert.equal(catalogResult.operations[0].schema.properties.text.type,'string');
  assert.equal(catalogResult.capabilities.labelCompositionRead,false);
  const source = await readFile(new URL('../../plugin/Bridge/RevitTools.cs', import.meta.url), 'utf8');
  const routed = [...source.slice(0, source.indexOf('private static object Ping')).matchAll(/"([a-z_]+)"\s*=>/g)].map(match => match[1]);
  const local = ['store_project_data', 'store_room_data', 'query_stored_data', 'get_running_revit_instances','get_family_operation_catalog'];
  assert.deepEqual(names.filter(name => !local.includes(name)).sort(), [...new Set(routed)].sort());
  for (const name of ['preview_place_hosted_families', 'apply_create_dimensions', 'preview_tag_rooms', 'apply_color_by_parameter'])
    assert.ok(names.includes(name), `Missing tool ${name}`);
  const deletion = tools.find(tool => tool.name === 'apply_delete_plan');
  const mepItems=tools.find(tool=>tool.name==='preview_create_mep').inputSchema.properties.segments.items;
  const flexSchema=(mepItems.anyOf??mepItems.oneOf??[]).find(s=>s.properties?.kind?.const==='flexPipe');
  assert.equal(flexSchema?.properties?.points?.maxItems,100,'The public MCP contract must expose flexible path points');
  const parameter=tools.find(tool=>tool.name==='preview_set_parameter');
  assert.equal(parameter.inputSchema.properties.elementIdValue.maximum,Number.MAX_SAFE_INTEGER);
  assert.ok(parameter.inputSchema.properties.parameterScope);
  assert.equal(tools.find(tool=>tool.name==='get_parameter_value_options').annotations.readOnlyHint,true);
  const invalidReference=await rpc('tools/call',{name:'preview_set_parameter',arguments:{elementIds:[1],parameterName:'Питание',value:'100',elementIdValue:100}});
  assert.ok(invalidReference.error||invalidReference.result?.isError,'Ambiguous values must fail before bridge access');
  const documentation = tools.find(tool => tool.name === 'read_documentation');
  assert.ok(documentation, 'Reference capture needs a native documentation reader');
  assert.equal(documentation.annotations.readOnlyHint, true);
  assert.equal(documentation.inputSchema.properties.limit.maximum, 10);
  const oversized = await rpc('tools/call', { name: 'read_documentation', arguments: { offset: 0, limit: 11 } });
  assert.ok(oversized.error || oversized.result?.isError, 'Oversized capture must be rejected before bridge access');
  assert.equal(deletion.inputSchema.properties.allowDependentDeletion.default, false);
  assert.equal(deletion.annotations.destructiveHint, true);
  const invalid = await rpc('tools/call', { name: 'preview_place_hosted_families', arguments: { instances: [] } });
  assert.ok(invalid.error || invalid.result?.isError, 'Invalid batch must be rejected before bridge access');
});

test('read-only MCP profile does not expose or dispatch mutation tools', async t => {
  const child=spawn(process.execPath,[fileURLToPath(new URL('../src/index.js',import.meta.url))],
    {stdio:['pipe','pipe','pipe'],windowsHide:true,env:{...process.env,REVIT_MCP_ACCESS:'read'}});
  t.after(()=>child.kill());
  child.stderr.resume();
  let buffer='',nextId=0;
  const pending=new Map();
  child.stdout.setEncoding('utf8');
  child.stdout.on('data',chunk=>{
    buffer+=chunk;
    while(buffer.includes('\n')) {
      const at=buffer.indexOf('\n'), message=JSON.parse(buffer.slice(0,at)); buffer=buffer.slice(at+1);
      pending.get(message.id)?.(message);
    }
  });
  const rpc=(method,params)=>new Promise((resolve,reject)=>{
    const id=++nextId, timeout=setTimeout(()=>reject(new Error('RPC timed out')),5000);
    pending.set(id,result=>{clearTimeout(timeout);pending.delete(id);resolve(result);});
    child.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n');
  });
  await rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'test',version:'1'}});
  child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
  const listed=await rpc('tools/list',{});
  assert.ok(listed.result.tools.some(t=>t.name==='get_running_revit_instances'));
  assert.ok(listed.result.tools.some(t=>t.name==='show_elements'));
  assert.ok(listed.result.tools.every(t=>!t.name.startsWith('preview_')&&!t.name.startsWith('apply_')));
  const rejected=await rpc('tools/call',{name:'apply_move_elements',arguments:{planId:'x'}});
  assert.ok(rejected.error||rejected.result?.isError);
});

const contracts = new Map();
for (const registrar of [registerHostedTools, registerAnnotationTools, registerGraphicsTools, registerSheetTools, registerRoofTools, registerMepTools, registerStructureTools])
  registrar((name, description, schema, annotations) => contracts.set(name, { schema, annotations }));
const parse = (name, args) => contracts.get(name).schema.safeParse(args).success;

test('MEP tags require explicit types, unique targets and valid leader geometry', () => {
  const target = { elementId: 10, tagTypeId: 20 };
  const point = { xMeters: 1, yMeters: 2, zMeters: 3 };
  assert.ok(parse('preview_tag_mep', { tags: [target] }));
  assert.ok(parse('preview_tag_mep', { viewId: 30, tags: [{ elementId: 10 }],
    categoryTypes: [{ category: 'OST_DuctTerminal', tagTypeId: 20 }] }));
  assert.ok(parse('preview_tag_mep', { tags: [{ ...target, headPosition: point,
    leaderEndPosition: point, elbowPosition: { ...point, xMeters: 2 } }] }));
  for (const input of [
    { tags: [] }, { tags: Array(51).fill(target) }, { tags: [target, target] },
    { tags: [{ elementId: 10 }] }, { tags: [{ ...target, leader: false, elbowPosition: point }] },
    { tags: [{ ...target, leader: false, leaderEndPosition: point }] },
    { tags: [{ ...target, headPosition: point, offsetRightPaperMillimeters: 15 }] },
    { tags: [{ ...target, headPosition: { ...point, zMeters: Infinity } }] },
    { tags: [{ ...target, offsetUpPaperMillimeters: 101 }] },
    { tags: [{ ...target, orientation: 'diagonal' }] },
    { tags: [target], categoryTypes: [{ category: 'OST_Walls', tagTypeId: 20 }] },
    { tags: [target], categoryTypes: Array(2).fill({ category: 'OST_DuctCurves', tagTypeId: 20 }) }
  ]) assert.equal(parse('preview_tag_mep', input), false, JSON.stringify(input));
  assert.ok(parse('list_annotation_types', { kind: 'mepTags' }));
  assert.equal(contracts.get('apply_tag_mep').annotations.readOnlyHint, false);
  assert.equal(parse('apply_tag_mep', {}), false);
});

test('roofs and sections reject degenerate geometry before bridge access', () => {
  const roof={roofTypeId:1,levelId:2,minX:0,maxX:12,minY:0,maxY:8,offsetMeters:3.15,slopeDegrees:30,ridgeAxis:'x',attachWallIds:[3,4]};
  assert.ok(parse('preview_create_roof',roof));
  for(const changes of [{maxX:0},{slopeDegrees:0},{slopeDegrees:90},{ridgeAxis:'z'},{attachWallIds:[3,3]},{offsetMeters:Infinity}])
    assert.equal(parse('preview_create_roof',{...roof,...changes}),false);
  const section={name:'Section',xMeters:0,yMeters:0,bottomMeters:-1,topMeters:7,widthMeters:15,depthMeters:10,directionX:0,directionY:1};
  assert.ok(parse('preview_create_sections',{views:[section]}));
  for(const changes of [{topMeters:-2},{directionY:0},{depthMeters:0}])
    assert.equal(parse('preview_create_sections',{views:[{...section,...changes}]}),false);
});

test('MEP contracts require a valid segment and matching size shape', () => {
  const point = (x) => ({ xMeters: x, yMeters: 0, zMeters: 2 });
  const pipe = { kind: 'pipe', typeId: 1, systemTypeId: 2, levelId: 3,
    start: point(0), end: point(2), diameterMeters: 0.05 };
  assert.ok(parse('preview_create_mep', { segments: [pipe] }));
  assert.ok(parse('preview_create_mep', { segments: [{ ...pipe, kind: 'duct',
    diameterMeters: undefined, widthMeters: 0.4, heightMeters: 0.2 }] }));
  for (const bad of [{ end: point(0) }, { diameterMeters: 0 },
    { widthMeters: 0.2 }, { kind: 'duct', widthMeters: 0.2, heightMeters: 0.2 }])
    assert.equal(parse('preview_create_mep', { segments: [{ ...pipe, ...bad }] }), false);
});

test('flex pipe contracts require a bounded path and explicit flexible type references',()=>{
  const point=(x,y=0,z=0)=>({xMeters:x,yMeters:y,zMeters:z});
  const flex={kind:'flexPipe',typeId:1,systemTypeId:2,levelId:3,diameterMeters:.015,points:[point(0),point(.3,.2),point(.6,0)]};
  assert.ok(parse('preview_create_mep',{segments:[flex]}));
  assert.ok(parse('preview_create_mep',{segments:[{...flex,startTangent:{x:1,y:0,z:0}}]}));
  assert.ok(parse('preview_create_mep',{segments:[{...flex,endTangent:{x:0,y:0,z:-1}}]}));
  const rigid={kind:'pipe',typeId:1,systemTypeId:2,levelId:3,start:point(0),end:point(1),diameterMeters:.02};
  assert.ok(parse('preview_create_mep',{segments:[rigid,flex]}));
  for(const change of [{kind:'flexDuct'},{typeId:0},{points:[]},{points:[point(0)]},{points:[point(0),point(0)]},
    {points:[point(0),point(.001)]},{points:[point(0),point(1),point(0)]},{points:[point(0),point(301)]},
    {points:Array.from({length:101},(_,i)=>point(i))},{points:[point(0),point(Infinity)]},
    {diameterMeters:undefined},{widthMeters:.1},{start:point(0)},{startTangent:{x:0,y:0,z:0}},
    {endTangent:{x:1e7,y:0,z:0}},{startConnector:{elementId:100,connectorId:1}}])
    assert.equal(parse('preview_create_mep',{segments:[{...flex,...change}]}),false);
  assert.equal(parse('apply_create_mep',{planId:'plan',segments:[flex]}),false);
});

test('MEP connections require explicit fitting and distinct valid connector owners', () => {
  const pair = { first: { elementId: 10, connectorId: 0 }, second: { elementId: 20, connectorId: 1 }, fitting: 'direct' };
  for (const fitting of ['direct', 'elbow', 'union', 'transition'])
    assert.ok(parse('preview_connect_mep', { ...pair, fitting }));
  for (const change of [
    { fitting: undefined }, { fitting: 'auto' }, { second: { elementId: 10, connectorId: 1 } },
    { first: { elementId: -1, connectorId: 0 } }, { first: { elementId: 10, connectorId: -1 } },
    { first: { elementId: 10, connectorId: 0.5 } }, { first: { elementId: 10, connectorId: 2147483648 } },
    { first: { elementId: 10, connectorId: 0, origin: [0, 0, 0] } }
  ]) assert.equal(parse('preview_connect_mep', { ...pair, ...change }), false);
  assert.ok(parse('list_mep_connectors', { elementIds: [10, 20], unusedOnly: true }));
  for (const elementIds of [[], [10, 10], [0], Array.from({ length: 51 }, (_, i) => i + 1)])
    assert.equal(parse('list_mep_connectors', { elementIds }), false);
  assert.equal(parse('apply_connect_mep', pair), false);
  assert.equal(parse('apply_connect_mep', { planId: '' }), false);
  assert.ok(parse('apply_connect_mep', { planId: 'preview-plan' }));
  assert.equal(contracts.get('apply_connect_mep').annotations.readOnlyHint, false);
});

test('beam system and phase contracts reject invalid layouts and duplicate views', () => {
  const system = { levelId: 1, beamTypeId: 2, minX: 0, maxX: 10, minY: 0, maxY: 8,
    beamDirection: 'x', spacingMeters: 0.6 };
  assert.ok(parse('preview_create_beam_systems', { systems: [system] }));
  assert.equal(parse('preview_create_beam_systems', { systems: [{ ...system, maxX: 0 }] }), false);
  assert.equal(parse('preview_create_beam_systems', { systems: [{ ...system, spacingMeters: 0 }] }), false);
  assert.ok(parse('preview_set_view_phase', { phaseId: 1, viewIds: [2, 3] }));
  assert.equal(parse('preview_set_view_phase', { phaseId: 1, viewIds: [2, 2] }), false);
});

test('sheet layout rejects incomplete views and reversed crop boxes', () => {
  const view={sourceViewId:1,viewName:'Plan A3',scale:50,centerXmm:200,centerYmm:165};
  const sheet={number:'АР-01',name:'План этажа',views:[view]};
  assert.ok(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[sheet]}));
  assert.ok(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[]}]}));
  assert.ok(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,centerXmm:600,centerYmm:400}]}]}));
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,scale:0}]}]}),false);
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,crop:{minX:1,maxX:0,minY:0,maxY:1,minZ:0,maxZ:1}}]}]}),false);
  assert.equal(parse('export_sheets_pdf',{sheetIds:[],path:'C:/out.pdf'}),false);
});

test('existing view placement accepts absolute negative coordinates and validates the whole batch', () => {
  const placement={sheetId:2798849,viewId:2792781,centerXmm:-250,centerYmm:400,viewportTypeId:12345};
  assert.ok(parse('list_sheet_resources',{}));
  assert.ok(parse('list_sheet_resources',{sheetId:placement.sheetId}));
  assert.equal(parse('list_sheet_resources',{sheetId:0}),false);
  assert.ok(parse('preview_place_views_on_sheets',{placements:[placement]}));
  assert.ok(parse('preview_place_views_on_sheets',{placements:[{...placement,viewportTypeId:undefined}]}));
  // A legend can be reused on distinct sheets; Revit decides whether each view is placeable.
  assert.ok(parse('preview_place_views_on_sheets',{placements:[placement,{...placement,sheetId:2}]}));
  for(const placements of [[],Array(51).fill(placement),[placement,placement],
    [{...placement,viewId:0}],[{...placement,viewportTypeId:-1}],
    [{...placement,centerXmm:Infinity}],[{...placement,centerYmm:NaN}],
    [{...placement,centerXmm:-100001}],[{...placement,sourceViewId:3}]])
    assert.equal(parse('preview_place_views_on_sheets',{placements}),false);
  assert.ok(parse('apply_place_views_on_sheets',{planId:'preview-plan'}));
  assert.equal(parse('apply_place_views_on_sheets',{planId:'preview-plan',placements:[placement]}),false);
  assert.equal(parse('apply_place_views_on_sheets',{}),false);
  for(const name of ['preview_place_views_on_sheets','apply_place_views_on_sheets'])
    assert.equal(contracts.get(name).annotations.readOnlyHint,false);
});

test('viewport titles can be edited without moving, rescaling or creating text notes', () => {
  const viewport={viewportId:2801470,viewportTypeId:12345,titleOnSheet:'В1',labelOffsetXmm:80,labelOffsetYmm:340,labelLineLengthMm:0};
  const accepts=p=>parse('preview_finish_sheet',{sheetId:2798849,viewports:[p]});
  assert.ok(accepts(viewport));
  for(const changes of [{titleOnSheet:''},{labelOffsetXmm:-80},{labelOffsetYmm:-340},{labelLineLengthMm:-20},
    {viewportId:1,titleOnSheet:'В2'},{viewportId:1,labelOffsetXmm:0},{viewportId:1,scale:100},
    {viewportId:1,xMm:250,yMm:400}]) assert.ok(accepts(changes.viewportId?changes:{...viewport,...changes}));
  for(const changes of [{viewportId:1},{viewportId:1,xMm:20},{viewportId:1,yMm:20},
    {...viewport,viewportTypeId:0},{...viewport,labelOffsetXmm:Infinity},{...viewport,labelOffsetYmm:NaN},
    {...viewport,labelLineLengthMm:100001},{...viewport,titleOnSheet:null},{...viewport,scale:1.5}])
    assert.equal(accepts(changes),false);
  assert.equal(parse('preview_finish_sheet',{sheetId:1,viewports:[viewport,viewport]}),false);
  assert.equal(parse('preview_finish_sheet',{sheetId:1,viewports:[],notes:[]}),false);
  assert.ok(parse('apply_finish_sheet',{planId:'preview-plan'}));
});

test('hosted placement requires one level selector and one sill selector', () => {
  const instance = { hostWallId: 1, familySymbolId: 2, levelId: 3, position: { xMeters: 1, yMeters: 2 } };
  assert.ok(parse('preview_place_hosted_families', { instances: [instance] }));
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, levelName: 'L1' }] }), false);
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, sillHeightMeters: 1, offsetMeters: 2 }] }), false);
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, hostWallId: Number.MAX_SAFE_INTEGER + 1 }] }), false);
});

test('graphics contracts reject incompatible fields, duplicates and missing targets', () => {
  assert.ok(parse('preview_element_graphics', { viewId: 1, action: 'reset_temporary' }));
  for (const args of [
    { viewId: 1, action: 'hide' },
    { viewId: 1, action: 'set_color', elementIds: [2] },
    { viewId: 1, action: 'hide', elementIds: [2, 2] },
    { viewId: 1, action: 'reset_temporary', elementIds: [2] },
    { viewId: 1, action: 'set_transparency', elementIds: [2], transparency: 101 }
  ]) assert.equal(parse('preview_element_graphics', args), false);
});

test('dimensions require complete line and at least two explicit references', () => {
  const dimension = { dimensionTypeId: 2, line: { start: { xMeters: 0, yMeters: 0, zMeters: 0 }, end: { xMeters: 2, yMeters: 0, zMeters: 0 } },
    references: [{ elementId: 3, stableReference: 'a' }, { elementId: 4, stableReference: 'b' }] };
  assert.ok(parse('preview_create_dimensions', { dimensions: [dimension] }));
  assert.equal(parse('preview_create_dimensions', { dimensions: [{ ...dimension, references: [dimension.references[0]] }] }), false);
  assert.equal(parse('preview_tag_rooms', { tagTypeId: 2, offsetUpPaperMillimeters: 101 }), false);
});
