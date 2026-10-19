import * as z from 'zod/v4';

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
  shape: z.enum(['Round','Rectangular','Oval']), system: z.enum(['SupplyAir','ReturnAir','ExhaustAir','OtherAir']),
  flowDirection: z.enum(['In','Out','Bidirectional']), frame,
  diameterMeters: number.positive().max(10).optional(), widthMeters: number.positive().max(10).optional(), heightMeters: number.positive().max(10).optional(),
  bindings: z.array(binding).max(20).default([]), primary: z.boolean().default(false)
};
const operations = z.discriminatedUnion('action', [
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
  z.object({ action: z.literal('setFamilyShared'), shared: z.boolean() }).strict()
]).superRefine((op, ctx) => {
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
export function registerFamilyTools(register) {
  register('get_family_authoring_options', 'Получить допустимые типы данных, единицы и группы параметров Revit для разработки RFA.', z.object({}).strict());
  register('read_family_file', 'Открыть локальный RFA без окон и вернуть категорию, размещение, все параметры и типы, геометрию, материалы, вложения и полные системы координат HVAC-разъёмов. Документ закрывается без сохранения в finally. Координаты в метрах.', z.object({ path:file('.rfa'), typeNames:z.array(name).max(200).optional(), mesh:z.boolean().default(false), maxTriangles:z.number().int().min(1).max(10000).default(1000) }).strict());
  register('read_family_type_catalog', 'Прочитать TXT-каталог семейства: заголовки, типы данных, единицы и точные имена строк. rowName возвращает одну точную строку; неоднозначные имена отклоняются.', z.object({ path:file('.txt'), encoding:z.enum(['utf8','utf16','windows1251']).default('utf8'), rowName:z.string().min(1).max(200).optional(), offset:z.number().int().min(0).default(0), limit:z.number().int().min(1).max(200).default(50) }).strict());
  register('preview_build_family', 'Проверить создание/редактирование семейства в скрытом документе из RFA или RFT. Выполнить операции, сохранить временную копию, повторно открыть и проверить результат. Исходник и outputPath не изменяются. Возвращает planId, геометрию, пропуски каталога и результаты проверок.', z.object({ sourcePath:file('.rfa').optional(), templatePath:file('.rft').optional(), outputPath:file('.rfa'), operations:z.array(operations).min(1).max(200), validation:validation.optional() }).strict().refine(v => (v.sourcePath !== undefined) !== (v.templatePath !== undefined), 'Choose one source RFA or RFT.').refine(v=>v.sourcePath?.toLowerCase() !== v.outputPath.toLowerCase(),'Output must differ from source.'),write);
  register('apply_build_family', 'Повторить проверенный план на неизменённых входных файлах и сохранить новый RFA после повторного открытия и проверок. Существующие файлы и исходник не перезаписываются; в проект семейство не загружается.', z.object({planId:name}).strict(),write);
  register('validate_family_file', 'Повторно открыть RFA, проверить типы, значения, габариты и число HVAC-разъёмов. testConnections загружает семейство в скрытый временный проект, проверяет координаты/ориентацию и ConnectTo с воздуховодами; проект закрывается без сохранения. Не меняет открытые проекты.', z.object({path:file('.rfa'), ...validation.shape}).strict(),write);
}
