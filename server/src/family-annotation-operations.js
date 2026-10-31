import * as z from 'zod/v4';
const id=z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const name=z.string().trim().min(1).max(200);
const number=z.number().finite();
const point=z.object({xMm:number.min(-100000).max(100000),yMm:number.min(-100000).max(100000)}).strict();
const element=z.union([z.object({id}).strict(),z.object({key:name}).strict()]);
const parameter=z.union([z.object({name}).strict(),z.object({guid:z.string().uuid()}).strict()]);
export const annotationCurve=z.discriminatedUnion('kind',[
  z.object({kind:z.literal('line'),start:point,end:point}).strict(),
  z.object({kind:z.literal('arc'),start:point,through:point,end:point}).strict(),
  z.object({kind:z.literal('circle'),center:point,radiusMm:number.positive().max(100000)}).strict()
]);
const style={lineStyle:element.optional()};
const text={position:point.optional(),widthMm:number.positive().max(10000).optional(),horizontal:z.enum(['Left','Center','Right']).optional(),vertical:z.enum(['Top','Middle','Bottom']).optional(),textType:element.optional()};
const boundaries=z.array(z.array(annotationCurve).min(1).max(200)).min(1).max(20);
export const annotationOperations=[
  z.object({action:z.literal('annotationRegionStyle'),key:name,baseType:element,name,foregroundPatternId:id.optional(),foregroundColor:z.tuple([z.number().int().min(0).max(255),z.number().int().min(0).max(255),z.number().int().min(0).max(255)]).optional(),backgroundPatternId:id.optional(),backgroundColor:z.tuple([z.number().int().min(0).max(255),z.number().int().min(0).max(255),z.number().int().min(0).max(255)]).optional(),lineWeight:z.number().int().min(1).max(16).optional()}).strict(),
  z.object({action:z.literal('annotationLineStyle'),key:name,name,lineWeight:z.number().int().min(1).max(16),color:z.tuple([z.number().int().min(0).max(255),z.number().int().min(0).max(255),z.number().int().min(0).max(255)]).optional(),linePatternId:id.optional()}).strict(),
  z.object({action:z.literal('annotationCurve'),key:name,viewId:id,curve:annotationCurve,mode:z.enum(['detail','symbolic']).default('detail'),...style}).strict(),
  z.object({action:z.literal('editAnnotationCurve'),element,viewId:id,curve:annotationCurve,...style}).strict(),
  z.object({action:z.literal('annotationTextStyle'),key:name,baseType:element,name,font:name.optional(),sizeMm:number.positive().max(100).optional(),bold:z.boolean().optional(),italic:z.boolean().optional(),underline:z.boolean().optional(),opaque:z.boolean().optional()}).strict(),
  z.object({action:z.literal('annotationText'),key:name,viewId:id,text:z.string().min(1).max(16000),...text,position:point,textType:element}).strict(),
  z.object({action:z.literal('editAnnotationText'),element,viewId:id,text:z.string().min(1).max(16000).optional(),...text}).strict(),
  z.object({action:z.literal('annotationRegion'),key:name,viewId:id,masking:z.boolean().default(false),regionType:element.optional(),boundaries,...style}).strict(),
  z.object({action:z.literal('replaceAnnotationRegion'),key:name,element,viewId:id,masking:z.boolean().default(false),regionType:element.optional(),boundaries,...style,allowDependentDeletion:z.boolean().default(false)}).strict(),
  z.object({action:z.literal('annotationDimension'),key:name,viewId:id,start:point,end:point,references:z.array(element).min(2).max(20),parameter:parameter.optional(),locked:z.boolean().optional()}).strict(),
  z.object({action:z.literal('setAnnotationDimension'),element,parameter:parameter.nullable().optional(),locked:z.boolean().optional()}).strict()
];
export function refineAnnotation(op,ctx){
  if(['annotationRegion','replaceAnnotationRegion'].includes(op.action)&&!op.masking&&!op.regionType)
    ctx.addIssue({code:'custom',message:'A filled region needs an explicit regionType; masking regions do not.'});
  if(op.action==='editAnnotationText'&&!Object.keys(op).some(k=>!['action','element','viewId'].includes(k)))ctx.addIssue({code:'custom',message:'Specify at least one text/layout change.'});
  if(op.action==='setAnnotationDimension'&&op.parameter===undefined&&op.locked===undefined)ctx.addIssue({code:'custom',message:'Specify parameter or locked.'});
}
export const annotationExamples={
  annotationRegionStyle:{action:'annotationRegionStyle',key:'fillStyle',baseType:{id:208},name:'Fill',foregroundPatternId:209,foregroundColor:[120,120,120]},
  annotationLineStyle:{action:'annotationLineStyle',key:'outline',name:'Outline',lineWeight:2},
  annotationCurve:{action:'annotationCurve',key:'line',viewId:101,curve:{kind:'line',start:{xMm:0,yMm:0},end:{xMm:20,yMm:0}}},
  editAnnotationCurve:{action:'editAnnotationCurve',element:{id:201},viewId:101,curve:{kind:'circle',center:{xMm:0,yMm:0},radiusMm:5}},
  annotationTextStyle:{action:'annotationTextStyle',key:'textStyle',baseType:{id:202},name:'Text 2.5',sizeMm:2.5},
  annotationText:{action:'annotationText',key:'caption',viewId:101,text:'Caption',position:{xMm:0,yMm:0},textType:{id:202}},
  editAnnotationText:{action:'editAnnotationText',element:{id:203},viewId:101,widthMm:25,horizontal:'Center'},
  annotationRegion:{action:'annotationRegion',key:'mask',viewId:101,masking:true,boundaries:[[{kind:'circle',center:{xMm:0,yMm:0},radiusMm:5}]]},
  replaceAnnotationRegion:{action:'replaceAnnotationRegion',key:'replacement',element:{id:204},viewId:101,masking:true,boundaries:[[{kind:'circle',center:{xMm:0,yMm:0},radiusMm:6}]]},
  annotationDimension:{action:'annotationDimension',key:'width',viewId:101,start:{xMm:0,yMm:10},end:{xMm:20,yMm:10},references:[{id:205},{id:206}],parameter:{name:'Width'}},
  setAnnotationDimension:{action:'setAnnotationDimension',element:{id:207},locked:true}
};
