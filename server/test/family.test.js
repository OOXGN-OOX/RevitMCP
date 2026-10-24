import test from 'node:test';
import assert from 'node:assert/strict';
import { registerFamilyTools } from '../src/family-tools.js';
import { registerAuthoringTools } from '../src/authoring-tools.js';

const schemas = new Map();
for (const register of [registerFamilyTools, registerAuthoringTools]) register((name, description, schema) => schemas.set(name, schema));
const valid = (tool, value) => schemas.get(tool).safeParse(value).success;
const frame = { origin: {x:0,y:0,z:0}, basisX: {x:1,y:0,z:0}, basisZ: {x:0,y:0,z:1} };
const plan = { sourcePath:'C:/Families/source.rfa', outputPath:'C:/Families/new.rfa', operations:[{action:'addType',name:'100'}] };
test('family reload accepts bounded expected changes with exact instance identity', () => {
  const request={path:'C:/Families/Unit.rfa',parameterPolicy:'preserve',sharedFamilySource:'file'};
  const item={instanceId:10,uniqueId:'instance-a',nestedType:{before:{family:'Original',typeId:11,type:'A'},after:{family:'Prepared',type:'A'}},
    systemClassification:{before:'Power',after:''},removeUnconnectedElectricalConnectors:[1]};
  assert.ok(valid('preview_load_family',{...request,expectedChanges:[item]}));
  assert.ok(valid('preview_load_family',{...request,expectedChanges:[{instanceId:12,uniqueId:'instance-b',connectorClassifications:[{connectorId:0,before:'SupplyAir',after:'UndefinedSystemType'}],logicalSystemReferences:[{connectorId:2,systemId:20}]}]}));
  for(const expectedChanges of [[],[item,item],[{instanceId:10,uniqueId:'instance-a'}],[{...item,uniqueId:undefined}],
    [{...item,ignorePlacement:true}],[{...item,removeUnconnectedElectricalConnectors:[1,1]}],
    [{...item,connectorClassifications:[{connectorId:1,before:'SupplyAir',after:'SupplyAir'}]}]])
    assert.equal(valid('preview_load_family',{...request,expectedChanges}),false);
  assert.equal(valid('apply_load_family',{planId:'plan',expectedChanges:[item]}),false,'Apply must use the immutable preview contract');
});
test('family export requires exact, exclusive selectors and new absolute RFA paths', () => {
  const project={familyId:12,outputPath:'C:/Families/Unit.rfa'};
  const nested={sourcePath:'C:/Families/source.rfa',nestedFamilyId:23,outputPath:'C:/Families/neutral.rfa'};
  assert.ok(valid('preview_export_family',project));
  assert.ok(valid('preview_export_family',{familyName:' Unit ',outputPath:project.outputPath}));
  assert.equal(schemas.get('preview_export_family').parse({familyName:' Unit ',outputPath:project.outputPath}).familyName,' Unit ');
  for (const value of [{outputPath:project.outputPath},{...project,familyName:'Unit'},{...project,familyId:0},
    {...project,familyId:1.5},{...project,outputPath:'relative.rfa'},{...project,outputPath:'C:/out.rvt'},{...project,overwrite:true}])
    assert.equal(valid('preview_export_family',value),false,JSON.stringify(value));
  assert.ok(valid('preview_export_nested_family',nested));
  assert.ok(valid('preview_export_nested_family',{sourcePath:nested.sourcePath,nestedFamilyName:' Exact ',outputPath:nested.outputPath}));
  for (const value of [{...nested,nestedFamilyName:'Unit'},{...nested,nestedFamilyId:undefined},{...nested,sourcePath:'C:/live.rvt'},
    {...nested,outputPath:'c:\\families\\SOURCE.rfa'},{...nested,nestedFamilyId:-1},{...nested,overwrite:true}])
    assert.equal(valid('preview_export_nested_family',value),false,JSON.stringify(value));
  for (const tool of ['apply_export_family','apply_export_nested_family']) {
    assert.ok(valid(tool,{planId:'reviewed'}));
    assert.equal(valid(tool,{planId:'reviewed',outputPath:'C:/unreviewed.rfa'}),false);
    assert.equal(valid(tool,{planId:''}),false);
  }
});
test('family build preserves source and requires one source and a bounded batch', () => {
  assert.ok(valid('preview_build_family',plan));
  for (const input of [ {...plan,outputPath:'c:/families/SOURCE.rfa'}, {...plan,templatePath:'C:/template.rft'}, {...plan,sourcePath:undefined}, {...plan,outputPath:'relative.rfa'}, {...plan,operations:[]}, {...plan,operations:Array(201).fill(plan.operations[0])}, {...plan,overwrite:true} ])
    assert.equal(valid('preview_build_family',input),false,JSON.stringify(input));
});
test('HVAC connector schema requires matching shape dimensions and orthogonal frame', () => {
  const connector = {action:'createHvacConnector', key:'outlet',hostFace:{element:{id:12},normal:frame.basisZ},shape:'Round',system:'SupplyAir',flowDirection:'Out',frame,diameterMeters:.2};
  const check = op => valid('preview_build_family',{...plan,operations:[op]});
  assert.ok(check(connector));
  assert.ok(check({...connector,system:'Global'}));
  const {key,hostFace,...editable}=connector;
  assert.ok(check({...editable,action:'editHvacConnector',element:{id:12},system:'Global'}));
  assert.ok(check({...connector,shape:'Rectangular',diameterMeters:undefined,widthMeters:.4,heightMeters:.2}));
  for (const op of [{...connector,diameterMeters:undefined}, {...connector,widthMeters:.4}, {...connector,diameterMeters:-1}, {...connector,system:'UndefinedSystemType'}, {...connector,frame:{...frame,basisX:frame.basisZ}}, {...connector,frame:{...frame,basisZ:{x:0,y:0,z:0}}}]) assert.equal(check(op),false);
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

test('family rename requires an explicit element id and a bounded nonempty name', () => {
  const request = {elementId:123,newName:'ОВ_Клапан 200×100'};
  assert.ok(valid('preview_rename_family',request));
  assert.equal(schemas.get('preview_rename_family').parse({...request,newName:' Тип 1 '}).newName,'Тип 1');
  for (const invalid of [{...request,elementId:0},{...request,elementId:1.5},{...request,elementId:'123'},
    {...request,newName:' '},{...request,newName:'x'.repeat(201)},{...request,newName:'A\nB'},
    {...request,instanceId:12},{newName:'New'},{elementId:123}]) assert.equal(valid('preview_rename_family',invalid),false);
  assert.ok(valid('apply_rename_family',{planId:'preview-id'}));
  assert.equal(valid('apply_rename_family',request),false);
  assert.equal(valid('apply_rename_family',{planId:'preview-id',newName:'unpreviewed'}),false);
});
