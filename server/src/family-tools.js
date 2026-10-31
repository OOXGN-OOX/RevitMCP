import * as z from 'zod/v4';
import {annotationOperations,refineAnnotation,annotationExamples} from './family-annotation-operations.js';

const name = z.string().trim().min(1).max(200);
const file = ext => z.string().min(4).max(240).refine(p => /^[A-Za-z]:[\\/]/.test(p) && p.toLowerCase().endsWith(ext), `Use an absolute local ${ext} path.`);
const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const number = z.number().finite();
const point = z.object({ x: number.min(-1000).max(1000), y: number.min(-1000).max(1000), z: number.min(-1000).max(1000) }).strict();
const vector = point.refine(p => Math.hypot(p.x, p.y, p.z) > 1e-9, 'A direction must be nonzero.');
const frame = z.object({ origin: point, basisX: vector, basisZ: vector }).strict().refine(f => Math.abs(f.basisX.x*f.basisZ.x + f.basisX.y*f.basisZ.y + f.basisX.z*f.basisZ.z) / Math.hypot(f.basisX.x,f.basisX.y,f.basisX.z) / Math.hypot(f.basisZ.x,f.basisZ.y,f.basisZ.z) < 1e-6, 'Frame axes must be perpendicular.');
const element = z.union([z.object({ id }).strict(), z.object({ key: name }).strict()]);
const parameter = z.union([z.object({ name }).strict(), z.object({ guid: z.string().uuid() }).strict()]);
const value = z.object({ parameter, value: z.union([number, z.string().max(4000), z.boolean()]).optional(), unitId: z.string().max(200).optional(), materialName: name.optional(), elementId: id.optional() }).strict()
  .refine(v => [v.value !== undefined, v.materialName !== undefined, v.elementId !== undefined].filter(Boolean).length === 1, 'Provide exactly one value, materialName or elementId.');
const visibility = z.object({ visible: z.boolean().optional(), coarse: z.boolean().optional(), medium: z.boolean().optional(), fine: z.boolean().optional(), plan: z.boolean().optional(), frontBack: z.boolean().optional(), leftRight: z.boolean().optional() }).strict();
const face = z.object({ element, normal: vector, origin: point.optional() }).strict();
const binding = z.object({ builtInParameter: name, parameter }).strict();
const connectorFields = {
  shape: z.enum(['Round','Rectangular','Oval']), system: z.enum(['SupplyAir','ReturnAir','ExhaustAir','OtherAir','Global']),
  flowDirection: z.enum(['In','Out','Bidirectional']), frame,
  diameterMeters: number.positive().max(10).optional(), widthMeters: number.positive().max(10).optional(), heightMeters: number.positive().max(10).optional(),
  bindings: z.array(binding).max(20).default([]), primary: z.boolean().default(false)
};
const operationSchemas = [
  z.object({ action: z.literal('addParameter'), name, dataTypeId: name, groupId: name, isInstance: z.boolean(), guid: z.string().uuid().optional(), description: z.string().max(1000).optional() }).strict(),
  z.object({ action: z.literal('setFormula'), parameter, formula: z.string().max(4000).nullable() }).strict(),
  z.object({ action: z.literal('addType'), name, values: z.array(value).max(500).default([]) }).strict(),
  z.object({ action: z.literal('setValues'), typeName: name, values: z.array(value).min(1).max(500) }).strict(),
  z.object({ action: z.literal('renameType'), typeName: name, name }).strict(),
  z.object({ action: z.literal('removeParameter'), parameter }).strict(),
  z.object({ action: z.literal('renameParameter'), parameter, name }).strict(),
  z.object({ action: z.literal('importCatalogType'), path: file('.txt'), encoding: z.enum(['utf8','utf16','windows1251']).default('utf8'), rowName: z.string().min(1).max(200), newTypeName: name.optional() }).strict(),
  z.object({ action: z.literal('material'), name, color: z.tuple([z.number().int().min(0).max(255),z.number().int().min(0).max(255),z.number().int().min(0).max(255)]).optional(), transparency: z.number().int().min(0).max(100).optional() }).strict(),
  z.object({ action: z.literal('subcategory'), name, materialName: name.optional() }).strict(),
  z.object({ action: z.literal('extrusion'), key: name, solid: z.boolean(), frame, loops: z.array(z.array(z.object({ x: number.min(-1000).max(1000), y: number.min(-1000).max(1000) }).strict()).min(3).max(200)).min(1).max(20), depthMeters: number.positive().max(1000), materialName: name.optional(), subcategory: name.optional(), visibility: visibility.optional() }).strict(),
  z.object({ action: z.literal('referencePlane'), key: name, name, bubble: point, free: point, cutVector: vector }).strict(),
  z.object({ action: z.literal('cut'), solid: element, void: element }).strict(),
  z.object({ action: z.literal('setVisibility'), element, visibility }).strict(),
  z.object({ action: z.literal('associateParameter'), element, builtInParameter: name.optional(), parameterName: name.optional(), parameter: parameter.nullable() }).strict(),
  z.object({ action: z.literal('createHvacConnector'), key: name, hostFace: face, ...connectorFields }).strict(),
  z.object({ action: z.literal('editHvacConnector'), element, hostFace: face.optional(), ...connectorFields }).strict(),
  z.object({ action: z.literal('deleteElement'), element, allowDependentDeletion: z.boolean().default(false) }).strict(),
  z.object({ action: z.literal('loadNestedFamily'), key: name, path: file('.rfa'), shared: z.boolean().optional() }).strict(),
  z.object({ action: z.literal('useNestedFamily'), key: name, familyId: id }).strict(),
  z.object({ action: z.literal('placeNestedFamily'), key: name, familyKey: name, typeName: name, position: point, rotationDegrees: number.min(-360).max(360).default(0), hostFace: face.optional(), visibility: visibility.optional(), bindings: z.array(binding).max(100).default([]) }).strict(),
  z.object({ action: z.literal('setNestedType'), element, familyKey: name, typeName: name }).strict(),
  z.object({ action: z.literal('setFamilyShared'), shared: z.boolean() }).strict(),
  ...annotationOperations
];
const operations=z.discriminatedUnion('action',operationSchemas).superRefine((op, ctx) => {
  refineAnnotation(op,ctx);
  if (['createHvacConnector','editHvacConnector'].includes(op.action)) {
    if (op.shape === 'Round' ? op.diameterMeters === undefined || op.widthMeters !== undefined || op.heightMeters !== undefined : op.diameterMeters !== undefined || op.widthMeters === undefined || op.heightMeters === undefined)
      ctx.addIssue({ code:'custom',message:'Round needs diameter; rectangular/oval need width and height.' });
  }
  if (op.action === 'associateParameter' && (op.builtInParameter === undefined) === (op.parameterName === undefined))
    ctx.addIssue({code:'custom',message:'Specify one element parameter selector.'});
});
const expected = z.object({ typeName: name, connectorCount: z.number().int().min(0).max(200).optional(), values: z.array(value).max(500).default([]), sizeMeters: point.optional(), toleranceMeters: number.positive().max(0.1).default(0.0001) }).strict();
const validation = z.object({ expected: z.array(expected).max(200).default([]), testConnections: z.boolean().default(false), projectTemplatePath: file('.rte').optional() }).strict();
const write = { readOnlyHint:false, destructiveHint:false, openWorldHint:false };
const exampleFrame={origin:{x:0,y:0,z:0},basisX:{x:1,y:0,z:0},basisZ:{x:0,y:0,z:1}};
const examples={
  addParameter:{action:'addParameter',name:'Width',dataTypeId:'autodesk.spec.aec:length-2.0.0',groupId:'autodesk.parameter.group:geometry-1.0.0',isInstance:false},
  setFormula:{action:'setFormula',parameter:{name:'Radius'},formula:'Diameter / 2'},
  addType:{action:'addType',name:'Type A',values:[]},setValues:{action:'setValues',typeName:'Type A',values:[{parameter:{name:'Width'},value:20,unitId:'autodesk.unit.unit:millimeters-1.0.1'}]},
  renameType:{action:'renameType',typeName:'Type A',name:'Type B'},removeParameter:{action:'removeParameter',parameter:{name:'Unused'}},renameParameter:{action:'renameParameter',parameter:{name:'Old'},name:'New'},
  importCatalogType:{action:'importCatalogType',path:'C:/Families/types.txt',rowName:'Type A'},
  material:{action:'material',name:'Steel',color:[100,100,100]},subcategory:{action:'subcategory',name:'Outline'},
  extrusion:{action:'extrusion',key:'body',solid:true,frame:exampleFrame,loops:[[{x:0,y:0},{x:.1,y:0},{x:.1,y:.1},{x:0,y:.1}]],depthMeters:.1},
  referencePlane:{action:'referencePlane',key:'datum',name:'Left',bubble:{x:0,y:0,z:0},free:{x:0,y:1,z:0},cutVector:{x:0,y:0,z:1}},
  cut:{action:'cut',solid:{id:101},void:{id:102}},setVisibility:{action:'setVisibility',element:{id:101},visibility:{visible:false}},
  associateParameter:{action:'associateParameter',element:{id:101},builtInParameter:'IS_VISIBLE_PARAM',parameter:{name:'ShowFrame'}},
  createHvacConnector:{action:'createHvacConnector',key:'port',hostFace:{element:{id:101},normal:{x:0,y:0,z:1}},shape:'Round',system:'SupplyAir',flowDirection:'Out',frame:exampleFrame,diameterMeters:.1},
  editHvacConnector:{action:'editHvacConnector',element:{id:101},shape:'Round',system:'Global',flowDirection:'Out',frame:exampleFrame,diameterMeters:.1},
  deleteElement:{action:'deleteElement',element:{id:101}},loadNestedFamily:{action:'loadNestedFamily',key:'nested',path:'C:/Families/nested.rfa'},
  useNestedFamily:{action:'useNestedFamily',key:'nested',familyId:101},placeNestedFamily:{action:'placeNestedFamily',key:'instance',familyKey:'nested',typeName:'Type A',position:{x:0,y:0,z:0}},
  setNestedType:{action:'setNestedType',element:{id:101},familyKey:'nested',typeName:'Type B'},setFamilyShared:{action:'setFamilyShared',shared:true},...annotationExamples
};
export function familyOperationCatalog(args={}) {
  const all=operationSchemas.map(schema=>{
    const action=schema.shape.action.value;
    const annotation=annotationOperations.includes(schema);
    return {action,categories:annotation?['annotation families','detail component families','view-specific graphics where Revit permits']:['family template dependent'],
      units:annotation?'view-local paper millimeters':'meters; numeric parameters use unitId',
      constraints:annotation?['explicit viewId from read_family_annotations','public Revit 2026 API only','Label field composition is unavailable','text content edits require TextNote; existing Label permits layout/style only']:['source RFA or category-compatible RFT required','native validation and SaveAs/reopen checks'],
      schema:z.toJSONSchema(schema,{target:'draft-7',io:'input'}),example:examples[action]};
  });
  if(args.action&&!all.some(op=>op.action===args.action))throw new Error('Unknown build_family action: '+args.action);
  return {catalogVersion:1,revitVersion:'2026',operations:args.action?all.filter(op=>op.action===args.action):all.map(({schema,...op})=>op),
    schemaIncluded:!!args.action,capabilities:{labelCompositionRead:false,labelCompositionWrite:false,labelLayoutAndTextStyle:true,
      labelLimitation:'Revit 2026 public API does not expose label field order, parameter composition, prefixes, suffixes, separators, breaks or unit overrides. Existing labels are preserved as opaque TextElements.'},
    validationSchema:z.toJSONSchema(validation,{target:'draft-7',io:'input'}),
    limits:{operations:200},notes:['Request each action for its complete JSON Schema; examples use placeholder IDs.','Refinements: Round connector requires only diameter; rectangular/oval requires width and height.','associateParameter needs exactly one builtInParameter or parameterName.','Filled regions require regionType; masking regions do not.']};
}
export function registerFamilyTools(register) {
  register('get_family_operation_catalog','Машиночитаемый каталог build_family: названия, JSON Schema, примеры, категории и ограничения Revit 2026. Работает без Revit. Запрос action возвращает одну операцию; без action — список. Состав Label недоступен в публичном API 2026.',z.object({action:name.optional()}).strict());
  const exactName = z.string().min(1).max(200);
  const projectExport = z.object({ familyId:id.optional(), familyName:exactName.optional(), outputPath:file('.rfa') }).strict()
    .refine(v => (v.familyId !== undefined) !== (v.familyName !== undefined), 'Choose familyId or an exact familyName.');
  const nestedExport = z.object({ sourcePath:file('.rfa'), nestedFamilyId:id.optional(), nestedFamilyName:exactName.optional(), outputPath:file('.rfa') }).strict()
    .refine(v => (v.nestedFamilyId !== undefined) !== (v.nestedFamilyName !== undefined), 'Choose nestedFamilyId or an exact nestedFamilyName.')
    .refine(v => v.sourcePath.replaceAll('\\','/').toLowerCase() !== v.outputPath.replaceAll('\\','/').toLowerCase(), 'Output must differ from source.');
  register('preview_export_family', 'Выгрузить актуальное загруженное Family через EditFamily: проверить сохранение и повторное открытие RFA, пробную обратную загрузку с откатом и сохранность экземпляров/MEP-соединений. Выбрать familyId или точное familyName. Имя файла должно совпадать с именем семейства для обратной загрузки. Конечный файл не создаётся.', projectExport, write);
  register('apply_export_family', 'Повторить проверенную выгрузку семейства из неизменённого проекта и сохранить новый RFA без перезаписи. Проект не меняется; временные документы закрываются.', z.object({planId:name}).strict(), write);
  register('preview_export_nested_family', 'Скрыто открыть sourcePath, выбрать непосредственное вложенное Family по nestedFamilyId или точному nestedFamilyName, EditFamily, сохранить и проверить копию и обратную загрузку в источник с откатом. Исходник и конечный outputPath не меняются. Имя выходного файла может быть нейтральным.', nestedExport, write);
  register('apply_export_nested_family', 'Выгрузить вложенное семейство по проверенному плану в новый RFA. Проверить SHA-256 исходника; закрыть оба временных документа. Файлы не перезаписываются.', z.object({planId:name}).strict(), write);
  register('get_family_authoring_options', 'Получить допустимые типы данных, единицы и группы параметров Revit для разработки RFA.', z.object({}).strict());
  register('read_family_annotations','Скрыто открыть RFA и прочитать TextElement/Label, постоянный текст, кривые, области, размеры, стили, виды и GUID общих параметров. Доступные свойства меток читаются явно; их состав и порядок полей недоступны в API 2026. Ошибки и неполнота выдаются отдельно. Документ закрывается в finally.',z.object({path:file('.rfa'),typeName:name.optional(),viewId:id.optional(),offset:z.number().int().min(0).default(0),limit:z.number().int().min(1).max(500).default(100)}).strict());
  register('read_family_file', 'Открыть локальный RFA без окон и вернуть категорию, размещение, все параметры и типы, геометрию, материалы, вложения и полные системы координат HVAC-разъёмов. Документ закрывается без сохранения в finally. Координаты в метрах.', z.object({ path:file('.rfa'), typeNames:z.array(name).max(200).optional(), mesh:z.boolean().default(false), maxTriangles:z.number().int().min(1).max(10000).default(1000) }).strict());
  register('read_family_type_catalog', 'Прочитать TXT-каталог семейства: заголовки, типы данных, единицы и точные имена строк. rowName возвращает одну точную строку; неоднозначные имена отклоняются.', z.object({ path:file('.txt'), encoding:z.enum(['utf8','utf16','windows1251']).default('utf8'), rowName:z.string().min(1).max(200).optional(), offset:z.number().int().min(0).default(0), limit:z.number().int().min(1).max(200).default(50) }).strict());
  register('preview_build_family', 'Проверить создание/редактирование семейства в скрытом документе из RFA или RFT. Выполнить операции, сохранить временную копию, повторно открыть и проверить результат. Исходник и outputPath не изменяются. Возвращает planId, геометрию, пропуски каталога и результаты проверок.', z.object({ sourcePath:file('.rfa').optional(), templatePath:file('.rft').optional(), outputPath:file('.rfa'), operations:z.array(operations).min(1).max(200), validation:validation.optional() }).strict().refine(v => (v.sourcePath !== undefined) !== (v.templatePath !== undefined), 'Choose one source RFA or RFT.').refine(v=>v.sourcePath?.toLowerCase() !== v.outputPath.toLowerCase(),'Output must differ from source.'),write);
  register('apply_build_family', 'Повторить проверенный план на неизменённых входных файлах и сохранить новый RFA после повторного открытия и проверок. Существующие файлы и исходник не перезаписываются; в проект семейство не загружается.', z.object({planId:name}).strict(),write);
  register('validate_family_file', 'Повторно открыть RFA, проверить типы, значения, габариты и число HVAC-разъёмов. testConnections загружает семейство в скрытый временный проект, проверяет координаты/ориентацию и ConnectTo с воздуховодами; проект закрывается без сохранения. Не меняет открытые проекты.', z.object({path:file('.rfa'), ...validation.shape}).strict(),write);
}
