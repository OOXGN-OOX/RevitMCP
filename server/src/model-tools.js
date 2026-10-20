import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const signedId = z.number().int().refine(v => v !== 0 && Number.isSafeInteger(v));
const ids = z.array(id).min(1).max(100).refine(v => new Set(v).size === v.length);
const point = z.object({ x: z.number().finite().min(-100000).max(100000), y: z.number().finite().min(-100000).max(100000), z: z.number().finite().min(-100000).max(100000) }).strict();
const page = { offset: z.number().int().min(0).default(0), limit: z.number().int().min(1).max(500).default(100) };
const write = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const plan = z.object({ planId: z.string().min(1).max(80) }).strict();
const rgb = z.object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) }).strict();
export const parameterFilter = z.object({
  parameterId: signedId.optional(), parameterName: z.string().min(1).max(200).optional(),
  scope: z.enum(['instance', 'type']).default('instance'),
  operator: z.enum(['equals', 'notEquals', 'contains', 'startsWith', 'endsWith', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'hasValue', 'hasNoValue']),
  value: z.union([z.string().max(1000), z.number().finite()]).optional(),
  unitId: z.string().max(200).optional()
}).strict().refine(v => (v.parameterId !== undefined) !== (v.parameterName !== undefined), 'Choose parameterId or parameterName.')
  .refine(v => ['hasValue', 'hasNoValue'].includes(v.operator) || v.value !== undefined, 'Comparison needs a value.');

const filter = z.object({
  fieldId: z.number().int().min(0),
  operator: z.enum(['Equal','NotEqual','GreaterThan','GreaterThanOrEqual','LessThan','LessThanOrEqual','Contains','NotContains','BeginsWith','NotBeginsWith','EndsWith','NotEndsWith','HasValue','HasNoValue']),
  value: z.union([z.string().max(1000), z.number().finite()]).optional(),
  valueType: z.enum(['string','integer','double','elementId']).optional(),
  unitId: z.string().max(200).optional()
}).strict().refine(v => ['HasValue','HasNoValue'].includes(v.operator) || (v.value !== undefined && v.valueType !== undefined));
const sorting = z.object({ fieldId: z.number().int().min(0), order: z.enum(['Ascending','Descending']).default('Ascending'), showHeader: z.boolean().default(false), showFooter: z.boolean().default(false), showBlankLine: z.boolean().default(false) }).strict();

export function registerModelTools(register) {
  const pair = (name, description, schema) => {
    register(`preview_${name}`, `${description} Пробная транзакция с откатом; размеры в метрах.`, schema, write);
    register(`apply_${name}`, `Применить проверенный план: ${description}`, plan, write);
  };
  register('show_elements', 'Приблизить активный вид к указанным видимым элементам. Модель не меняется.', z.object({ elementIds: ids }).strict(), write);
  register('list_views', 'Прочитать все виды, шаблоны и спецификации, включая неразмещённые. Постранично.', z.object({ kind: z.enum(['all','views','templates','schedules']).default('all'), ...page }).strict());
  register('list_sheet_collections', 'Прочитать коллекции листов и их состав. Без collectionId возвращает все коллекции.', z.object({ collectionId: id.optional(), ...page }).strict());
  register('get_room_boundaries', 'Получить контуры помещений; дуги сохраняются как тесселированные точки с указанием исходного типа кривой. Координаты в метрах.', z.object({ elementIds: ids, location: z.enum(['Finish','Center','CoreBoundary','CoreCenter']).default('Finish') }).strict());
  register('get_schedule_definition', 'Прочитать поля, фильтры, сортировку и группировку существующей спецификации, включая неразмещённую.', z.object({ scheduleId: id }).strict());
  register('get_schedulable_fields', 'Получить доступные поля для существующей спецификации или категории. Для категории используется временная спецификация с откатом.', z.object({ scheduleId: id.optional(), categoryId: signedId.optional() }).strict().refine(v => (v.scheduleId !== undefined) !== (v.categoryId !== undefined)), write);
  register('get_fill_and_line_patterns', 'Прочитать штриховки, типы линий и категории с идентификаторами.', z.object({}).strict());
  pair('move_elements', 'Переместить существующие элементы на вектор translation.', z.object({ elementIds: ids, translation: point }).strict());
  pair('tag_elements', 'Создать марки выбранного типа для элементов на виде. Уже промаркированные элементы пропускаются; совместимость категории проверяет Revit.', z.object({
    viewId:id, tagTypeId:id, elementIds:ids, leader:z.boolean().default(false),
    offsetRightMeters:z.number().finite().min(-100).max(100).default(0),
    offsetUpMeters:z.number().finite().min(-100).max(100).default(0)
  }).strict());
  pair('rotate_elements', 'Повернуть существующие элементы вокруг оси axisStart–axisEnd на angleDegrees.', z.object({ elementIds: ids, axisStart: point, axisEnd: point, angleDegrees: z.number().finite().min(-360).max(360) }).strict().refine(v => Math.hypot(v.axisStart.x-v.axisEnd.x,v.axisStart.y-v.axisEnd.y,v.axisStart.z-v.axisEnd.z)>1e-6));
  pair('change_element_types', 'Изменить тип существующих элементов. Возвращает соответствие старых и новых ID, если Revit заменил элемент.', z.object({ elementIds: ids, typeId: id }).strict());
  pair('create_schedule', 'Создать спецификацию выбранной категории и полей. Идентификаторы брать из get_schedulable_fields.', z.object({
    categoryId: signedId, name: z.string().trim().min(1).max(200),
    fields: z.array(z.object({ parameterId: signedId, fieldType: z.string().min(1).max(80), heading: z.string().max(200).optional(), hidden: z.boolean().default(false) }).strict()).min(1).max(100)
  }).strict());
  pair('modify_schedule', 'Изменить фильтры и сортировку/группировку спецификации. Переданный массив заменяет соответствующий список целиком; [] очищает его.', z.object({
    scheduleId: id, filters: z.array(filter).max(8).optional(), sortGroups: z.array(sorting).max(4).optional(),
    isItemized: z.boolean().optional(), showGrandTotal: z.boolean().optional()
  }).strict().refine(v => Object.keys(v).length > 1));
  pair('edit_sheet', 'Переименовать лист, изменить номер или коллекцию. collectionId=null убирает лист из коллекции.', z.object({ sheetId: id, name: z.string().trim().min(1).max(200).optional(), number: z.string().trim().min(1).max(100).optional(), collectionId: id.nullable().optional() }).strict().refine(v => Object.keys(v).length>1));
  pair('override_graphics', 'Настроить графику элементов или категорий вида. Непереданные свойства сохраняются.', z.object({
    viewId: id, elementIds: ids.optional(), categoryIds: z.array(signedId).min(1).max(100).optional(),
    projectionLineColor: rgb.optional(), projectionLineWeight: z.number().int().min(1).max(16).optional(), projectionLinePatternId: signedId.optional(),
    surfaceForegroundPatternId: id.optional(), surfaceForegroundColor: rgb.optional(), cutForegroundPatternId: id.optional(), cutForegroundColor: rgb.optional(),
    transparency: z.number().int().min(0).max(100).optional(), detailLevel: z.enum(['Coarse','Medium','Fine']).optional(), halftone: z.boolean().optional()
  }).strict().refine(v => (v.elementIds !== undefined) !== (v.categoryIds !== undefined))
    .refine(v => Object.keys(v).some(k => !['viewId','elementIds','categoryIds'].includes(k))));
  register('get_annotation_categories', 'Прочитать категории аннотаций на виде и их текущую видимость. Revit 2026 использует временную изоляцию категорий.', z.object({ viewId: id }).strict());
  pair('isolate_annotations', 'Временно изолировать выбранные категории аннотаций на виде. reset=true снимает временную изоляцию/скрытие.', z.object({ viewId: id, categoryIds: z.array(signedId).min(1).max(100).optional(), reset: z.boolean().default(false) }).strict().refine(v => v.reset ? !v.categoryIds : !!v.categoryIds));
  register('export_views', 'Экспортировать виды в PNG/JPG/BMP/TIFF/PDF или спецификации в CSV. Создаёт отдельную папку; файлы не перезаписываются. visibleRegion доступен только для изображений.', z.object({
    directory: z.string().min(1).max(170), format: z.enum(['png','jpg','bmp','tiff','pdf','csv']),
    scope: z.enum(['views','activeView','visibleRegion']).default('views'), viewIds: z.array(id).min(1).max(20).optional(),
    pixelSize: z.number().int().min(500).max(4000).default(2000)
  }).strict().refine(v => v.scope === 'views' ? !!v.viewIds : !v.viewIds).refine(v => v.scope !== 'visibleRegion' || !['pdf','csv'].includes(v.format)), write);
  register('export_model', 'Экспортировать проект в IFC или gbXML в новую папку. Использует текущие настройки модели; энергетическая модель должна быть подготовлена для gbXML. Изменения экспортёра откатываются.', z.object({ directory: z.string().min(1).max(170), format: z.enum(['ifc','gbxml']) }).strict(), write);
}
