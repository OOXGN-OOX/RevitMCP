// Opt-in integration check against an installed matching build and Revit 2026.
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { callRevit } from '../server/src/revit-bridge.js';

const [templatePath, projectTemplatePath, outputDirectory] = process.argv.slice(2);
if (!templatePath || !projectTemplatePath || !outputDirectory || ![templatePath,projectTemplatePath,outputDirectory].every(path.isAbsolute))
  throw new Error('Usage: node tests/family-revit.mjs <unhosted mechanical-equipment RFT> <MEP project RTE> <new output directory>');
await mkdir(outputDirectory); // Must not reuse a directory containing user files.
const hash = async file => createHash('sha256').update(await readFile(file)).digest('hex');
const missing = async file => { try { await stat(file); return false; } catch (e) { if (e.code === 'ENOENT') return true; throw e; } };
const options = await callRevit('get_family_authoring_options');
const hasProject = (await callRevit('revit_ping')).activeDocumentOpen;
const checkProjectPreview = async () => {
  if (!hasProject) return;
  // This legacy preview only reads levels and records a plan; it does not start
  // a transaction or change the active model. Never apply this diagnostic plan.
  const preview = await callRevit('preview_create_levels', {levels:[{name:'MCP lifecycle check '+Date.now(),elevationMeters:0}]});
  assert.equal(preview.planCreated,true,'Project previews remain usable after temporary documents close');
};
await checkProjectPreview();
const length = options.dataTypes.find(t => t.id.startsWith('autodesk.spec.aec:length-'));
assert.ok(length, 'Length spec is available');
const meters = length.units.find(u => u.id.startsWith('autodesk.unit.unit:meters-')).id;
const group = options.groups.find(g => g.id.startsWith('autodesk.parameter.group:geometry-')).id;
const value = (name,number) => ({parameter:{name},value:number,unitId:meters});
const zero = {x:0,y:0,z:0}, x = {x:1,y:0,z:0}, z = {x:0,y:0,z:1};
const source = path.join(outputDirectory,'ConnectorFixture.rfa');
const frame = {origin:zero,basisX:x,basisZ:z};
const connector = (key,origin,normal,direction) => ({action:'createHvacConnector',key,
  hostFace:{element:{key:'body'},normal,origin},shape:'Round',system:'SupplyAir',flowDirection:direction,
  frame:{origin,basisX:x,basisZ:normal},diameterMeters:.2,bindings:[{builtInParameter:'CONNECTOR_RADIUS',parameter:{name:'Radius'}}]});
const request = {templatePath,outputPath:source,operations:[
  {action:'addType',name:'Fixture 200'},
  {action:'addParameter',name:'Diameter',dataTypeId:length.id,groupId:group,isInstance:false},
  {action:'addParameter',name:'Radius',dataTypeId:length.id,groupId:group,isInstance:false,guid:'79348f4c-901d-49ed-a412-0cba55964672'},
  {action:'setValues',typeName:'Fixture 200',values:[value('Diameter',.2)]},
  {action:'setFormula',parameter:{name:'Radius'},formula:'Diameter / 2'},
  {action:'material',name:'Fixture material',color:[160,160,160]},
  {action:'subcategory',name:'Fixture body',materialName:'Fixture material'},
  {action:'extrusion',key:'body',solid:true,frame,loops:[[{x:-.2,y:-.2},{x:.2,y:-.2},{x:.2,y:.2},{x:-.2,y:.2}]],depthMeters:1,materialName:'Fixture material',subcategory:'Fixture body'},
  connector('inlet',zero,{x:0,y:0,z:-1},'In'), connector('outlet',z,z,'Out')
],validation:{testConnections:true,projectTemplatePath,expected:[{typeName:'Fixture 200',connectorCount:2,values:[value('Radius',.1)],sizeMeters:{x:.4,y:.4,z:1}}]}};
const preview = await callRevit('preview_build_family',request);
await checkProjectPreview();
assert.ok(preview.preview.verified.connections.passed);
assert.ok(await missing(source),'Preview must not create output');
const saved = await callRevit('apply_build_family',{planId:preview.planId});
assert.equal(saved.applied,true,'Confirm saving the test RFA in Revit');
const originalHash = await hash(source);
const catalog = path.join(outputDirectory,'Types.txt');
await writeFile(catalog,',Diameter##LENGTH##MILLIMETERS,Radius##LENGTH##MILLIMETERS,Absent##OTHER##\nFixture 300,300,999,ignored\n');
const edited = path.join(outputDirectory,'ConnectorFixture300.rfa');
const editRequest = {sourcePath:source,outputPath:edited,operations:[{action:'importCatalogType',path:catalog,rowName:'Fixture 300'}],validation:{testConnections:true,projectTemplatePath,expected:[{typeName:'Fixture 300',connectorCount:2,values:[value('Radius',.15)]}]}};
const edit = await callRevit('preview_build_family',editRequest);
assert.ok(edit.preview.fields.some(f=>f.parameter==='Radius'&&f.reason==='formula'));
assert.ok(edit.preview.fields.some(f=>f.parameter==='Absent'&&f.reason==='missing_or_ambiguous_parameter'));
await writeFile(catalog,(await readFile(catalog,'utf8')).replace('300,999','350,999'));
await assert.rejects(()=>callRevit('apply_build_family',{planId:edit.planId}),/changed/i);
assert.ok(await missing(edited),'Changed dependencies must prevent saving');
await writeFile(catalog,(await readFile(catalog,'utf8')).replace('350,999','300,999'));
const retry = await callRevit('preview_build_family',editRequest);
assert.equal((await callRevit('apply_build_family',{planId:retry.planId})).applied,true);
assert.equal(await hash(source),originalHash,'Source RFA is unchanged');
const reopened = await callRevit('validate_family_file',{path:edited,...editRequest.validation});
await checkProjectPreview();
assert.equal(reopened.passed,true);
assert.equal(reopened.connections.checkedConnectors,4);
console.log(JSON.stringify({passed:true,source,edited,originalHash,checkedConnectors:reopened.connections.checkedConnectors},null,2));
