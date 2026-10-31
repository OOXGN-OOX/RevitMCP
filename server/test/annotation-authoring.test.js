import test from 'node:test';
import assert from 'node:assert/strict';
import {registerFamilyTools,familyOperationCatalog} from '../src/family-tools.js';
import {registerAnnotationEditTools} from '../src/annotation-edit-tools.js';
const schemas=new Map();for(const r of [registerFamilyTools,registerAnnotationEditTools])r((n,d,s)=>schemas.set(n,s));
const valid=(n,a)=>schemas.get(n).safeParse(a).success;
const build=op=>({sourcePath:'C:/Families/tag.rfa',outputPath:'C:/Families/new.rfa',operations:[op]});
test('every catalog example parses and every operation exposes its complete JSON schema',()=>{
  const all=familyOperationCatalog();
  assert.equal(all.operations.length,34);assert.equal(all.capabilities.labelCompositionWrite,false);
  for(const op of all.operations){
    assert.ok(op.example,op.action);assert.ok(valid('preview_build_family',build(op.example)),op.action);
    const detailed=familyOperationCatalog({action:op.action}).operations[0];
    assert.equal(detailed.schema.type,'object');assert.equal(detailed.schema.properties.action.const,op.action);
    assert.ok(detailed.schema.required.includes('action'));assert.ok(detailed.categories.length);assert.ok(detailed.constraints.length);
  }
  assert.throws(()=>familyOperationCatalog({action:'createLabel'}));
});
test('annotation authoring rejects missing owners, views, styles and unsupported label composition',()=>{
  const curve={action:'annotationCurve',key:'line',viewId:1,curve:{kind:'arc',start:{xMm:0,yMm:0},through:{xMm:5,yMm:5},end:{xMm:10,yMm:0}}};
  assert.ok(valid('preview_build_family',build(curve)));
  for(const op of [{...curve,viewId:undefined},{...curve,curve:{...curve.curve,kind:'spline'}},
    {action:'annotationRegion',key:'region',viewId:1,boundaries:[[{kind:'circle',center:{xMm:0,yMm:0},radiusMm:5}]]},
    {action:'editAnnotationText',element:{id:1},viewId:2},{action:'setAnnotationDimension',element:{id:1}},
    {action:'createLabel',parameters:[{name:'System'}]},
    {action:'annotationText',key:'text',viewId:1,text:'Caption',position:{xMm:0,yMm:0},textType:{id:2},labelFields:[]}])
    assert.equal(valid('preview_build_family',build(op)),false);
  assert.ok(valid('read_family_annotations',{path:'C:/Families/tag.rfa',typeName:'Type A',limit:500}));
  assert.equal(valid('read_family_annotations',{path:'C:/Families/tag.rfa',limit:501}),false);
});
test('project annotations and tag editing have explicit bounded contracts',()=>{
  assert.ok(valid('preview_edit_view_annotations',{operations:[{action:'placeGenericAnnotation',key:'a',viewId:1,typeId:2,position:{xMm:10,yMm:20}}]}));
  assert.equal(valid('preview_edit_view_annotations',{operations:[{action:'setAnnotationDimension',element:{id:1},locked:true}]}),false);
  assert.ok(valid('preview_edit_tags',{tags:[{tagId:1,headPosition:{xMeters:0,yMeters:1,zMeters:0},hasLeader:true,leaderEndCondition:'Free',leaders:[{referenceIndex:0,endPosition:{xMeters:0,yMeters:0,zMeters:0}}]}]}));
  for(const tags of [[{tagId:1}],[{tagId:1,hasLeader:true},{tagId:1,hasLeader:false}],[{tagId:1,leaders:[{referenceIndex:0},{referenceIndex:0}]}],
    [{tagId:1,removeReferenceIndices:[0,0]}],[{tagId:1,addLocalElementIds:[2,2]}]])assert.equal(valid('preview_edit_tags',{tags}),false);
  assert.equal(valid('apply_edit_tags',{planId:'p',tags:[]}),false);
});
