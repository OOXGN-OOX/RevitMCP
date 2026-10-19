import test from 'node:test';
import assert from 'node:assert/strict';
import { registerFamilyTools } from '../src/family-tools.js';
import { registerAuthoringTools } from '../src/authoring-tools.js';

const schemas = new Map();
for (const register of [registerFamilyTools, registerAuthoringTools]) register((name, description, schema) => schemas.set(name, schema));
const valid = (tool, value) => schemas.get(tool).safeParse(value).success;
const frame = { origin: {x:0,y:0,z:0}, basisX: {x:1,y:0,z:0}, basisZ: {x:0,y:0,z:1} };
const plan = { sourcePath:'C:/Families/source.rfa', outputPath:'C:/Families/new.rfa', operations:[{action:'addType',name:'100'}] };
test('family build preserves source and requires one source and a bounded batch', () => {
  assert.ok(valid('preview_build_family',plan));
  for (const input of [ {...plan,outputPath:'c:/families/SOURCE.rfa'}, {...plan,templatePath:'C:/template.rft'}, {...plan,sourcePath:undefined}, {...plan,outputPath:'relative.rfa'}, {...plan,operations:[]}, {...plan,operations:Array(201).fill(plan.operations[0])}, {...plan,overwrite:true} ])
    assert.equal(valid('preview_build_family',input),false,JSON.stringify(input));
});
test('HVAC connector schema requires matching shape dimensions and orthogonal frame', () => {
  const connector = {action:'createHvacConnector', key:'outlet',hostFace:{element:{id:12},normal:frame.basisZ},shape:'Round',system:'SupplyAir',flowDirection:'Out',frame,diameterMeters:.2};
  const check = op => valid('preview_build_family',{...plan,operations:[op]});
  assert.ok(check(connector));
  assert.ok(check({...connector,shape:'Rectangular',diameterMeters:undefined,widthMeters:.4,heightMeters:.2}));
  for (const op of [{...connector,diameterMeters:undefined}, {...connector,widthMeters:.4}, {...connector,diameterMeters:-1}, {...connector,frame:{...frame,basisX:frame.basisZ}}, {...connector,frame:{...frame,basisZ:{x:0,y:0,z:0}}}]) assert.equal(check(op),false);
});
test('family values and bindings reject ambiguous selectors', () => {
  const value = {parameter:{name:'Width'},value:200,unitId:'autodesk.unit.unit:millimeters-1.0.1'};
  const check = v => valid('preview_build_family',{...plan,operations:[{action:'setValues',typeName:'100',values:[v]}]});
  assert.ok(check(value));
  assert.equal(check({...value,materialName:'Steel'}),false);
  assert.equal(check({...value,parameter:{name:'Width',guid:'79348f4c-901d-49ed-a412-0cba55964672'}}),false);
  assert.equal(check({...value,value:Infinity}),false);
  assert.ok(valid('preview_build_family',{...plan,operations:[{action:'associateParameter',element:{id:1},builtInParameter:'CONNECTOR_RADIUS',parameter:null}]}));
});
test('catalog rows remain exact and reload policy is explicit', () => {
  const parsed = schemas.get('read_family_type_catalog').parse({path:'C:/Types.txt',rowName:' Exact type '});
  assert.equal(parsed.rowName,' Exact type ');
  assert.equal(valid('preview_load_family',{path:'C:/test.rfa'}),false);
  for (const parameterPolicy of ['rejectExisting','preserve','overwrite']) assert.ok(valid('preview_load_family',{path:'C:/test.rfa',parameterPolicy,sharedFamilySource:'project'}));
  assert.equal(valid('validate_family_file',{path:'C:/test.rfa',testConnections:true,projectTemplatePath:'C:/live.rvt'}),false);
});
