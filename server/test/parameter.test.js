import test from 'node:test';
import assert from 'node:assert/strict';
import {registerParameterTools} from '../src/parameter-tools.js';
const tools=new Map();
registerParameterTools((name,description,schema)=>tools.set(name,schema));
const valid=(name,args)=>tools.get(name).safeParse(args).success;

test('parameter writes accept typed references and keep selectors and values unambiguous',()=>{
  const base={elementIds:[1],parameterName:'Питание'};
  assert.ok(valid('preview_set_parameter',{...base,elementIdValue:100}));
  assert.ok(valid('preview_set_parameter',{...base,elementIdValue:-1}));
  assert.ok(valid('preview_set_parameter',{...base,value:'9223372036854775807'}));
  assert.ok(valid('preview_set_parameter',{elementIds:[1],parameterId:-123,parameterScope:'type',elementIdValue:100}));
  assert.ok(valid('preview_set_parameter',{elementIds:[1],parameterGuid:'b7f6a06a-cd31-4ff5-b92b-0840e6153562',value:''}));
  for(const request of [{...base},{...base,value:'1',elementIdValue:1},{...base,elementIdValue:0},{...base,elementIdValue:1.5},
    {...base,elementIdValue:Number.MAX_SAFE_INTEGER+1},{...base,elementIdValue:'1'},
    {...base,parameterId:2,elementIdValue:1},{...base,parameterScope:'both',elementIdValue:1},
    {...base,elementIds:[1,1],elementIdValue:1},{...base,value:100},{elementIds:[1],elementIdValue:1}])
    assert.equal(valid('preview_set_parameter',request),false);
  assert.equal(valid('apply_parameter_plan',{planId:'plan',elementIdValue:1}),false,'apply must use the bound preview value');
});
test('parameter option discovery is scoped and paginated',()=>{
  assert.ok(valid('get_parameter_value_options',{elementId:1,parameterName:'Питание',parameterScope:'type'}));
  assert.ok(valid('get_parameter_value_options',{elementId:1,parameterId:100,offset:100,limit:500}));
  for(const request of [{elementId:1},{elementId:1,parameterName:'Питание',parameterId:100},{elementId:1,parameterName:'Питание',limit:501}])
    assert.equal(valid('get_parameter_value_options',request),false);
});
